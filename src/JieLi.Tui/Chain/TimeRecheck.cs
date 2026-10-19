using System.Globalization;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

/// <summary>
/// 复查模式（全部遍历 / 补录都做完之后跑一遍）：
///
/// 用「时间」复查链条是否完整 —— 相邻两棒的时间差如果明显超过活动本身的棒距，
/// 就说明中间的棒丢了（例如 1、2、3、6、7、8 缺了 4 和 5）。
/// 这时进入补全模式：
///   1. 先站在「缺口的前一棒」上，按它的「下一棒 @对象」向后递推；
///   2. 前一棒没有 @ 对象（或递推不到）时，改站在「缺口的后一棒」上，按「上一棒」向前递推；
///   3. 逐个查，查到接上为止；真查不到就放弃，并把这件事写进缺口清单（不假装补上了）。
///
/// AI 辅助开启时不用另写逻辑：补全复用了 <see cref="ExpandOne"/> →
/// <see cref="ResolveNeighbor"/> 的整条定位链路，AI 兜底本来就在里面。
///
/// 和「数字连续性断言」的分工：棒号只用来展示，缺不缺棒一律按时间判定
/// （与 --at-bar 的约定一致：人工棒号不作缺口位置判据）。
/// </summary>
public sealed partial class ChainWalker
{
    /// <summary>复查模式开关（默认开；CLI 的 --no-recheck 关掉）。</summary>
    public bool RecheckTimeGaps { get; init; } = true;

    /// <summary>最多复查几处断裂（默认 12；每处可能要读若干净化链接）。</summary>
    public int MaxRecheckGaps { get; init; } = 12;

    /// <summary>复查最多补进来几棒（默认 24，防止把请求数打爆）。</summary>
    public int MaxRecheckRepairs { get; init; } = 24;

    /// <summary>单处断裂最多逐层递推几轮。</summary>
    private const int MaxRecheckRounds = 8;

    /// <summary>
    /// 复查期间的「降噪」开关：不新增缺口、不写警告、不让局部失败影响方向状态。
    /// 这些状态在补全结束后由 <see cref="TryRepair"/> 原样恢复。
    /// </summary>
    private bool _inRecheck;

    // ================================================================ 入口

    /// <summary>
    /// 复查时间连续性并尝试补全。放在 InferChainBars 之后、FinishResult 之前；
    /// 补进来的棒会再跑一次 InferChainBars，让「从相邻指认推棒号」也作用到它们身上。
    /// </summary>
    private void RecheckTimeContinuity(WalkContext ctx)
    {
        if (!RecheckTimeGaps)
        {
            Audit(ctx, "复查", "复查模式已关闭（--no-recheck），不检查时间断裂", true);
            return;
        }

        var dayStart = ResolveRecheckDayStart(ctx);
        if (dayStart == DateTime.MinValue)
        {
            Audit(ctx, "复查", "拿不到活动日，跳过复查", false);
            return;
        }

        var ordered = OrderedSlots(ctx, dayStart);
        if (ordered.Count < 2)
        {
            Audit(ctx, "复查", $"能定位到时间的节点只有 {ordered.Count} 个，跳过复查", false);
            return;
        }

        var step = InferStep(ordered);
        var gaps = FindTimeGaps(ordered, step);
        if (gaps.Count == 0)
        {
            Audit(ctx, "复查", $"时间连续：{ordered.Count} 个节点、步长 {step:0} 分钟，没有断裂", true);
            ctx.Log?.Invoke($"[复查] 时间连续：{ordered.Count} 个节点、步长 {step:0} 分钟，没有断裂");
            return;
        }

        ctx.Log?.Invoke($"[复查] 时间断裂 {gaps.Count} 处（步长按 {step:0} 分钟算）—— 进入补全模式");
        Audit(ctx, "复查", $"时间断裂 {gaps.Count} 处：{DescribeGapList(gaps)}", false);

        var repaired = 0;
        var tried = 0;
        var repairedFrom = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gap in gaps)
        {
            if (tried >= MaxRecheckGaps)
            {
                ctx.Result.Warnings.Add($"复查：断裂超过 {MaxRecheckGaps} 处，只处理了前 {MaxRecheckGaps} 处（--recheck-gaps 可调）");
                break;
            }
            if (repaired >= MaxRecheckRepairs)
            {
                ctx.Result.Warnings.Add($"复查：已补 {repaired} 棒，达到上限 {MaxRecheckRepairs}（--recheck-repairs 可调）");
                break;
            }

            tried++;
            var grew = RepairOneGap(ctx, gap, dayStart);
            if (grew > 0)
            {
                repaired += grew;
                if (gap.A.Facts is { } af) repairedFrom.Add(af.PostId);
            }
            else if (gap.Definite)
            {
                RecordGiveUp(ctx, gap);
            }
            else
            {
                ctx.Log?.Invoke($"[复查] 第 {BarText(gap.A)} 棒（{FmtSlot(gap.SlotA)}）→ 第 {BarText(gap.B)} 棒（{FmtSlot(gap.SlotB)}）：时间差偏大但没有「缺棒」证据，只提示不补");
                ctx.Result.Warnings.Add($"复查：第 {BarText(gap.A)} 棒（{FmtSlot(gap.SlotA)}）与第 {BarText(gap.B)} 棒（{FmtSlot(gap.SlotB)}）之间时间差偏大，但两边都没有指向缺失棒的 @ 证据，未补");
            }
        }

        if (repaired > 0)
        {
            // 补进来的节点可能因此有了棒号，也可能让「相邻指认」能推棒号了。
            InferChainBars(ctx);
            DropStaleTimeGaps(ctx, repairedFrom);
            ctx.Log?.Invoke($"[复查] 补入 {repaired} 棒；现在共 {ctx.Result.Nodes.Count} 个节点");
            ctx.Result.Warnings.Add($"复查模式：按时间发现链条断裂，已自动补入 {repaired} 棒（补进来的棒请顺便核对棒号）");
        }

        Audit(ctx, "复查", repaired > 0 ? $"补入 {repaired} 棒" : "没有补到任何棒（已放弃）", repaired > 0);
    }

    // ================================================================ 断裂检测

    private static DateTime ResolveRecheckDayStart(WalkContext ctx)
    {
        if (ctx.Query.DayStart != DateTime.MinValue) return ctx.Query.DayStart;
        foreach (var node in ctx.Result.Nodes)
        {
            if (node.IsStart && node.Facts?.PubLocal is { } pub && pub != DateTime.MinValue) return pub.Date;
        }
        var first = ctx.Result.Nodes.FirstOrDefault(n => n.Facts?.PubLocal is { } p && p != DateTime.MinValue);
        return first?.Facts?.PubLocal.Date ?? DateTime.MinValue;
    }

    /// <summary>按「棒位时间」排好序的节点（EX、拿不到时间的、重复槽位的都不参与）。</summary>
    private static List<(ChainNode Node, DateTime Slot)> OrderedSlots(WalkContext ctx, DateTime dayStart)
    {
        var all = new List<(ChainNode Node, DateTime Slot)>();
        foreach (var node in ctx.Result.Nodes)
        {
            if (node.Facts is not { } facts) continue;
            if (facts.IsEx) continue;
            var slot = NodeSlot(facts, dayStart);
            if (slot == DateTime.MinValue) continue;
            all.Add((node, slot));
        }

        all.Sort((x, y) =>
        {
            var bySlot = x.Slot.CompareTo(y.Slot);
            return bySlot != 0 ? bySlot : x.Node.Facts!.PubTs.CompareTo(y.Node.Facts!.PubTs);
        });

        var unique = new List<(ChainNode Node, DateTime Slot)>(all.Count);
        foreach (var item in all)
        {
            if (unique.Count > 0 && unique[^1].Slot == item.Slot) continue; // 同槽位多节点：只留一个参与判定
            unique.Add(item);
        }
        return unique;
    }

    /// <summary>
    /// 棒位时间：优先用帖里声明的时段（17:00 / 第4棒 3:00），
    /// 没写声明时段就退回发布时间（对齐到半点，容忍发布延迟）。
    /// </summary>
    private static DateTime NodeSlot(PostFacts facts, DateTime dayStart)
    {
        var declared = SlotOf(facts, dayStart);
        if (declared != DateTime.MinValue) return declared;
        var pub = facts.PubLocal;
        return pub == DateTime.MinValue ? DateTime.MinValue : RoundToHalfHour(pub);
    }

    /// <summary>从相邻间隔里猜活动本身的棒距（30 分钟 / 60 分钟），用于判断「差多少算缺棒」。</summary>
    private static double InferStep(List<(ChainNode Node, DateTime Slot)> ordered)
    {
        var deltas = new List<int>();
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var minutes = (int)Math.Round((ordered[i + 1].Slot - ordered[i].Slot).TotalMinutes);
            if (minutes >= 5 && minutes <= 240) deltas.Add(minutes);
        }
        if (deltas.Count == 0) return 60;

        var mode = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        if (Math.Abs(mode - 30) <= 10) return 30;
        if (Math.Abs(mode - 60) <= 10) return 60;
        return Math.Max(15, deltas.Min());
    }

    private static List<TimeGap> FindTimeGaps(List<(ChainNode Node, DateTime Slot)> ordered, double step)
    {
        var gaps = new List<TimeGap>();
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];
            var minutes = (b.Slot - a.Slot).TotalMinutes;
            // 阈值下限 90 分钟：30 分钟制里「差 1 小时」常常只是整点位与半点位混排（实测这场活动就这样），
            // 真缺棒的 1 小时空档会由下面的「名字/棒号」证据单独认出来，不靠时间差硬判。
            if (minutes < Math.Max(step * 1.5, 90)) continue;

            var aFacts = a.Node.Facts!;
            var bFacts = b.Node.Facts!;
            var aBar = aFacts.BarNumber;
            var bBar = bFacts.BarNumber;
            var barHole = aBar is { } x && bBar is { } y && y - x > 1;
            var expected = Math.Max(0, (int)Math.Round(minutes / step) - 1);
            var hole = aBar is { } xa && bBar is { } xb ? Math.Max(0, xb - xa - 1) : 0;

            // 名字级证据：前一棒写的「下一棒」/ 后一棒写的「上一棒」若不是彼此，说明中间还夹着人。
            var nextName = aFacts.Nexts.FirstOrDefault()?.Name;
            var prevName = bFacts.Prevs.FirstOrDefault()?.Name;
            var nameGap = (IsNamed(nextName) && !SamePerson(nextName, bFacts.AuthorName))
                       || (IsNamed(prevName) && !SamePerson(prevName, aFacts.AuthorName));

            // 只认两种断裂：① 相邻两棒的 @ 指认对不上（中间还有人）；② 棒号洞比时段能容纳的还多。
            // 为什么不能见「差 1 小时」就判缺棒：同一场接力里 30 分钟位与整点位常常并存
            // （实测这场活动 12:00–15:00 是半点制、其余是整点制），只看时间差会把整点位全当成缺棒。
            if (!(nameGap || barHole)) continue;
            var missing = Math.Max(1, Math.Max(hole, expected));
            gaps.Add(new TimeGap(a.Node, b.Node, a.Slot, b.Slot, missing, true, barHole));
        }
        return gaps;
    }

    private sealed record TimeGap(ChainNode A, ChainNode B, DateTime SlotA, DateTime SlotB, int Missing, bool Definite, bool BarHole);

    /// <summary>名字对不上就算两个不同的人（简繁/异体已折叠）。</summary>
    private static bool SamePerson(string? a, string? b)
        => IsNamed(a) && IsNamed(b) && TextNormalizer.SameName(a!, b!);

    private static bool IsNamed(string? name) => !string.IsNullOrWhiteSpace(name);

    private static string DescribeGapList(List<TimeGap> gaps)
    {
        var parts = new List<string>(gaps.Count);
        foreach (var gap in gaps.Take(6))
            parts.Add($"{FmtSlot(gap.SlotA)}→{FmtSlot(gap.SlotB)} 缺 {gap.Missing} 棒");
        if (gaps.Count > 6) parts.Add($"…等 {gaps.Count} 处");
        return string.Join("；", parts);
    }

    // ================================================================ 补全

    private int RepairOneGap(WalkContext ctx, TimeGap gap, DateTime dayStart)
    {
        ctx.Log?.Invoke($"[复查] 第 {BarText(gap.A)} 棒（{FmtSlot(gap.SlotA)}）→ 第 {BarText(gap.B)} 棒（{FmtSlot(gap.SlotB)}）：疑似缺 {gap.Missing} 棒（时间差 {(gap.SlotB - gap.SlotA).TotalMinutes:0} 分钟）");

        var before = ctx.Result.Nodes.Count;
        var added = TryRepair(ctx, gap.A, gap.B, gap.SlotB, WalkDirection.Forward, dayStart);
        if (added == 0)
        {
            if ((gap.A.Facts?.Nexts.Count ?? 0) == 0)
                ctx.Log?.Invoke("[复查] 缺口前一棒没有「下一棒」@ 对象 → 改从缺口后一棒向前递推");
            else
                ctx.Log?.Invoke("[复查] 从缺口前一棒向后递推没补到 → 改从缺口后一棒向前递推");
            added = TryRepair(ctx, gap.B, gap.A, gap.SlotA, WalkDirection.Backward, dayStart);
        }

        var grew = ctx.Result.Nodes.Count - before;
        if (grew <= 0)
        {
            ctx.Log?.Invoke("[复查] 两个方向都没补到棒");
            return 0;
        }

        ctx.Log?.Invoke($"[复查] 补入 {grew} 棒（{(added > 0 ? "前向 / 后向递推" : "递推")}）");
        return grew;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 出发，沿 <paramref name="dir"/> 逐层递推，直到槽位越过
    /// <paramref name="boundarySlot"/>（接上缺口的另一头）为止。返回是否补到了棒。
    /// </summary>
    private int TryRepair(WalkContext ctx, ChainNode from, ChainNode boundary, DateTime boundarySlot, WalkDirection dir, DateTime dayStart)
    {
        if (from.Facts is not { } fromFacts) return 0;
        var links = dir == WalkDirection.Forward ? fromFacts.Nexts : fromFacts.Prevs;
        if (links.Count == 0) return 0;

        var state = ctx.StateOf(dir);
        var savedStopped = state.Stopped;
        var savedReason = state.StopReason;
        var savedFailures = state.ConsecutiveFailures;
        var ghostKey = HideBoundary(ctx, boundary);
        var added = new List<ChainNode>();

        _inRecheck = true;
        try
        {
            state.Stopped = false;
            state.StopReason = null;
            state.ConsecutiveFailures = 0;

            var frontier = new List<ChainNode> { from };
            for (var round = 0; round < MaxRecheckRounds && frontier.Count > 0; round++)
            {
                var next = new List<ChainNode>();
                foreach (var current in frontier)
                {
                    if (added.Count >= MaxRecheckRepairs) break;
                    if (ctx.Result.Nodes.Count >= MaxNodes) break;

                    ctx.Queue.Clear();
                    ExpandOne(ctx, current, dir);
                    while (ctx.Queue.Count > 0)
                    {
                        var expansion = ctx.Queue.Dequeue();
                        if (!Covers(expansion.Directions, dir)) continue;
                        var node = expansion.Node;
                        if (node.Facts is null) continue;
                        if (!ReferenceEquals(node, from) && !added.Contains(node)) added.Add(node);

                        var slot = NodeSlot(node.Facts, dayStart);
                        var reached = slot != DateTime.MinValue &&
                                      (dir == WalkDirection.Forward ? slot >= boundarySlot : slot <= boundarySlot);
                        if (!reached) next.Add(node);
                    }
                }
                frontier = next;
            }
        }
        finally
        {
            _inRecheck = false;
            state.Stopped = savedStopped;
            state.StopReason = savedReason;
            state.ConsecutiveFailures = savedFailures;
            RestoreBoundary(ctx, ghostKey);
            ctx.Queue.Clear();
        }

        if (added.Count == 0) return 0;

        var label = dir == WalkDirection.Forward ? "下一棒" : "上一棒";
        foreach (var node in added)
        {
            // 复查补进来的棒：写清来源，交人工复核棒号。
            node.Notes.Add($"复查补全：时间断裂处按「{label}」@ 递推补回（请核对棒号与时段）");
        }
        return added.Count;
    }

    /// <summary>记录「补不到」：如实写进缺口清单，并给一条人能执行的提示。</summary>
    private static void RecordGiveUp(WalkContext ctx, TimeGap gap)
    {
        var fromFacts = gap.A.Facts!;
        var nextName = fromFacts.Nexts.FirstOrDefault()?.Name;
        var prevName = gap.B.Facts?.Prevs.FirstOrDefault()?.Name;
        var expected = nextName ?? prevName;
        var tried = new List<string> { "复查：按时间找出断裂", "从缺口前一棒按「下一棒」@ 递推" };
        if (nextName is null) tried.Add("前一棒没有「下一棒」@，改从后一棒按「上一棒」递推");
        if (nextName is null && prevName is null) tried.Add("两侧都没有 @ 对象可递推");

        ctx.Result.Gaps.Add(new GapItem
        {
            Kind = "time",
            Reason = $"复查：第 {BarText(gap.A)} 棒（{FmtSlot(gap.SlotA)}）与第 {BarText(gap.B)} 棒（{FmtSlot(gap.SlotB)}）之间约缺 {gap.Missing} 棒，" +
                     "前向 / 后向递推都没能补全（已放弃）",
            FromPostId = fromFacts.PostId,
            FromPostUrl = fromFacts.Url,
            Direction = WalkDirection.Forward,
            ExpectedName = expected,
            ExpectedBar = gap.BarHole && fromFacts.BarNumber is { } nb ? (nb + 1).ToString(CultureInfo.InvariantCulture) : null,
            ExpectedTime = SlotGrid.FormatMinutes((int)(gap.SlotA.TimeOfDay.TotalMinutes + 30) % 1440),
            TriedMethods = { },
            SuggestedCommand = "--add-link <缺失那一棒的帖子链接> --into <slug|topic_id> [--at-bar N]",
        });
        ctx.Result.Warnings.Add($"复查：第 {BarText(gap.A)} 棒（{FmtSlot(gap.SlotA)}）与第 {BarText(gap.B)} 棒（{FmtSlot(gap.SlotB)}）之间约缺 {gap.Missing} 棒，自动补全失败（已放弃，见缺口清单）");
        ctx.Log?.Invoke($"[复查] 放弃：第 {BarText(gap.A)} 棒 → 第 {BarText(gap.B)} 棒 之间缺 {gap.Missing} 棒，两个方向都没补到");
    }

    /// <summary>
    /// 补上之后，之前按「时间断裂」写下的缺口可能已经被覆盖：把起点是我们刚补过的那一棒的
    /// time 缺口清掉（真正的「没写 @」缺口不在清理范围内）。
    /// </summary>
    private static void DropStaleTimeGaps(WalkContext ctx, HashSet<string> repairedFrom)
    {
        if (ctx.Result.Gaps.Count == 0 || repairedFrom.Count == 0) return;
        var removed = ctx.Result.Gaps.RemoveAll(g =>
            string.Equals(g.Kind, "time", StringComparison.Ordinal) &&
            g.FromPostId is { Length: > 0 } id && repairedFrom.Contains(id));
        if (removed > 0) ctx.Log?.Invoke($"[复查] 清掉 {removed} 处已被补上的时间缺口记录");
    }

    // ================================================================ 小工具

    private static string BarText(ChainNode node) =>
        node.Facts?.BarNumber?.ToString(CultureInfo.InvariantCulture) ?? "?";

    /// <summary>把边界节点临时伪装成「入口节点」：这样碰到它时是 Skip（静默跳过）而不是 Meeting（两向相遇、停双向）。</summary>
    private static string? HideBoundary(WalkContext ctx, ChainNode boundary)
    {
        if (boundary.Facts is null) return null;
        var key = KeyOf(boundary.Facts, boundary.Ref);
        if (!ctx.Visited.TryGetValue(key, out var original)) return null;

        var ghost = new ChainNode
        {
            Ref = original.Ref,
            Facts = original.Facts,
            Direction = original.Direction,
            Source = original.Source,
            Depth = original.Depth,
            Confidence = original.Confidence,
            IsStart = true,
            IsSupplement = original.IsSupplement,
            MatchedBy = original.MatchedBy,
        };
        ghost.Notes.AddRange(original.Notes);
        ctx.Visited[key] = ghost;
        return key;
    }

    private static void RestoreBoundary(WalkContext ctx, string? key)
    {
        if (key is null) return;
        foreach (var node in ctx.Result.Nodes)
        {
            if (node.Facts is null) continue;
            if (string.Equals(KeyOf(node.Facts, node.Ref), key, StringComparison.Ordinal))
            {
                ctx.Visited[key] = node;
                return;
            }
        }
        ctx.Visited.Remove(key);
    }
}
