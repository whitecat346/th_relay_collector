#nullable enable
using System.Globalization;
using System.Text.RegularExpressions;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」（与 ChainWalker.cs 同属 ChainWalker 分部类）
//
// 职责：遍历收尾的「按话题时间表补全」。
//
// 为什么需要它（实测证据，2026 永远的巫女就任灵梦接力）：
//   @ 链本身会断 —— 13:30 那棒正文只有一句「第一次真正意义上写灵梦」，没写「上一棒」；
//   13:00 那棒也没写「下一棒」。工具只靠 @ 递推时 backward 直接停在 13:30，
//   00:00–13:00 的 16 棒全部漏掉。人工整理能补齐，是因为话题页按发布时间列出了每一棒。
//   所以这里改用「话题卡片时间表」做第二证据源，把 @ 链断开处按时段接上。
//
// 规则（宁可少补也不补错）：
//   * 只补「话题里存在、但链上没有」的卡片；
//   * 只有当卡片声明的时段落在「已有棒次的时段空隙」里（且离任何已有棒次 ≥ SlotToleranceMinutes）才入链，
//     避免把「同一棒的另一稿」「22:08 这种附加帖」当成新棒次；
//   * 补进来的节点置信度 0.6（时间表推断，不是 @ 结构证据），并在 Note 里写清来源；
//   * 显式 EX/SP/约稿/特典 → 标 IsEx，不参与棒号编号，单独进报告的 ex 棒段。
// ===========================================================================

public sealed partial class ChainWalker
{
    /// <summary>收尾补全最多再读多少条帖子的正文（每条 1–2 请求；上限防打爆风控）。</summary>
    /// <remarks>实测本场活动话题里 46 张卡，全读一遍才不漏棒次，所以默认给到 60。</remarks>
    public int MaxTopicBodyReads { get; init; } = 60;

    /// <summary>补进来的棒次，声明时段与已有棒次至少差这么多分钟才算「空隙里的一棒」。</summary>
    private const int SlotToleranceMinutes = 25;

    private const double TopicCompletionConfidence = 0.6;

    private const string CompletionNote = "收尾按话题卡片时间表补齐（@ 链在这段断开；时段来自帖内声明/卡片时间表，请核对）";

    /// <summary>
    /// 用话题卡片时间表补全链路，然后重排棒号、插值缺失时段、产出缺口。
    /// 话题不存在（视频/单帖入口常见）时安静跳过。
    /// </summary>
    private void CompleteFromTopic(WalkContext ctx)
    {
        var result = ctx.Result;
        if (ctx.Query.TopicId is null or <= 0)
        {
            Audit(ctx, "话题补全", "本次没有话题 id（入口帖没挂话题），跳过按时间表补全", true);
            return;
        }

        TopicIndex? topic;
        try
        {
            topic = _locator.EnsureTopic(ctx.Log, ctx.Ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Audit(ctx, "话题补全", $"取话题卡片失败：{ex.Message}", false);
            return;
        }

        if (topic is null || !topic.IsBuilt)
        {
            Audit(ctx, "话题补全", "话题卡片索引为空（接口失败/话题无卡片），跳过", false);
            return;
        }

        var startNode = result.Nodes.FirstOrDefault(n => n.IsStart);
        var startPub = startNode?.Facts?.PubLocal ?? DateTime.MinValue;
        var dayStart = ctx.Query.DayStart != DateTime.MinValue ? ctx.Query.DayStart : startPub.Date;

        // ---- 1. 已有节点与已有棒次时段 ----
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in result.Nodes)
        {
            if (!string.IsNullOrWhiteSpace(n.Ref.Id)) known.Add(n.Ref.Id);
            if (n.Facts is not null && !string.IsNullOrWhiteSpace(n.Facts.PostId)) known.Add(n.Facts.PostId);
        }

        var existingSlots = new List<DateTime>();
        var slotOwner = new Dictionary<DateTime, ChainNode>();
        foreach (var n in result.Nodes)
        {
            if (n.Facts is null) continue;
            var s = SlotOf(n.Facts, dayStart);
            if (s == DateTime.MinValue) continue;
            existingSlots.Add(s);
            slotOwner[s] = n;
        }
        existingSlots.Sort();

        // ---- 2. 挑要读的卡片：话题里存在、链上没有 ----
        // 先做**年份闸门**：同一个话题每年复用（「7月26日卡娜日」这类年度活动），话题卡片里
        // 混着 2024/2025 往年那几场。它们写着同样的钟点，只看声明时段会被当成今年的棒次
        // （实测 2026 卡娜日补进来 2024、2025 各十几棒）。发布时间的绝对范围兜底：
        // 只保留活动当天前后 3 天内的卡（实测提前发布最多 1 天）。
        var sameRelay = new List<TopicCardEntry>(topic.Cards.Count);
        var offYear = 0;
        foreach (var card in topic.Cards)
        {
            if (IsNearRelayDay(card, dayStart)) sameRelay.Add(card);
            else if (!known.Contains(card.Id)) offYear++;
        }
        if (offYear > 0)
        {
            ctx.Log?.Invoke($"[话题补全] 话题「{topic.Name}」里有 {offYear} 张卡不属于 {dayStart:yyyy-MM-dd} 这一场"
                            + "（同话题往年的帖子），已跳过");
            result.Warnings.Add($"话题「{topic.Name}」里有 {offYear} 张往年（非本次接力日期）的帖子，已跳过不入链。");
        }

        // 刻意不按发布时间窗口过滤：实测有棒次提前一天占位（15:30 那棒 10-07 14:57 就发了），
        // 一旦用 PubTs 卡当天窗口就会把它整条漏掉。上面的「±3 天」已经拦住往年，够用。
        //
        // 但要省请求：卡面**没写时段**、且发布时间四舍五入到半小时后已经落在「链上已有的时段」上的，
        // 基本就是同一棒的另一稿（活动里一人多发的文章版/动态版/视频版），不必再读正文。
        // 实测 46 张卡里这一类有十几张，省下来能把总请求数压下去（用户明确担心风控）。
        var pending = sameRelay
            .Where(c => !known.Contains(c.Id))
            .Where(c => !IsAnnouncementPost(c, out _))       // 「一共N棒+5EX棒」「接力名单」这类公告帖不是棒次
            .Where(c => !SkipByPubSlot(c, existingSlots, dayStart, slotOwner))
            .OrderByDescending(c => MatchVerifier.TrySlotAtLineStart(c.Text, out _)) // 卡面就写了时段的优先（信息量最大）
            .ThenBy(c => c.PubTs)
            .Take(MaxTopicBodyReads)
            .ToList();

        if (pending.Count == 0)
        {
            Audit(ctx, "话题补全", $"话题卡片 {topic.Cards.Count} 张都已在链上，无需补全", true);
            return;
        }

        ctx.Log?.Invoke($"[话题补全] 链上 {result.Nodes.Count} 棒，话题卡片 {topic.Cards.Count} 张"
                        + $"（本场 {sameRelay.Count} 张，跳过往年 {offYear} 张），"
                        + $"其中 {pending.Count} 张未入链 —— 逐张读正文按时段接上（上限 {MaxTopicBodyReads}）");

        // ---- 3. 读正文，挑出「像一棒」的卡片 ----
        var inserted = 0;
        var extras = new List<string>();
        var readCount = 0;
        foreach (var card in pending)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var reference = CardToRef(card);
            var facts = ReadFactsCached(ctx, reference, WalkDirection.None, FetchMethod.TopicCards, out var readError);
            readCount++;
            if (facts is null)
            {
                extras.Add($"「{ShortName(card)}」读取失败：{readError ?? "未知原因"}");
                continue;
            }
            if (known.Contains(facts.PostId)) continue; // 同一帖换了 id（视频卡给 bvid，读回来是作品号）
            known.Add(facts.PostId);

            if (IsAnnouncementText(facts.MergedText, out var announceWhy))
            {
                extras.Add($"「{ShortName(card)}」像是主办方的公告/汇总帖（命中「{announceWhy}」），未当棒次或附加帖");
                continue;
            }

            var slot = SlotOf(facts, dayStart);
            var exKind = ClassifyEx(facts, card);

            if (exKind == ExKind.Strong)
            {
                AddTopicNode(ctx, reference, facts, card, isEx: true, note: "收尾按话题卡片识别为附加帖（约稿/特典/EX 标记），单独列出、不参与棒号");
                inserted++;
                continue;
            }

            if (exKind == ExKind.Weak)
            {
                // 只带 SP 标记：本场活动有 5 张，人工只收 1 张当 EX，所以只提示、不替人决定
                extras.Add($"「{ShortName(card)}」带「SP」标记，可能是附加帖（EX），未自动列入；如确认为 ex 棒请在 TUI 里补");
                continue;
            }

            // 帖里没写时段时的兜底：用发布时间四舍五入到半小时。
            // 为什么需要：实测第 13 棒（11:00 竹取上川）正文只写了「夏天要结束了呢 / 上一棒：@BP-0728 /
            // 下一棒：@妥托若」，通篇没有钟点，只能靠发布时间（11:0x）归位；
            // 人工整理时也是这么定的。为防止把杂物当棒次，只对「有上一棒/下一棒或棒字」的帖子启用。
            if (slot == DateTime.MinValue && LooksLikeBar(facts))
            {
                var rounded = RoundToHalfHour(facts.PubLocal);
                if (rounded != DateTime.MinValue
                    && rounded >= dayStart.Date.AddHours(-6)
                    && rounded <= dayStart.Date.AddHours(30))
                {
                    slot = rounded;
                    facts.DeclaredTime = $"{rounded.Hour}:{rounded.Minute:00}";
                    facts.FieldSources["DeclaredTime"] = "按发布时间四舍五入到半小时（帖里没写时段）";
                }
            }

            if (slot == DateTime.MinValue)
            {
                extras.Add($"「{ShortName(card)}」没声明时段，无法定位到棒次（已读正文，可在报告里人工核对）");
                continue;
            }

            if (CloseToExisting(existingSlots, slot))
            {
                // 与已有棒次时段重叠：多半是「同一棒的另一稿」（文章版/动态版/视频版），不进链，只提示。
                // 但有一种情况必须换掉：链上那张是**空正文的视频卡**，而这张有正文/有上下棒。
                // 实测第 22 棒（21:30）：@ 链把「同人视频 BV1xTHS6MEET」当成那一棒收下，
                // 真正的棒次帖（动态，写了「21:30 @16sanrach 上一棒…下一棒…」）反而被判成重叠丢掉了。
                var owner = OwnerOfSlot(slotOwner, slot);
                if (owner is not null && IsThinner(owner.Facts, facts))
                {
                    var oldKind = owner.Facts!.Kind;
                    owner.Ref = reference;
                    owner.Facts = facts;
                    owner.Source = FetchMethod.TopicCards;
                    owner.Confidence = TopicCompletionConfidence;
                    owner.MatchedBy = "话题卡片·同一棒的另一稿（改用带正文的那张）";
                    owner.Notes.Add($"同一棒有多个帖子，改用有正文的那张（原为 {oldKind} 的空帖）");
                    owner.Notes.Add(CompletionNote);
                    inserted++;
                    continue;
                }

                extras.Add($"「{ShortName(card)}」声明 {FmtSlot(slot)}，与已有棒次时段重叠（可能是同一棒的另一稿），未自动采用");
                continue;
            }

            AddTopicNode(ctx, reference, facts, card, isEx: false, note: CompletionNote);
            existingSlots.Add(slot);
            existingSlots.Sort();
            inserted++;
        }

        ctx.Log?.Invoke($"[话题补全] 读完 {readCount} 张卡，按时间表补入 {inserted} 棒");

        // ---- 4. 重排：插值缺失时段 + 编号 + 缺口 ----
        RenumberBySlots(ctx, dayStart);

        foreach (var extra in extras.Take(12))
            result.Warnings.Add("话题补全：" + extra);

        Audit(ctx, "话题补全",
            $"话题卡片 {topic.Cards.Count} 张，读 {readCount} 张，补入 {inserted} 棒；"
            + $"链路现有 {result.Nodes.Count} 棒", inserted > 0);
    }

    // ================================================================ 重排/编号/缺口

    private void RenumberBySlots(WalkContext ctx, DateTime dayStart)
    {
        var result = ctx.Result;
        var bars = result.Nodes.Where(n => n.Facts is not null).ToList();
        if (bars.Count == 0) return;

        // 排序键：有声明时段用时段（绝对时间），没有就用发布时间（同一活动当天发布顺序≈棒次顺序）
        static DateTime SortKey(ChainNode n, DateTime dayStart)
        {
            var s = SlotOf(n.Facts!, dayStart);
            return s != DateTime.MinValue ? s : (n.Facts!.PubLocal == DateTime.MinValue ? dayStart : n.Facts!.PubLocal);
        }

        var ordered = bars.OrderBy(n => SortKey(n, dayStart)).ThenBy(n => n.Facts!.PubTs).ToList();
        var exNodes = ordered.Where(n => n.Facts!.IsEx).ToList();
        var numbered = ordered.Where(n => !n.Facts!.IsEx).ToList();

        // 缺失时段插值：在前后已知时段之间按「发布时间占比」估一个时段写回去，
        // 这样报告里不会出现「声明时段：—」；同时标清是插值，别让人当成原文。
        for (var i = 0; i < numbered.Count; i++)
        {
            var facts = numbered[i].Facts!;
            if (SlotOf(facts, dayStart) != DateTime.MinValue) continue;

            var prevIdx = i - 1;
            while (prevIdx >= 0 && SlotOf(numbered[prevIdx].Facts!, dayStart) == DateTime.MinValue) prevIdx--;
            var nextIdx = i + 1;
            while (nextIdx < numbered.Count && SlotOf(numbered[nextIdx].Facts!, dayStart) == DateTime.MinValue) nextIdx++;

            DateTime slot;
            if (prevIdx >= 0 && nextIdx < numbered.Count)
            {
                var a = SlotOf(numbered[prevIdx].Facts!, dayStart);
                var b = SlotOf(numbered[nextIdx].Facts!, dayStart);
                var gapCount = nextIdx - prevIdx;                 // 中间隔了几棒（含自己）
                var step = (b - a).TotalMinutes / gapCount;
                slot = a.AddMinutes(step * (i - prevIdx));
            }
            else if (prevIdx >= 0)
            {
                // 没有后一个已知时段：按 30 分钟一棒推（30/60 分钟接力的最常见节奏）
                var prevSlot = SlotOf(numbered[prevIdx].Facts!, dayStart);
                slot = prevSlot.AddMinutes(30.0 * (i - prevIdx));
            }
            else if (nextIdx < numbered.Count)
            {
                var nextSlot = SlotOf(numbered[nextIdx].Facts!, dayStart);
                slot = nextSlot.AddMinutes(-30.0 * (nextIdx - i));
            }
            else
            {
                continue;
            }

            facts.DeclaredTime = $"{slot.Hour:00}:{slot.Minute:00}";
            facts.FieldSources["DeclaredTime"] = "收尾插值（前后相邻棒次的时段）";
            numbered[i].Notes.Add($"声明时段缺失，按前后相邻棒次插值为 {facts.DeclaredTime}（请核对）");
        }

        // 编号：以「最近的帖内显式棒号」为锚点、按时段差 ÷ 步长换算。
        // 不能再用「列表下标 + 1」——中间缺一棒时后面全体错位，还会和显式编号撞车
        // （实测「7月26日卡娜日」：2:00 那棒缺，9:00 那棒本该第 10 却被推算成第 9，与 8:00 的第 9 撞号）。
        var slotStep = 30.0;
        var diffs = new List<double>();
        for (var i = 0; i + 1 < numbered.Count; i++)
        {
            var sa = SlotOf(numbered[i].Facts!, dayStart);
            var sb = SlotOf(numbered[i + 1].Facts!, dayStart);
            if (sa == DateTime.MinValue || sb == DateTime.MinValue) continue;
            var d = (sb - sa).TotalMinutes;
            if (d >= 15 && d <= 180) diffs.Add(d);
        }
        if (diffs.Count > 0) slotStep = diffs.Min();

        var explicitConflict = 0;
        var used = new HashSet<int>();
        // 先占住显式棒号的号位（声明优先，显式值永远不动 —— 用户定的规则）
        foreach (var n in numbered)
        {
            if (n.Facts!.BarNumber is { } declaredBar && SlotOf(n.Facts!, dayStart) != DateTime.MinValue)
                used.Add(declaredBar);
        }

        for (var i = 0; i < numbered.Count; i++)
        {
            var node = numbered[i];
            var facts = node.Facts!;
            var slot = SlotOf(facts, dayStart);
            int? explicitBar = facts.BarNumber;

            int guess;
            var anchor = NearestExplicitBar(numbered, i, dayStart);
            if (anchor is { } a && slot != DateTime.MinValue)
                guess = a.Bar + (int)Math.Round((slot - a.Slot).TotalMinutes / slotStep);
            else if (slot != DateTime.MinValue)
            {
                var firstSlot = SlotOf(numbered[0].Facts!, dayStart);
                guess = firstSlot == DateTime.MinValue
                    ? i + 1
                    : 1 + (int)Math.Round((slot - firstSlot).TotalMinutes / slotStep);
            }
            else
            {
                guess = i + 1; // 既没时段也没锚点：退回排序位置
            }

            if (explicitBar is { } declaredBar)
            {
                if (declaredBar != guess)
                {
                    explicitConflict++;
                    node.Notes.Add($"帖内声明第 {declaredBar} 棒，但按时段/时间排序是第 {guess} 棒（保留声明的 {declaredBar}，请核对是否漏棒）");
                }
                continue;
            }

            if (used.Contains(guess))
            {
                // 号位被别的棒次占了（同一时段两帖、或推算偏差）：顺延到最近的空号并留痕
                var shift = 1;
                while (used.Contains(guess + shift) && used.Contains(guess - shift)) shift++;
                var candidate = used.Contains(guess + shift) ? guess - shift : guess + shift;
                node.Notes.Add($"按时段推算应为第 {guess} 棒，但该棒号已有帖（可能是同一棒的另一稿），改记第 {candidate} 棒，请核对");
                guess = candidate;
            }

            used.Add(guess);
            facts.BarNumber = guess;
            facts.BarRaw ??= guess.ToString(CultureInfo.InvariantCulture);
            if (facts.FieldSources.GetValueOrDefault("BarNumber") is null)
                facts.FieldSources["BarNumber"] = "按声明时段推算（以最近的帖内棒号为锚点）";
        }

        result.Nodes.Sort((a, b) => SortKey(a, dayStart).CompareTo(SortKey(b, dayStart)));

        // 缺口：相邻两棒时段相隔超过时间窗，多半漏了一棒
        var window = ctx.Query.WindowMinutes > 0 ? ctx.Query.WindowMinutes : 90;
        for (var i = 0; i + 1 < numbered.Count; i++)
        {
            var a = numbered[i];
            var b = numbered[i + 1];
            var sa = SlotOf(a.Facts!, dayStart);
            var sb = SlotOf(b.Facts!, dayStart);
            if (sa == DateTime.MinValue || sb == DateTime.MinValue) continue;
            var minutes = (sb - sa).TotalMinutes;
            if (minutes <= window) continue;
            result.Gaps.Add(new GapItem
            {
                Kind = "time",
                Reason = $"第 {a.Facts!.BarNumber} 棒（{FmtSlot(sa)}）与第 {b.Facts!.BarNumber} 棒（{FmtSlot(sb)}）相隔 {minutes:0} 分钟，"
                         + "中间疑似还有棒次没找到（话题卡片里也没有对应帖子时，需人工补链接）",
                FromPostId = a.Facts!.PostId,
                FromPostUrl = a.Facts!.Url,
                Direction = WalkDirection.None,
                ExpectedName = null,
            });
        }

        Audit(ctx, "时段/棒号重排",
            $"按声明时段重排 {numbered.Count} 棒（EX {exNodes.Count} 条单列）；"
            + (explicitConflict > 0 ? $"{explicitConflict} 处帖内棒号与排序冲突（已保留声明值）" : "帖内棒号与排序一致"),
            true);
    }

    /// <summary>
    /// 公告帖识别：主办方「一共 24 棒 + 5 EX 棒」「接力名单」「圆满结束，我写个专栏整合」这类帖子
    /// 会出现在同一个话题里，带「棒」字但不属于任何棒次；以前它们会被当成 EX 棒收进报告。
    /// </summary>
    private static bool IsAnnouncementPost(TopicCardEntry card, out string why) => IsAnnouncementText(card.Text, out why);

    private static bool IsAnnouncementText(string? text, out string why)
    {
        why = string.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var pattern in AnnouncementPatterns)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            why = m.Value.Length > 40 ? m.Value[..40] : m.Value;
            return true;
        }
        return false;
    }

    private static readonly Regex[] AnnouncementPatterns =
    [
        new(@"一共\s*\d+\s*棒", RegexOptions.Compiled),
        new(@"共\s*\d+\s*棒", RegexOptions.Compiled),
        new(@"\d+\s*棒\s*[+＋]\s*\d+\s*[Ee][Xx]\s*棒", RegexOptions.Compiled),
        new(@"接力名单", RegexOptions.Compiled),
        new(@"接力整合", RegexOptions.Compiled),
        new(@"圆满结束", RegexOptions.Compiled),
    ];

    // ================================================================ 小工具

    /// <summary>
    /// 在排序序列里找离 i 最近、且帖内写了棒号的节点（先往前找、再往后找）。
    /// 用于「按声明时段推算棒号」：只有显式棒号能当锚点，缺棒导致的整体错位才不会传染。
    /// </summary>
    private static (int Bar, DateTime Slot)? NearestExplicitBar(List<ChainNode> numbered, int i, DateTime dayStart)
    {
        for (var step = 1; step < numbered.Count; step++)
        {
            foreach (var j in new[] { i - step, i + step })
            {
                if (j < 0 || j >= numbered.Count) continue;
                var f = numbered[j].Facts!;
                if (f.BarNumber is not { } bar) continue;
                var s = SlotOf(f, dayStart);
                if (s == DateTime.MinValue) continue;
                return (bar, s);
            }
        }
        return null;
    }

    /// <summary>把话题卡片转成可读的 PostRef（视频卡给 bvid，动态/文章卡给 opus id）。</summary>
    private static PostRef CardToRef(TopicCardEntry card)
    {
        // 注意：PostRef.Url 是按 Kind+Id 计算的，不能直接塞 cv 地址；
        // 文章卡在 TopicIndex 里 Kind 仍是 Opus（opus 页对文章同样成立），这里原样带过去。
        var kind = card.Kind == PostKind.Video ? PostKind.Video : PostKind.Opus;
        return new PostRef { Kind = kind, Id = card.Id };
    }

    /// <summary>
    /// 把「声明时段」安放到活动当天的绝对时间。
    /// 规则：以活动当天 0:00 为基准加分钟；若结果落在活动开始前 12 小时以更早，说明是次日的 00:00 之类的写法，顺延一天。
    /// （24:00 / 25:30 这类 32h 接力的写法由 AddMinutes 自然跨日。）
    /// </summary>
    private static DateTime SlotOf(PostFacts facts, DateTime dayStart)
    {
        if (!MatchVerifier.TryParseHourMinute(facts.DeclaredTime, out var minutes)) return DateTime.MinValue;
        var slot = dayStart.Date.AddMinutes(minutes);
        while (slot < dayStart.Date.AddHours(-12)) slot = slot.AddDays(1);
        return slot;
    }

    private static bool CloseToExisting(List<DateTime> existing, DateTime slot)
    {
        foreach (var s in existing)
            if (Math.Abs((s - slot).TotalMinutes) < SlotToleranceMinutes) return true;
        return false;
    }

    /// <summary>已有的那根棒（按声明时段找，容忍 ±SlotToleranceMinutes）。</summary>
    private static ChainNode? OwnerOfSlot(Dictionary<DateTime, ChainNode> owner, DateTime slot)
    {
        ChainNode? best = null;
        var bestDelta = double.MaxValue;
        foreach (var (key, node) in owner)
        {
            var delta = Math.Abs((key - slot).TotalMinutes);
            if (delta >= SlotToleranceMinutes || delta >= bestDelta) continue;
            best = node;
            bestDelta = delta;
        }
        return best;
    }

    /// <summary>
    /// 链上这张是不是「更没信息」的那一稿：空正文的视频卡，而新读到的这张有正文或上下棒。
    /// 只在同一棒有两张帖时用来决定换不换，避免把已经对的那一棒换掉。
    /// </summary>
    private static bool IsThinner(PostFacts? existing, PostFacts candidate)
    {
        if (existing is null) return true;
        var newText = (candidate.MergedText ?? string.Empty).Trim();
        if (newText.Length < 20 && !HasBarStructure(candidate)) return false;

        // 同一棒既有「视频」又有「动态/文章」，两张都写了上下棒时取动态/文章：
        // 视频通常是作品本体（简介里顺手贴了棒次信息），动态才是棒次说明帖，
        // 人工归档也是这么选的（实测第 22 棒：同人视频 vs 动态，人工取动态）。
        if (existing.Kind == PostKind.Video && candidate.Kind != PostKind.Video
            && HasBarStructure(existing) && HasBarStructure(candidate))
        {
            return true;
        }

        // 已有那棒自己写了上一棒/下一棒（或正文提到「棒」）——说明它就是正式的棒次帖，别换。
        // 只按字数判断是不够的：同人视频的简介可能有 60 多字，却一个字都没提棒次。
        if (HasBarStructure(existing)) return false;

        return HasBarStructure(candidate) || newText.Length >= 40;
    }

    /// <summary>这帖有没有「棒次结构」：写了上一棒/下一棒，或正文提到「棒」。</summary>
    private static bool HasBarStructure(PostFacts facts)
        => facts.Prevs.Count > 0
           || facts.Nexts.Count > 0
           || (facts.MergedText ?? string.Empty).Contains('棒');

    /// <summary>这帖看起来像一棒吗（有上一棒/下一棒指认，或正文里出现「棒」字）。</summary>
    private static bool LooksLikeBar(PostFacts facts)
    {
        if (facts.Prevs.Count > 0 || facts.Nexts.Count > 0) return true;
        var text = facts.MergedText;
        return !string.IsNullOrEmpty(text) && text.Contains('棒');
    }

    /// <summary>
    /// 卡片的发布时间是不是落在这一场活动的日子附近。
    /// </summary>
    /// <remarks>
    /// 同一个话题每年复用（「7月26日卡娜日」这种年度活动），话题卡片里混着往年那几场；
    /// 它们写着同样的钟点，若只按「声明时段」归位，就会把 2024/2025 的棒次当成今年这一场补进来
    /// （实测 2026 卡娜日补进了 2024、2025 各十几棒）。允许活动前后各 3 天（实测有棒次提前一天发布占位）。
    /// </remarks>
    private static bool IsNearRelayDay(TopicCardEntry card, DateTime dayStart)
    {
        if (card.PubTs <= 0) return true; // 拿不到时间戳的留着，靠读正文与验收判
        var pub = DateTimeOffset.FromUnixTimeSeconds(card.PubTs).LocalDateTime;
        // ±3 天太松：话题「7月26日卡娜日」里连着好几年都有帖，还会混进同一年的「后日谈 / 别的日子」，
        // 那些帖子自己写着「第N棒 8:00」，一旦被套到本场 dayStart 上就变成幽灵棒次。
        // 实测本场棒次最早提前 1 天发布（10-07 发 10-08 的棒），当天结束后最多再刷一天多的花絮，
        // 所以收紧成「前一天 0 点 ~ 次日中午」。
        return pub >= dayStart.Date.AddDays(-2) && pub < dayStart.Date.AddDays(1.5);
    }

    /// <summary>
    /// 省请求用的跳读判断：卡面没声明时段，而按发布时间推出来的时段已经被链上某一棒占了 → 不读。
    /// </summary>
    /// <remarks>
    /// 只对「卡面一个字都没有/没写时段」的卡生效：只要卡面写了钟点就必须读（那是最强证据，
    /// 15:30 那棒提前一天发布就是靠卡面文字救回来的）。
    /// </remarks>
    private static bool SkipByPubSlot(TopicCardEntry card, List<DateTime> existingSlots, DateTime dayStart,
        Dictionary<DateTime, ChainNode> slotOwner)
    {
        if (MatchVerifier.TrySlotAtLineStart(card.Text, out _)) return false;
        if (card.PubTs <= 0) return false;
        var pub = DateTimeOffset.FromUnixTimeSeconds(card.PubTs).LocalDateTime;
        var slot = RoundToHalfHour(pub);
        if (slot == DateTime.MinValue) return false;
        // 只比较钟点（同一天的活动），跨天的时间戳不参与
        var slotOfDay = dayStart.Date.AddHours(slot.Hour).AddMinutes(slot.Minute);
        if (slot < dayStart.Date.AddHours(-6) || slot > dayStart.Date.AddHours(30)) return false;
        if (!CloseToExisting(existingSlots, slotOfDay)) return false;

        // 链上那一棒自己没有棒次结构（只有靠发布时间猜出来的），说明当初可能猜到了
        // 「同一棒的另一稿」（同人视频/转发），话题里另有正式棒次帖 —— 这种不能跳读。
        // 实测第 22 棒（21:30）：同人视频 BV1xTHS6MEET 的简介被当成那一棒，真正的动态被跳过。
        var owner = OwnerOfSlot(slotOwner, slotOfDay);
        if (owner?.Facts is { } ownerFacts)
        {
            if (!HasBarStructure(ownerFacts)) return false;
            // 同一棒链上是视频、这张卡不是视频 → 还得读：动态/文章才是棒次说明帖。
            if (ownerFacts.Kind == PostKind.Video && card.Kind != PostKind.Video) return false;
        }
        return true;
    }

    /// <summary>把发布时间四舍五入到最近的半小时（活动棒次都是整点/半点）。</summary>
    private static DateTime RoundToHalfHour(DateTime value)
    {
        if (value == DateTime.MinValue) return DateTime.MinValue;
        var minutes = value.Minute;
        var rounded = minutes switch
        {
            < 15 => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Kind),
            < 45 => new DateTime(value.Year, value.Month, value.Day, value.Hour, 30, 0, value.Kind),
            _ => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Kind).AddHours(1),
        };
        return rounded;
    }

    /// <summary>附加帖（EX/SP/约稿）的识别强度。</summary>
    private enum ExKind
    {
        /// <summary>不是附加帖。</summary>
        None,
        /// <summary>强证据：帖里明确写成「约稿：」「特典：」或带「EX棒」——直接列进 ex 棒段。</summary>
        Strong,
        /// <summary>弱证据：只带了「SP」标记——先进提示，由人决定要不要补成 EX。</summary>
        Weak,
    }

    /// <summary>显式 EX/SP/约稿/特典标记（只认明确的词，别把普通帖误判成附加帖）。</summary>
    /// <remarks>
    /// 为什么要收紧：「约稿」「特典」出现在**正文任意位置**时误报率极高——实测第 16 棒（13:00 蔼石）
    /// 的正文里有一行「鸣谢约稿插图作者：@SAM271」，被旧规则判成附加帖，导致 13:00 那一棒从链上消失、
    /// 后面的 13:30 被插值成 13:15。人工 CSV 里那一棒就是正常棒次。
    ///
    /// 为什么又分强弱：本场活动里带「SP」标记的帖子有 5 张（上午 10:08 两张、晚上 10:08PM 三张），
    /// 但人工归档只把「10:08 约稿」那一张当 ex 棒。所以「SP」只作提示、不作结论——
    /// 机器不替人决定 ex 棒段收哪些，只把候选摆出来。
    /// </remarks>
    private static ExKind ClassifyEx(PostFacts facts, TopicCardEntry card)
    {
        if (facts.IsEx) return ExKind.Strong;

        var head = string.Join("\n", new[] { card.Text ?? "", facts.Title ?? "", HeadLines(facts.BodyText, 2) });
        var body = Trim(facts.BodyText, 400);

        // 强证据：署名的「约稿：」「特典：」（「鸣谢约稿插图作者：」这类不算——冒号不紧跟）
        if (System.Text.RegularExpressions.Regex.IsMatch(body, @"(?m)^[ \t　]*(约稿|特典)[ \t　]*[:：]"))
            return ExKind.Strong;
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"(约稿|特典)[ \t　]*[:：]"))
            return ExKind.Strong;
        if (head.Contains("EX棒", StringComparison.OrdinalIgnoreCase)) return ExKind.Strong;

        // 弱证据：只带 SP 标记
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"SP(?=[」】』\s)）!！:：,]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return ExKind.Weak;

        return ExKind.None;
    }

    /// <summary>取正文前 <paramref name="count"/> 行有内容的行（用来找首行声明的时段/标记）。</summary>
    private static string HeadLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n');
        var picked = new List<string>(count);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            picked.Add(line.Trim());
            if (picked.Count >= count) break;
        }
        return string.Join("\n", picked);
    }

    private void AddTopicNode(WalkContext ctx, PostRef reference, PostFacts facts, TopicCardEntry card, bool isEx, string note)
    {
        var node = new ChainNode
        {
            Ref = reference,
            Facts = facts,
            Direction = WalkDirection.None,
            Source = FetchMethod.TopicCards,
            Depth = 0,
            Confidence = isEx ? 0.5 : TopicCompletionConfidence,
            MatchedBy = isEx ? "话题卡片（附加帖）" : "话题卡片时段",
        };
        node.Notes.Add(note);
        if (isEx)
        {
            facts.IsEx = true;
            facts.BarNumber = null;
        }
        if (!string.IsNullOrWhiteSpace(card.AuthorName) && string.IsNullOrWhiteSpace(facts.AuthorName))
            facts.AuthorName = card.AuthorName;
        if (card.AuthorUid > 0 && facts.AuthorUid <= 0) facts.AuthorUid = card.AuthorUid;

        ctx.Result.Nodes.Add(node);
        ctx.ReadCache[reference.Id] = facts;
        if (!string.IsNullOrWhiteSpace(facts.PostId)) ctx.ReadCache[facts.PostId] = facts;
        ctx.Result.Audit.Add(new ChainAuditEntry
        {
            Step = isEx ? "话题补入（附加帖）" : "话题补入",
            Direction = WalkDirection.None,
            Method = FetchMethod.TopicCards,
            Detail = $"{facts.PostId}「{facts.AuthorName}」{(facts.DeclaredTime is null ? "" : "声明 " + facts.DeclaredTime)}"
                     + (string.IsNullOrWhiteSpace(facts.Title) ? "" : $" 标题：{Trim(facts.Title, 40)}"),
            Success = true,
        });
    }

    private static string ShortName(TopicCardEntry card)
    {
        var s = !string.IsNullOrWhiteSpace(card.Title) ? card.Title : card.Text;
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length == 0 ? card.Id : Trim(s, 28);
    }

    private static string FmtSlot(DateTime slot) => slot.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Trim(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    private static void Audit(WalkContext ctx, string step, string detail, bool success)
        => ctx.Result.Audit.Add(new ChainAuditEntry
        {
            Step = step, Direction = WalkDirection.Both, Method = FetchMethod.TopicCards,
            Detail = detail, Success = success,
        });
}
