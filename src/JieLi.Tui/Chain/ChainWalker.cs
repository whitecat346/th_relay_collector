#nullable enable
using System.Globalization;
using System.Text;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」
// 只允许改本文件与 Chain/MatchVerifier.cs / Chain/ChainWriter.cs。
//
// 职责：把一条接力链**双向遍历**出来并写成 ChainResult。
//
// 为什么是双向：
//   入口帖通常只是链条中间某一棒，只知道「下一棒 @ 谁」和「上一棒 @ 谁」。
//   Forward 沿 Facts.Nexts 往后走，Backward 沿 Facts.Prevs 往前走，两向都走到
//   显式首/末棒标记（或相遇）为止，才能得到完整接力顺序。
//
// 为什么 visited 以 postId 为键、绝不按 uid 去重：
//   一人可以投多棒（同一 uid 在链上出现多次是合法且常见的），按 uid 去重会直接
//   把整条链截断。postId 才是「一棒」的身份。
//
// 终止条件（与用户敲定，勿改）：
//   ① Forward 遇 LastMark ② Backward 遇 FirstMark ③ 两向相遇（下一棒 postId 已在 visited）
//   ④ 某方向连续 MaxConsecutiveFailures 次失败即停该方向 ⑤ MaxNodes 上限
//
// 每走一步都写 Audit（方法/成功/耗时），因为 --selftest-chain 与 report.md 都靠它解释
// 「这一棒是怎么找到的」；只有失败步骤不 MarkSuccess。
// ===========================================================================

/// <summary>双向 BFS 遍历器。</summary>
public sealed partial class ChainWalker
{
    // 审计里约定的两个统计步：报告要写「请求数 / 是否用了登录态」，但 ChainResult 是冻结契约
    // 不能加字段，所以借 Audit 传递（ChainWriter 读这两个 Step 的 Detail）。
    public const string AuditStepRequestCount = "统计-请求数";
    public const string AuditStepCookie = "统计-登录态";

    /// <summary>「棒号推算：…」必须作为 Node.Notes 的前缀，ChainWriter 靠它给报告标「推算」。</summary>
    public const string BarInferredPrefix = "棒号推算：";

    /// <summary>人工指定棒号的说明标记（--add-link 带棒号时写入，报告标「人工指定」）。</summary>
    public const string BarManualNote = "棒号由人工指定";

    // 单个邻居最多读几条候选。读候选是整条链最贵的开销（每帖一次详情请求），
    // 按「离期望时段最近」排序后取前几条，通常第一条就是对的。
    private const int MaxCandidateReads = 6;

    private readonly ChainHttp _http;
    private readonly IPostReader _reader;
    private readonly IUserLocator _locator;
    private readonly IAiAssist? _ai;
    private readonly MatchVerifier _verifier;

    public ChainWalker(ChainHttp http, IPostReader reader, IUserLocator locator, IAiAssist? ai = null)
    {
        _http = http;
        _reader = reader;
        _locator = locator;
        _ai = ai;
        _verifier = new MatchVerifier(http);
    }

    /// <summary>节点上限（一人多棒的链可能很长，防止病态链把请求数打爆）。</summary>
    public int MaxNodes { get; init; } = 80;

    /// <summary>某方向连续失败几次就停该方向（默认 2，与用户约定一致）。</summary>
    public int MaxConsecutiveFailures { get; init; } = 2;

    /// <summary>
    /// 从入口帖出发双向遍历。
    /// <paramref name="supplements"/> 是 --add-link 给的人工补录点（可以带人工棒号），
    /// 它们先作为节点落进链、再参与双向遍历，永远不会被自动发现覆盖。
    /// </summary>
    public ChainResult Walk(PostRef start, RelayQuery query, IReadOnlyList<(PostRef Ref, int? ForcedBar)>? supplements, Action<string>? log, CancellationToken ct = default)
    {
        var result = new ChainResult { StartPostId = start.Id };
        var ctx = new WalkContext(result, query) { Log = log, Ct = ct };

        // ---- 1. 入口帖：读失败就没有链可谈，直接抛给 Program（映射退出码 3）----
        PostFacts? startFacts = null;
        var startError = "未知原因";
        using (var scope = AuditScope.Begin(result, "读入口帖", WalkDirection.Both, FetchMethod.StartLink, start.Url))
        {
            try
            {
                startFacts = _reader.Read(start, log, ct);
                if (startFacts is not null)
                    scope.MarkSuccess($"{startFacts.PostId} 作者={startFacts.AuthorName} uid={startFacts.AuthorUid}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                startError = ex.Message;
            }
        }
        if (startFacts is null) throw new ChainFetchException($"入口帖读取失败：{startError}");

        // ---- 2. 日期窗口：query 是 Program 那个实例，原地补齐 DayStart/DayEnd（ChainWriter 还要用）----
        ResolveQueryWindow(query, startFacts);
        if (string.IsNullOrWhiteSpace(query.RelayName)) query.RelayName = startFacts.RelayName;
        // 视频/动态入口常常没有话题标签，RelayName 会是空的，于是自动命名退化成「2026_接力_x-x」。
        // 退一步从标题里抽「【…接力…】」那段当活动名（实测标题里最可靠的活动名来源）。
        if (string.IsNullOrWhiteSpace(query.RelayName))
            query.RelayName = InferRelayNameFromTitle(startFacts.Title);
        result.RelayName = query.RelayName;

        // 话题 id 一旦已知就告知定位器，让它建立「名字→uid→帖子」索引。
        // 这条索引完全绕开搜索接口（搜索会被 v_voucher 挑战），是本轮修复风控问题的核心。
        if (query.TopicId is null && startFacts.TopicId is { } startTopic) query.TopicId = startTopic;
        _locator.PrimeTopic(query.TopicId);

        var startNode = new ChainNode
        {
            Ref = start,
            Direction = WalkDirection.Both,
            Source = FetchMethod.StartLink,
            Depth = 0,
            Confidence = 1.0,
            IsStart = true,
            MatchedBy = "入口帖",
        };
        startNode.Notes.Add("入口帖（双向起点；棒号若为推算请核对）");
        AddNode(ctx, startNode, startFacts);
        ctx.ReadCache[KeyOf(startFacts, start)] = startFacts; // 入口帖不重读
        log?.Invoke($"入口：{startFacts.AuthorName}（uid={startFacts.AuthorUid}）发布于 {Fmt(startFacts.PubLocal)}，声明棒号={startFacts.BarRaw ?? "无"}");

        // ---- 3. 补录链接先当成链上的节点（人工指定优先于任何自动识别）----
        var seeds = new List<ChainNode>();
        if (supplements is { Count: > 0 })
        {
            foreach (var (suppRef, forcedBar) in supplements)
            {
                var node = ReadSupplement(ctx, suppRef, forcedBar);
                if (node is not null) seeds.Add(node);
            }
        }

        // ---- 4. 双向 BFS：入口与补录点都作为双向种子入队 ----
        foreach (var seed in seeds) ctx.Queue.Enqueue(new Expansion(seed, WalkDirection.Both));
        ctx.Queue.Enqueue(new Expansion(startNode, WalkDirection.Both));

        while (ctx.Queue.Count > 0)
        {
            if (ctx.Forward.Stopped && ctx.Backward.Stopped) break;
            ct.ThrowIfCancellationRequested();

            if (result.Nodes.Count >= MaxNodes)
            {
                StopBoth(ctx, $"已达 MaxNodes={MaxNodes} 上限，停止遍历（缺口见清单）", warn: true);
                break;
            }

            var expansion = ctx.Queue.Dequeue();
            var node = expansion.Node;
            if (node.Facts is null) continue;

            if (Covers(expansion.Directions, WalkDirection.Forward) && !ctx.Forward.Stopped)
                ExpandOne(ctx, node, WalkDirection.Forward);
            if (Covers(expansion.Directions, WalkDirection.Backward) && !ctx.Backward.Stopped)
                ExpandOne(ctx, node, WalkDirection.Backward);
        }

        // 方向收尾：没见到显式首/末棒标记就停了，说明链可能不完整（不是错误，但必须让报告说清楚）
        if (!ctx.Forward.Stopped)
            result.Warnings.Add("Forward 方向没有走到显式末棒标记（可能漏标 / 链路不完整，缺口见清单）");
        if (!ctx.Backward.Stopped)
            result.Warnings.Add("Backward 方向没有走到显式首棒标记（可能漏标 / 链路不完整，缺口见清单）");

        // 收尾补全：@ 链常常在「没写上一棒」的地方断开（实测存在），这时用话题卡片时间表把缺的棒接回来。
        // 放在 InferChainBars 之前：补进来的棒次也参与「从相邻指认推棒号」。
        CompleteFromTopic(ctx);

        InferChainBars(ctx);
        FinishResult(ctx);
        PruneCoveredGaps(ctx);
        return result;
    }

    /// <summary>
    /// 在**已有的链结果**上补录一棒，并从它继续双向补全（`--add-link URL --into &lt;slug&gt;`）。
    ///
    /// 与 <see cref="Walk"/> 的区别：旧链上的节点全部当作已知事实直接种进上下文（**不联网重读**），
    /// 只有补录帖是新的落点；两个方向各走到碰到已知节点（相遇）为止，再用
    /// <see cref="CompleteFromTopic"/> 把「没写上一棒」的断点按话题时间表补上。
    ///
    /// `forcedBar`（`--at-bar`）只改棒号标签，不参与缺口位置判定 —— 缺口一律按时间算。
    /// </summary>
    public ChainResult Complete(
        ChainResult existing,
        IReadOnlyList<(PostRef Ref, int? ForcedBar)> supplements,
        RelayQuery query,
        Action<string>? log,
        CancellationToken ct = default)
    {
        var result = new ChainResult { StartPostId = existing.StartPostId };
        var ctx = new WalkContext(result, query) { Log = log, Ct = ct };

        // ---- 1. 旧链节点全部当已知事实种进来（零请求）----
        ChainNode? startNode = null;
        foreach (var old in existing.Nodes.Where(n => n.Facts is not null).OrderBy(n => n.Depth))
        {
            var facts = old.Facts!;
            var node = new ChainNode
            {
                Ref = old.Ref,
                Direction = old.Direction,
                Source = old.Source,
                Depth = old.Depth,
                Confidence = old.Confidence,
                IsStart = old.IsStart,
                IsSupplement = old.IsSupplement,
                MatchedBy = old.MatchedBy,
            };
            node.Notes.AddRange(old.Notes);
            if (AddNode(ctx, node, facts) is { } added)
            {
                ctx.ReadCache[KeyOf(facts, old.Ref)] = facts;
                if (added.IsStart) startNode = added;
            }
        }

        // ---- 2. 活动名 / 日期窗口 / 话题：沿用旧链，缺的才补 ----
        var startFacts = startNode?.Facts;
        if (startFacts is not null) ResolveQueryWindow(query, startFacts);
        if (string.IsNullOrWhiteSpace(query.RelayName))
            query.RelayName = startFacts?.RelayName ?? existing.RelayName;
        if (string.IsNullOrWhiteSpace(query.RelayName)) query.RelayName = existing.RelayName;
        result.RelayName = query.RelayName ?? existing.RelayName;
        if (query.TopicId is null && (startFacts?.TopicId ?? FirstTopicId(existing)) is { } topicId)
            query.TopicId = topicId;
        _locator.PrimeTopic(query.TopicId);

        // 旧缺口当底稿留着，补完由 PruneCoveredGaps 去掉已经被补上的
        foreach (var gap in existing.Gaps) result.Gaps.Add(gap);

        // 旧链上的节点本来就是「保留」的：后面剔除窗口外新节点时不能误伤
        var knownIds = ctx.Visited.Keys.ToHashSet(StringComparer.Ordinal);

        // ---- 3. 补录帖作为新落点，从它双向展开 ----
        var seeds = new List<ChainNode>();
        foreach (var (suppRef, forcedBar) in supplements)
        {
            var node = ReadSupplement(ctx, suppRef, forcedBar);
            if (node is not null) seeds.Add(node);
        }

        if (seeds.Count == 0)
        {
            log?.Invoke("补录链接没有带来新节点（读不出来，或它本来就在链上）；仍按原链重算缺口");
        }
        else
        {
            foreach (var seed in seeds) ctx.Queue.Enqueue(new Expansion(seed, WalkDirection.Both));
            while (ctx.Queue.Count > 0)
            {
                if (ctx.Forward.Stopped && ctx.Backward.Stopped) break;
                ct.ThrowIfCancellationRequested();
                if (result.Nodes.Count >= MaxNodes)
                {
                    StopBoth(ctx, $"已达 MaxNodes={MaxNodes} 上限，停止遍历（缺口见清单）", warn: true);
                    break;
                }

                var expansion = ctx.Queue.Dequeue();
                var node = expansion.Node;
                if (node.Facts is null) continue;
                if (Covers(expansion.Directions, WalkDirection.Forward) && !ctx.Forward.Stopped)
                    ExpandOne(ctx, node, WalkDirection.Forward);
                if (Covers(expansion.Directions, WalkDirection.Backward) && !ctx.Backward.Stopped)
                    ExpandOne(ctx, node, WalkDirection.Backward);
            }

            // 补录点往往只接上两个方向中的一边，另一侧仍断：再用话题时间表兜一次
            DropOutOfWindow(ctx, knownIds, seeds);
            CompleteFromTopic(ctx);
        }

        InferChainBars(ctx);
        FinishResult(ctx);
        PruneCoveredGaps(ctx);
        if (!string.IsNullOrWhiteSpace(existing.Slug)) result.Slug = existing.Slug; // 目录名保持不变
        return result;
    }

    private static long? FirstTopicId(ChainResult result)
    {
        foreach (var n in result.Nodes)
            if (n.Facts?.TopicId is > 0 and var t) return t;
        return null;
    }

    /// <summary>
    /// 补录展开的边界：如果补录链接指到了**别的接力**，顺着 @ 会走进另一场活动。
    /// 这里把「不在本场时间窗内」的新节点剔掉（旧链节点与补录帖本身保留），只留一条警告。
    /// 判据是发布时间：活动日 [-2h, DayEnd+6h]，与候选验收用的是同一个窗口。
    /// </summary>
    private static void DropOutOfWindow(WalkContext ctx, HashSet<string> knownIds, IReadOnlyList<ChainNode> seeds)
    {
        var start = ctx.Query.DayStart;
        if (start == DateTime.MinValue) return;
        var lo = start.AddHours(-2);
        var hi = (ctx.Query.DayEnd == DateTime.MinValue ? start.AddHours(24) : ctx.Query.DayEnd).AddHours(6);

        var seedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            if (seed.Facts is null) continue;
            seedIds.Add(KeyOf(seed.Facts, seed.Ref));
            var pub = seed.Facts.PubLocal;
            if (pub != DateTime.MinValue && (pub < lo || pub > hi))
                ctx.Result.Warnings.Add(
                    $"补录帖「{seed.Facts.AuthorName}」发布于 {pub:MM-dd HH:mm}，不在本场活动时间窗（{lo:MM-dd HH:mm}～{hi:MM-dd HH:mm}）内 —— 请确认它确实是这一场接力的棒次。");
        }

        var dropped = new List<string>();
        for (var i = ctx.Result.Nodes.Count - 1; i >= 0; i--)
        {
            var node = ctx.Result.Nodes[i];
            if (node.Facts is null) continue;
            var key = KeyOf(node.Facts, node.Ref);
            if (knownIds.Contains(key) || seedIds.Contains(key)) continue;
            var local = node.Facts.PubLocal;
            if (local >= lo && local <= hi) continue;
            dropped.Add($"{node.Facts.AuthorName}（{local:MM-dd HH:mm}）");
            ctx.Result.Nodes.RemoveAt(i);
            ctx.Visited.Remove(key);
        }

        if (dropped.Count > 0)
        {
            // 被剔掉的节点带出来的缺口一并清掉，否则报告里会留下无主的「候选全被拒收」
            var orphanGaps = ctx.Result.Gaps.RemoveAll(g =>
                !string.IsNullOrEmpty(g.FromPostId) && !ctx.Visited.ContainsKey(g.FromPostId));
            ctx.Result.Warnings.Add(
                $"补录展开带进了 {dropped.Count} 条不在本场活动时间窗内的帖子（多半是补录链接指到了别的接力），已剔除："
                + string.Join("、", dropped)
                + (orphanGaps > 0 ? $"；同时清掉 {orphanGaps} 处由它们产生的缺口" : ""));
        }
    }


    /// <summary>
    /// 去掉「其实已经拿到」的假缺口：某条缺口期望的人/棒，链上已经有节点覆盖它。
    /// 判据要**同时**对上人和棒位（或双方都推不出棒位），否则会把「一人多棒」里
    /// 真正还缺的那一棒也删掉。
    /// </summary>
    private static void PruneCoveredGaps(WalkContext ctx)
    {
        if (ctx.Result.Gaps.Count == 0) return;
        var kept = new List<GapItem>(ctx.Result.Gaps.Count);
        foreach (var gap in ctx.Result.Gaps)
        {
            var covered = FindGapCoveringNode(ctx, gap);
            if (covered is null)
            {
                kept.Add(gap);
                continue;
            }
            var key = covered.Facts is null ? "?" : KeyOf(covered.Facts, covered.Ref);
            ctx.Log?.Invoke($"[缺口合并] 「{gap.ExpectedName}」第 {gap.ExpectedBar?.ToString() ?? "?"} 棒已被链上 postId={key} 覆盖，从缺口清单移除");
        }
        var removed = ctx.Result.Gaps.Count - kept.Count;
        ctx.Result.Gaps.Clear();
        ctx.Result.Gaps.AddRange(kept);
        if (removed > 0)
            ctx.Result.Warnings.Add($"已把 {removed} 处「其实已拿到」的缺口从清单里合并掉（同名同棒位在链上已有节点）");
    }

    /// <summary>缺口里的棒号是给人看的文本（可能是「7（推算）」），只取开头的数字。</summary>
    private static bool TryParseBarLabel(string? label, out int bar)
    {
        bar = 0;
        if (string.IsNullOrWhiteSpace(label)) return false;
        var digits = new string(label.Trim().TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out bar);
    }

    /// <summary>
    /// 从标题里抽活动名：优先【…】/「…」/『…』里含「接力」的那段，其次标题里第一个含「接力」的片段。
    /// 例：「【神行话妄】亘古始怨翌塑轮回」→ 抽不到（返回 null）；
    ///     「【8月15日魅魔日接力1.5】列王纪春秋」→「8月15日魅魔日接力1.5」。
    /// </summary>
    private static string? InferRelayNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(title, @"[【「『]([^】」』]{2,40})[】」』]"))
        {
            var inner = m.Groups[1].Value.Trim();
            if (inner.Contains("接力", StringComparison.Ordinal)) return inner;
        }

        var idx = title.IndexOf("接力", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = Math.Max(0, idx - 12);
        var end = Math.Min(title.Length, idx + 2);
        var fragment = title[start..end].Trim(' ', '【', '】', '「', '」', '『', '』', '-', '—', '|', '/', '·');
        return fragment.Length >= 4 ? fragment : null;
    }

    private static ChainNode? FindGapCoveringNode(WalkContext ctx, GapItem gap)
    {
        if (string.IsNullOrWhiteSpace(gap.ExpectedName) && string.IsNullOrWhiteSpace(gap.ExpectedUid)) return null;
        foreach (var n in ctx.Result.Nodes)
        {
            var f = n.Facts;
            if (f is null) continue;

            // 人对得上：uid 一致，或昵称归一化后一致
            var samePerson =
                (!string.IsNullOrWhiteSpace(gap.ExpectedUid)
                    && long.TryParse(gap.ExpectedUid, out var gapUid) && gapUid > 0 && f.AuthorUid == gapUid)
                || (!string.IsNullOrWhiteSpace(gap.ExpectedName) && SameNameFolded(f.AuthorName, gap.ExpectedName));
            if (!samePerson) continue;

            // 棒位也对得上：缺口没写棒号，或节点棒号相同
            if (gap.ExpectedBar is null) return n;
            var nodeBar = f.BarNumber;
            if (nodeBar is not null && TryParseBarLabel(gap.ExpectedBar, out var gapBar) && nodeBar == gapBar) return n;
        }
        return null;
    }

    /// <summary>
    /// 昵称比较（缺口合并专用）：先走 TextNormalizer.SameName，再容忍繁体/异体差异。
    /// 实测「雾雨梦灵」（帖子里写的）与「霧雨梦灵」（账号真名）被当成两个人，
    /// 于是链上明明已经有那一棒，缺口清单里还挂着一条假缺口。
    /// </summary>
    private static bool SameNameFolded(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (TextNormalizer.SameName(a, b)) return true;
        return string.Equals(FoldVariant(a), FoldVariant(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把常见繁体/异体字折叠成简体，并去空白/标点（只用于比较，不写回数据）。</summary>
    private static string FoldVariant(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch)) continue;
            sb.Append(FoldMap.TryGetValue(ch, out var mapped) ? mapped : ch);
        }
        return sb.ToString();
    }

    private static readonly Dictionary<char, char> FoldMap = new()
    {
        ['霧'] = '雾', ['靈'] = '灵', ['夢'] = '梦', ['繪'] = '绘', ['畫'] = '画',
        ['燈'] = '灯', ['貓'] = '猫', ['鳥'] = '鸟', ['魚'] = '鱼', ['龍'] = '龙',
        ['風'] = '风', ['雲'] = '云', ['銀'] = '银', ['紅'] = '红', ['藍'] = '蓝',
        ['綠'] = '绿', ['黃'] = '黄', ['點'] = '点', ['線'] = '线', ['聲'] = '声',
        ['樂'] = '乐', ['戀'] = '恋', ['愛'] = '爱', ['戰'] = '战', ['鬥'] = '斗',
        ['無'] = '无', ['盡'] = '尽', ['終'] = '终', ['緣'] = '缘',
    };

    // ================================================================ 节点管理

    private ChainNode? AddNode(WalkContext ctx, ChainNode node, PostFacts facts)
    {
        var key = KeyOf(facts, node.Ref);
        if (string.IsNullOrWhiteSpace(key))
        {
            ctx.Result.Warnings.Add($"有一棒的 postId 与链接都为空，无法识别，已跳过（作者 {facts.AuthorName}）");
            return null;
        }
        if (ctx.Visited.ContainsKey(key)) return null;

        var originalId = node.Ref.Id;
        if (!string.IsNullOrWhiteSpace(originalId) && ctx.Visited.ContainsKey(originalId)) return null;

        if (string.IsNullOrWhiteSpace(facts.PostId)) facts.PostId = key;
        node.Ref = new PostRef { Kind = facts.Kind, Id = key };
        node.Facts = facts;
        if (string.IsNullOrWhiteSpace(facts.Url)) facts.Url = node.Ref.Url;

        // 中途某一棒才带话题标签时（入口视频/动态常见），这里补上，让后续邻居定位走话题索引。
        if (ctx.Query.TopicId is null && facts.TopicId is { } discoveredTopic)
        {
            ctx.Query.TopicId = discoveredTopic;
            _locator.PrimeTopic(discoveredTopic);
            ctx.Log?.Invoke($"  · 从「{facts.AuthorName}」这一棒发现话题 topic_id={discoveredTopic}，后续定位改用话题索引");
        }

        ctx.Visited[key] = node;
        // 同一帖在不同来源里可能给的是 opus id 或链接里的 id，两把钥匙都登记，避免重复成节点
        if (!string.IsNullOrWhiteSpace(originalId) && originalId != key) ctx.Visited.TryAdd(originalId, node);

        ctx.Result.Nodes.Add(node);
        ctx.Log?.Invoke($"  + [{DirLabel(node.Direction)}] 棒号={facts.BarRaw ?? "?"} {facts.AuthorName}（postId={key} 深度={node.Depth} 置信={node.Confidence:0.00}）");
        return node;
    }

    private static ChainNode? LookupVisited(WalkContext ctx, PostFacts facts, PostRef fallback)
    {
        var key = KeyOf(facts, fallback);
        if (!string.IsNullOrWhiteSpace(key) && ctx.Visited.TryGetValue(key, out var node)) return node;
        if (!string.IsNullOrWhiteSpace(fallback.Id) && ctx.Visited.TryGetValue(fallback.Id, out node)) return node;
        return null;
    }

    private static string KeyOf(PostFacts facts, PostRef fallback)
        => !string.IsNullOrWhiteSpace(facts.PostId) ? facts.PostId.Trim() : fallback.Id.Trim();

    // ================================================================ 补录链接

    private ChainNode? ReadSupplement(WalkContext ctx, PostRef suppRef, int? forcedBar)
    {
        PostFacts? facts = null;
        string? error = null;
        var detail = suppRef.Url + (forcedBar is null ? "" : $"（人工指定第 {forcedBar} 棒）");
        using (var scope = AuditScope.Begin(ctx.Result, "读补充帖", WalkDirection.Both, FetchMethod.Supplement, detail))
        {
            try
            {
                facts = _reader.Read(suppRef, ctx.Log, ctx.Ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }
            if (facts is not null) scope.MarkSuccess($"{facts.PostId} 作者={facts.AuthorName}");
        }

        if (facts is null)
        {
            // 补录点读不出来只记缺口，不终止遍历（其它方向仍可能走通）
            ctx.Result.Gaps.Add(new GapItem
            {
                Kind = "reader",
                Reason = $"补充链接读取失败：{error ?? "未知原因"}",
                Direction = WalkDirection.Both,
                ExpectedName = forcedBar is null ? null : $"第 {forcedBar} 棒",
                TriedMethods = { "IPostReader.Read（--add-link 补录链接）" },
                SuggestedCommand = $"--add-link {suppRef.Url}",
            });
            return null;
        }

        var existing = LookupVisited(ctx, facts, suppRef);
        if (existing is not null)
        {
            // 补录点就是入口/已被自动发现的那一棒 → 只补人工棒号，不重复建节点
            if (forcedBar is not null && existing.Facts is not null)
            {
                ApplyForcedBar(existing.Facts, existing.Notes, forcedBar.Value);
                ctx.Log?.Invoke($"补充：postId={KeyOf(facts, suppRef)} 与已有节点重复，只补人工棒号 {forcedBar}");
            }
            return null;
        }

        var node = new ChainNode
        {
            Ref = suppRef,
            Direction = WalkDirection.Both,
            Source = FetchMethod.Supplement,
            Depth = 0,
            Confidence = 1.0,
            IsSupplement = true,
            MatchedBy = "补充链接（人工落点）",
        };
        if (forcedBar is not null) ApplyForcedBar(facts, node.Notes, forcedBar.Value);
        else node.Notes.Add("补录节点（--add-link 未指定棒号）");
        node.Notes.Add("补充链接：先作为落点，再从它继续双向走（不回头重走已知节点）");

        if (AddNode(ctx, node, facts) is null) return null;
        ctx.ReadCache[KeyOf(facts, suppRef)] = facts;
        ctx.Log?.Invoke($"补充：{facts.AuthorName}（uid={facts.AuthorUid}）postId={facts.PostId} 棒号={facts.BarRaw ?? "?"}");
        return node;
    }

    private static void ApplyForcedBar(PostFacts facts, List<string> notes, int bar)
    {
        facts.BarNumber = bar;
        facts.BarRaw ??= bar.ToString(CultureInfo.InvariantCulture);
        notes.RemoveAll(n => n.StartsWith(BarInferredPrefix, StringComparison.Ordinal));
        notes.Add($"{BarManualNote}：{bar}（来自 --add-link，优先于自动识别）");
    }

    // ================================================================ 单方向展开

    private void ExpandOne(WalkContext ctx, ChainNode node, WalkDirection dir)
    {
        var state = ctx.StateOf(dir);
        if (state.Stopped) return;
        var facts = node.Facts!;

        // 终止 ①/②：显式首末棒标记
        if (dir == WalkDirection.Forward && facts.LastMark)
        {
            StopDirection(ctx, dir, $"末棒显式标记「{facts.LastMarkRaw ?? "最后一棒"}」，Forward 方向到头", warn: false);
            return;
        }
        if (dir == WalkDirection.Backward && facts.FirstMark)
        {
            StopDirection(ctx, dir, $"首棒显式标记「{facts.FirstMarkRaw ?? "第一棒"}」，Backward 方向到头", warn: false);
            return;
        }

        var links = dir == WalkDirection.Forward ? facts.Nexts : facts.Prevs;
        var linkLabel = dir == WalkDirection.Forward ? "下一棒" : "上一棒";

        if (links.Count == 0)
        {
            // 没有 @ 指认：可能只是漏标。记一个可执行的缺口，并计一次失败（连续失败会停方向）。
            ctx.Result.Gaps.Add(BuildLinkGap(facts, dir, linkLabel));
            RegisterFailure(ctx, dir, $"没有「{linkLabel}」@：postId={facts.PostId}");
            return;
        }

        foreach (var link in links)
        {
            if (state.Stopped) return;
            if (ctx.Result.Nodes.Count >= MaxNodes)
            {
                StopBoth(ctx, $"已达 MaxNodes={MaxNodes} 上限，停止遍历（缺口见清单）", warn: true);
                return;
            }

            var resolution = ResolveNeighbor(ctx, node, link, dir);
            switch (resolution.Status)
            {
                case NeighborStatus.Found:
                    state.ConsecutiveFailures = 0;
                    ctx.Queue.Enqueue(new Expansion(resolution.Node!, dir));
                    break;

                case NeighborStatus.Skip:
                    ctx.Result.Audit.Add(new ChainAuditEntry
                    {
                        Step = "跳过已知节点", Direction = dir, Method = FetchMethod.Unknown,
                        Detail = resolution.Message, Success = true,
                    });
                    ctx.Log?.Invoke($"  · {resolution.Message}");
                    break;

                case NeighborStatus.StopDirection:
                    StopDirection(ctx, dir, resolution.Message, warn: true);
                    return;

                case NeighborStatus.Meeting:
                    if (resolution.Existing is { } existing && !Covers(existing.Direction, dir))
                        existing.Notes.Add("两向都指向本节点（先到者胜，保留先到方向；方向矛盾见报告）");
                    ctx.Result.Warnings.Add(resolution.Message);
                    StopBoth(ctx, resolution.Message, warn: false);
                    return;

                default:
                    RegisterFailure(ctx, dir, resolution.Message);
                    if (state.Stopped) return;
                    break;
            }
        }
    }

    private static GapItem BuildLinkGap(PostFacts facts, WalkDirection dir, string linkLabel)
    {
        return new GapItem
        {
            Kind = "neighbor",
            Reason = $"该帖没有识别到「{linkLabel}」@（可能漏标，或那条 @ 没有被正确识别）",
            FromPostId = facts.PostId,
            FromPostUrl = facts.Url,
            Direction = dir,
            ExpectedBar = GuessBar(facts, dir),
            ExpectedTime = facts.DeclaredTime,
            TriedMethods = { "NeighborExtractor（正文 + 评论区）", "首/末棒显式标记检查" },
            SuggestedCommand = "--add-link <缺失那一棒的帖子链接>",
        };
    }

    // ================================================================ 邻居解析：名字 → uid → 候选 → 验收

    private NeighborResolution ResolveNeighbor(WalkContext ctx, ChainNode node, NeighborLink link, WalkDirection dir)
    {
        var from = node.Facts!;
        var expectedName = TextNormalizer.CleanMention(link.Name);
        var hint = string.IsNullOrWhiteSpace(link.Uid) ? null : link.Uid!.Trim();
        if (string.IsNullOrWhiteSpace(expectedName) && string.IsNullOrWhiteSpace(hint))
            return NeighborResolution.Failed("该 @ 指认没有可用的昵称/uid，无法定位用户");

        // 这条 @ 指认可能早就被别的路径覆盖了（例如 5→7 与 6→7 都指向同一个人）。
        // 这里只做快速跳过（同一 uid 且**棒位也一致**才跳过，避免把「一人多棒」误判成已覆盖）；
        // 真正的去重在遍历结束后由 PruneCoveredGaps 统一做。

        // ---- ① 名字（或 @uid）→ uid ----
        UserRef? user = null;
        string? resolveError = null;
        using (var scope = AuditScope.Begin(ctx.Result, "定位用户", dir, FetchMethod.UserSearch,
                   $"「{link.Name}」" + (hint is null ? "" : $"（@组件 uid={hint}）")))
        {
            try
            {
                user = _locator.Resolve(link.Name, hint, ctx.Log, ctx.Ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolveError = ex.Message;
            }
            if (user is not null)
                scope.MarkSuccess($"uid={user.Uid} 名字「{user.Name}」方法={MethodLabel(user.Method)}{(user.Exact ? "" : "（近似）")}");
        }
        if (user is null)
        {
            return NeighborFailure(ctx, node, link, dir, "uid",
                $"昵称「{expectedName}」→ uid 定位失败{(resolveError is null ? "" : "：" + resolveError)}",
                new[]
                {
                    hint is null ? "① @组件 uid（源帖未给出）" : $"① @组件 uid={hint}",
                    "② bili_user 用户搜索",
                    "③ 话题作者表（TopicAuthorTable）",
                });
        }

        // ---- ② uid + 活动时间窗 → 候选帖子 ----
        IReadOnlyList<PostCandidate> candidates = Array.Empty<PostCandidate>();
        string? findError = null;
        using (var scope = AuditScope.Begin(ctx.Result, "找候选", dir, FetchMethod.SpaceOpusFeed,
                   $"uid={user.Uid}「{user.Name}」时间窗 {Fmt(ctx.Query.SearchStart)} ~ {Fmt(ctx.Query.SearchEnd)}"))
        {
            try
            {
                // 把源帖 id 当作「时间近似锚点」交给定位器：空间动态接口只回正文首行，
                // 接力帖首行常常不含活动名，严格标题过滤会 0 命中（实测过），
                // 这时按 opus id 与源帖 id 的距离兜底排序，读正文后再由 MatchVerifier 判。
                ctx.Query.AnchorPostId = long.TryParse(node.Ref.Id, out var srcId) ? srcId : 0;
                candidates = _locator.FindPosts(user, ctx.Query, ctx.Log, ctx.Ct) ?? Array.Empty<PostCandidate>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findError = ex.Message;
            }
            if (candidates.Count > 0) scope.MarkSuccess($"候选 {candidates.Count} 条");
            else scope.MarkSuccess(findError is null ? "无候选" : "无候选（异常见链路）");
        }
        if (candidates.Count == 0)
        {
            return NeighborFailure(ctx, node, link, dir, "candidate",
                $"uid={user.Uid}「{user.Name}」在活动时间窗内没有候选帖子{(findError is null ? "" : "：" + findError)}",
                new[]
                {
                    $"IUserLocator.Resolve → uid={user.Uid}（{MethodLabel(user.Method)}）",
                    "④ 空间动态列表（窗口内）",
                    "⑤ 文章搜索",
                    "⑥ 视频搜索",
                });
        }

        // ---- ③ 排序：先读「最像那一棒」的（声明时段/非专栏/带上下棒/发布时间靠近）----
        // 同一作者同窗多帖很常见（文章版 vs 动态版、21:30 动态 vs 21:35 视频），
        // 以前只顾「发布时间离期望时段最近、id 靠前」，会读到那篇空文本的，人工整理时不是这么选的。
        var slot = MatchVerifier.ExpectedSlot(from, link, dir);
        var ordered = candidates
            .Where(c => InSearchWindow(c, ctx.Query))
            .OrderByDescending(c => MatchVerifier.CandidateScore(c, slot, dir))
            .ThenBy(c => DistanceToSlot(c, slot))
            .ThenByDescending(c => c.PubTs)
            .Take(MaxCandidateReads)
            .ToList();
        if (ordered.Count == 0)
        {
            return NeighborFailure(ctx, node, link, dir, "candidate",
                $"uid={user.Uid}「{user.Name}」的候选都不在活动时间窗内（定位器的窗口过滤可能失效）",
                new[] { $"IUserLocator.FindPosts → {candidates.Count} 条候选", "⑦ 时间窗过滤（SearchStart ~ SearchEnd）" });
        }

        // ---- ④ 读候选 + 逐条验收；同一 postId 只读一次（缓存）----
        var read = new List<ReadCandidate>(ordered.Count);
        var rejected = new List<string>(ordered.Count);
        var readFailures = new List<string>();
        var readOk = 0;
        foreach (var cand in ordered)
        {
            var candFacts = ReadFactsCached(ctx, cand.Ref, dir, cand.Method, out var readError);
            if (candFacts is null)
            {
                readFailures.Add($"{cand.Ref.Id}: {readError ?? "读取失败"}");
                continue;
            }
            var key = KeyOf(candFacts, cand.Ref);
            if (key == KeyOf(from, node.Ref)) continue; // 就是源帖自身

            readOk++;
            var outcome = _verifier.Verify(from, link, candFacts, ctx.Query, dir);
            ctx.Result.Audit.Add(new ChainAuditEntry
            {
                Step = "验收候选", Direction = dir, Method = cand.Method,
                Detail = $"{key}「{candFacts.AuthorName}」→ {(outcome.Accepted ? "收下" : "拒收")}：{outcome.Reason}",
                Success = outcome.Accepted,
            });
            if (outcome.Accepted)
            {
                read.Add(new ReadCandidate(cand, candFacts, outcome));
                // 已经读到「声明时段正好对上 + 非专栏」的那张，就是这一棒了，不再多读（省请求、省风控）
                if (MatchVerifier.RankScore(candFacts, outcome, slot, dir) >= 90) break;
            }
            else rejected.Add($"{key}: {outcome.Reason}");
        }

        // ---- ⑤ 选要走的下一棒：优先从没走过的候选里挑；都已走过则做相遇/环判定 ----
        var fresh = read
            .Where(r => !IsVisited(ctx, r.Facts, r.Candidate.Ref))
            .OrderByDescending(r => MatchVerifier.RankScore(r.Facts, r.Outcome, slot, dir))
            .ThenBy(r => Math.Abs(r.Outcome.DiffMinutes ?? int.MaxValue))
            .ToList();
        if (fresh.Count > 0) return FoundNode(ctx, node, link, dir, fresh[0]);

        if (read.Count > 0)
        {
            var best = read
                .OrderByDescending(r => MatchVerifier.RankScore(r.Facts, r.Outcome, slot, dir))
                .ThenBy(r => Math.Abs(r.Outcome.DiffMinutes ?? int.MaxValue))
                .First();
            var bestKey = KeyOf(best.Facts, best.Candidate.Ref);
            var existing = LookupVisited(ctx, best.Facts, best.Candidate.Ref)!;

            // 入口/补录点不是「另一方向」的臂，只是绕回已知落点 → 跳过这条指认，别把方向整个停掉
            if (existing.IsStart || existing.IsSupplement)
                return NeighborResolution.Skip($"下一棒 postId={bestKey} 是{(existing.IsStart ? "入口" : "补录")}节点，已走过，跳过这条指认");

            if (Covers(existing.Direction, dir))
                return NeighborResolution.StopDirection($"同方向重逢：postId={bestKey} 已在本方向链上（链上出现环）");

            return NeighborResolution.Meeting(
                $"两向相遇：{DirLabel(dir)}方向的下一棒 postId={bestKey} 已被 {DirLabel(existing.Direction)} 方向先到（先到者胜；两向方向矛盾即方向冲突）",
                existing);
        }

        // ---- ⑥ AI 兜底：只有 AI 可用、且确实读过候选、且以上全部失败时才问 ----
        var aiResolution = TryAiCandidatePick(ctx, node, link, dir, read, out var aiNote);
        if (aiResolution is { } ai) return ai;

        var tried = new List<string>
        {
            $"IUserLocator.Resolve → uid={user.Uid}",
            $"候选 {candidates.Count} 条（读 {ordered.Count} 条，成功 {readOk} 条，读失败 {readFailures.Count} 条）",
        };
        if (rejected.Count > 0) tried.Add("拒收：" + Truncate(string.Join("；", rejected), 240));
        if (readFailures.Count > 0) tried.Add("读失败：" + Truncate(string.Join("；", readFailures), 160));
        if (!string.IsNullOrWhiteSpace(aiNote)) tried.Add("AI 兜底：" + aiNote);

        var kind = readOk == 0 && readFailures.Count > 0 ? "reader" : "candidate";
        return NeighborFailure(ctx, node, link, dir, kind,
            aiNote.Length > 0 ? "AI 兜底之后仍无法确定这一棒" : "候选全部被拒收 / 读取失败，无法确定这一棒",
            tried);
    }

    private NeighborResolution FoundNode(WalkContext ctx, ChainNode parent, NeighborLink link, WalkDirection dir, ReadCandidate picked)
    {
        var facts = picked.Facts;
        var node = new ChainNode
        {
            Ref = picked.Candidate.Ref,
            Direction = dir,
            Source = picked.Candidate.Method,
            Depth = parent.Depth + 1,
            Confidence = picked.Outcome.Confidence,
            MatchedBy = picked.Outcome.MatchedBy,
        };
        node.Notes.Add($"来源方法：{MethodLabel(picked.Candidate.Method)}");
        if (!string.IsNullOrWhiteSpace(picked.Candidate.Why)) node.Notes.Add($"候选理由：{picked.Candidate.Why}");
        node.Notes.Add($"验收：{picked.Outcome.Reason}");
        FillBarFromLink(node, facts, link);

        if (AddNode(ctx, node, facts) is null)
            return NeighborResolution.Skip($"postId={KeyOf(facts, picked.Candidate.Ref)} 已在链上，跳过");

        return NeighborResolution.Found(node);
    }

    /// <summary>源帖那条指认里写了棒号（「下一棒 第 7 棒 @X」）时补到候选上——只作辅助。</summary>
    private static void FillBarFromLink(ChainNode node, PostFacts facts, NeighborLink link)
    {
        if (facts.BarNumber is not null || facts.IsEx) return;
        var bar = MatchVerifier.ParseBar(link.BarRaw);
        if (bar is null) return;
        facts.BarNumber = bar;
        facts.BarRaw ??= link.BarRaw;
        node.Notes.Add($"{BarInferredPrefix}上游帖把这一棒标注为「{link.BarRaw}」（{(link.IsPrev ? "上一棒" : "下一棒")}指认，仅作辅助）");
    }

    private NeighborResolution? TryAiCandidatePick(WalkContext ctx, ChainNode parent, NeighborLink link, WalkDirection dir, List<ReadCandidate> read, out string note)
    {
        note = "";
        if (_ai is not { Enabled: true } || read.Count == 0) return null;

        var from = parent.Facts!;
        var request = new AiRequest
        {
            Purpose = "candidate",
            Snippet = Truncate(from.MergedText, 1500),
            Candidates = read.Select(r => new AtMention
            {
                Name = r.Facts.AuthorName,
                Uid = r.Facts.AuthorUid > 0 ? r.Facts.AuthorUid.ToString(CultureInfo.InvariantCulture) : null,
                Context = $"{r.Facts.PostId} {r.Facts.Title} 棒号={r.Facts.BarRaw ?? "?"}",
            }).ToList(),
            ExpectedBar = MatchVerifier.ParseBar(link.BarRaw),
            ExpectedTime = link.DeclaredTime ?? from.DeclaredTime,
            RelayName = ctx.Query.RelayName ?? from.RelayName,
            Question = $"源帖在找{DirLabel(dir)}「{link.Name}」。以上候选里哪一条是它？（只回 uid）",
        };

        AiVerdict? verdict = null;
        string? error = null;
        using (var scope = AuditScope.Begin(ctx.Result, "AI 兜底", dir, FetchMethod.AiAssist, $"候选 {read.Count} 条，问 AI 挑一条"))
        {
            try
            {
                verdict = _ai.Ask(request, ctx.Log, ctx.Ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }
            if (verdict is { Confident: true })
                scope.MarkSuccess($"AI 选 uid={verdict.PickUid} 名字={verdict.PickName}：{verdict.Reason}");
        }

        if (verdict is not { Confident: true })
        {
            note = error is null ? "未给出确信结论" : "调用失败：" + error;
            return null;
        }

        var picked = read.FirstOrDefault(r => MatchesAiPick(verdict, r.Facts));
        if (picked.Facts is null)
        {
            note = "AI 选中的 uid/名字不在候选里";
            return null;
        }

        note = $"AI 判定 uid={verdict.PickUid} 名字={verdict.PickName} 理由={verdict.Reason}（需人工复核）";
        var node = new ChainNode
        {
            Ref = picked.Candidate.Ref,
            Direction = dir,
            Source = FetchMethod.AiAssist,
            Depth = parent.Depth + 1,
            Confidence = 0.5,
            MatchedBy = "AI 判定",
        };
        node.Notes.Add(note);
        FillBarFromLink(node, picked.Facts, link);
        if (AddNode(ctx, node, picked.Facts) is null)
            return NeighborResolution.Skip($"postId={KeyOf(picked.Facts, picked.Candidate.Ref)} 已在链上，跳过");
        return NeighborResolution.Found(node);
    }

    private static bool MatchesAiPick(AiVerdict verdict, PostFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(verdict.PickUid)
            && long.TryParse(verdict.PickUid!.Trim(), out var uid) && uid == facts.AuthorUid) return true;
        if (!string.IsNullOrWhiteSpace(verdict.PickName) && TextNormalizer.SameName(verdict.PickName!, facts.AuthorName)) return true;
        return false;
    }

    // ================================================================ 失败 / 停止

    private NeighborResolution NeighborFailure(WalkContext ctx, ChainNode node, NeighborLink link, WalkDirection dir, string kind, string reason, IEnumerable<string> tried)
    {
        var facts = node.Facts!;
        var expectedBar = link.BarRaw;
        if (string.IsNullOrWhiteSpace(expectedBar)) expectedBar = GuessBar(facts, dir);

        ctx.Result.Gaps.Add(new GapItem
        {
            Kind = kind,
            Reason = reason,
            FromPostId = facts.PostId,
            FromPostUrl = facts.Url,
            Direction = dir,
            ExpectedName = string.IsNullOrWhiteSpace(link.Name) ? null : TextNormalizer.CleanMention(link.Name),
            ExpectedUid = link.Uid,
            ExpectedBar = expectedBar,
            ExpectedTime = link.DeclaredTime ?? facts.DeclaredTime,
            TriedMethods = tried.ToList(),
            SuggestedCommand = $"--add-link <第{expectedBar ?? "?"}棒的帖子链接>",
        });
        return NeighborResolution.Failed(reason);
    }

    /// <summary>棒号只作辅助：用源帖棒号 ±1 猜「期望棒号」，仅用于缺口清单提示。</summary>
    private static string? GuessBar(PostFacts facts, WalkDirection dir)
    {
        if (facts.IsEx || facts.BarNumber is not { } bar) return null;
        var guess = dir == WalkDirection.Forward ? bar + 1 : Math.Max(1, bar - 1);
        return $"{guess}（推算）";
    }

    private void RegisterFailure(WalkContext ctx, WalkDirection dir, string message)
    {
        var state = ctx.StateOf(dir);
        state.ConsecutiveFailures++;
        if (state.ConsecutiveFailures >= Math.Max(1, MaxConsecutiveFailures))
            StopDirection(ctx, dir, $"{DirLabel(dir)}方向连续 {state.ConsecutiveFailures} 次失败，停止该方向（最后一次：{message}）", warn: true);
    }

    private static void StopDirection(WalkContext ctx, WalkDirection dir, string message, bool warn)
    {
        var state = ctx.StateOf(dir);
        if (state.Stopped) return;
        state.Stopped = true;
        state.StopReason = message;
        ctx.Result.Audit.Add(new ChainAuditEntry
        {
            Step = "方向停止", Direction = dir, Method = FetchMethod.Unknown,
            Detail = message, Success = !warn,
        });
        if (warn) ctx.Result.Warnings.Add($"{DirLabel(dir)}方向停止：{message}");
        ctx.Log?.Invoke($"  · 停止{DirLabel(dir)}方向：{message}");
    }

    private static void StopBoth(WalkContext ctx, string message, bool warn)
    {
        StopDirection(ctx, WalkDirection.Forward, message, warn);
        StopDirection(ctx, WalkDirection.Backward, message, warn);
    }

    // ================================================================ 日期 / 棒号 / 收尾

    /// <summary>
    /// 补齐活动时间窗。规则（与用户敲定）：DayStart 缺失用入口帖发布日；年份为 1 用入口帖年份补；
    /// DeclaredHours<=0 时按 24 小时，否则按声明小时数。
    /// </summary>
    private static void ResolveQueryWindow(RelayQuery query, PostFacts startFacts)
    {
        var entryLocal = startFacts.PubLocal;
        var fallback = entryLocal != DateTime.MinValue ? entryLocal.Date : DateTime.Now.Date;

        if (query.DayStart == DateTime.MinValue)
        {
            query.DayStart = fallback;
        }
        else if (query.DayStart.Year == 1)
        {
            var month = Math.Clamp(query.DayStart.Month, 1, 12);
            var day = Math.Clamp(query.DayStart.Day, 1, DateTime.DaysInMonth(fallback.Year, month));
            query.DayStart = new DateTime(fallback.Year, month, day);
        }

        // 窗口以 DayStart 为唯一基准，避免 --date 与 --hours 组合时出现两份不一致的 DayEnd
        query.DayEnd = query.DeclaredHours > 0 ? query.DayStart.AddHours(query.DeclaredHours) : query.DayStart.AddHours(24);
    }

    /// <summary>
    /// 棒号后处理：声明值优先，其次从相邻棒指认 / 发布时间推算（报告会标「推算」）。
    /// 推算只是为了让「按棒号排序」和人工核对有个参照，绝不覆盖声明值。
    /// </summary>
    /// <summary>棒号是否由人工指定（`--at-bar`）：人工号是权威标签，不能拿去推相邻棒的号。</summary>
    private static bool HasManualBar(ChainNode node)
        => node.Notes.Any(n => n.StartsWith(BarManualNote, StringComparison.Ordinal));

    private static void InferChainBars(WalkContext ctx)
    {
        var nodes = ctx.Result.Nodes.Where(n => n.Facts is not null).ToList();
        var changed = 0;

        // ① 相邻棒的「上一棒/下一棒」标注里写了棒号（最可靠的一类推算）
        foreach (var node in nodes)
        {
            var facts = node.Facts!;
            foreach (var (link, isPrev) in AllLinks(facts))
            {
                var target = FindChainNodeByLink(nodes, link, node);
                if (target?.Facts is null || ReferenceEquals(target, node)) continue;
                if (target.Facts.BarNumber is not null || target.Facts.IsEx) continue;
                if (HasManualBar(target)) continue;
                var bar = MatchVerifier.ParseBar(link.BarRaw);
                if (bar is null) continue;
                target.Facts.BarNumber = bar;
                target.Facts.BarRaw ??= link.BarRaw;
                target.Notes.Add($"{BarInferredPrefix}postId={facts.PostId} 的「{(isPrev ? "上一棒" : "下一棒")}」标注为「{link.BarRaw}」（报告标为推算）");
                changed++;
            }
        }

        // ② 发布时间相邻（≤3h）且一侧有棒号 → 另一侧 ±1。
        //    不按 uid 合并节点（一人多棒合法），只按时间相邻推数字。
        //    人工指定的棒号不参与推算（否则会把人工号传播成一大片推算号）。
        var byTime = nodes.OrderBy(n => n.Facts!.PubTs).ToList();
        for (var i = 0; i + 1 < byTime.Count; i++)
        {
            var a = byTime[i];
            var b = byTime[i + 1];
            var fa = a.Facts!;
            var fb = b.Facts!;
            if (fa.PubTs <= 0 || fb.PubTs <= 0) continue;
            if (fb.PubTs - fa.PubTs > 3 * 3600) continue;
            if (HasManualBar(a) || HasManualBar(b)) continue;

            if (fa.BarNumber is { } ab && fb.BarNumber is null && !fb.IsEx)
            {
                var inferred = ab + 1;
                fb.BarNumber = inferred;
                fb.BarRaw ??= inferred.ToString(CultureInfo.InvariantCulture);
                b.Notes.Add($"{BarInferredPrefix}与第 {ab} 棒发布时间相隔 ≤3 小时，推算为第 {inferred} 棒");
                changed++;
            }
            else if (fb.BarNumber is { } bb && fa.BarNumber is null && !fa.IsEx)
            {
                var inferred = Math.Max(1, bb - 1);
                fa.BarNumber = inferred;
                fa.BarRaw ??= inferred.ToString(CultureInfo.InvariantCulture);
                a.Notes.Add($"{BarInferredPrefix}与第 {bb} 棒发布时间相隔 ≤3 小时，推算为第 {inferred} 棒");
                changed++;
            }
        }

        ctx.Result.Audit.Add(new ChainAuditEntry
        {
            Step = "棒号推算", Direction = WalkDirection.Both, Method = FetchMethod.Unknown,
            Detail = changed > 0 ? $"从相邻指认/发布时间补齐 {changed} 个棒号（报告标为推算）" : "没有可推算的棒号",
            Success = changed > 0,
        });
    }

    private static IEnumerable<(NeighborLink Link, bool IsPrev)> AllLinks(PostFacts facts)
    {
        foreach (var l in facts.Prevs) yield return (l, true);
        foreach (var l in facts.Nexts) yield return (l, false);
    }

    private static ChainNode? FindChainNodeByLink(List<ChainNode> nodes, NeighborLink link, ChainNode exclude)
    {
        if (!string.IsNullOrWhiteSpace(link.Uid) && long.TryParse(link.Uid!.Trim(), out var uidValue))
        {
            var byUid = nodes.Where(n => n.Facts is not null && n.Facts.AuthorUid == uidValue && !ReferenceEquals(n, exclude)).ToList();
            if (byUid.Count == 1) return byUid[0];
        }

        var byName = nodes
            .Where(n => n.Facts is not null && !ReferenceEquals(n, exclude)
                        && (TextNormalizer.SameName(n.Facts.AuthorName, link.Name)
                            || (n.Facts.CreatorInText is { } c && TextNormalizer.SameName(c, link.Name))))
            .ToList();
        if (byName.Count == 1) return byName[0];

        // 同名多人（一人多棒 / 重名）时用棒号消歧，仍消不掉就放弃（宁可留缺口也不猜错）
        if (byName.Count > 1 && MatchVerifier.ParseBar(link.BarRaw) is { } bar)
            return byName.FirstOrDefault(n => n.Facts!.BarNumber == bar);
        return null;
    }

    // 实例方法：需要读 _http 的请求计数/登录态（审计要如实反映这次遍历花了多少请求）
    private void FinishResult(WalkContext ctx)
    {
        var result = ctx.Result;
        var local = result.Nodes.FirstOrDefault(n => n.IsStart)?.Facts?.PubLocal ?? DateTime.MinValue;
        var year = local != DateTime.MinValue ? local.Year : (ctx.Query.DayStart != DateTime.MinValue ? ctx.Query.DayStart.Year : DateTime.Now.Year);
        var month = local != DateTime.MinValue ? local.Month : ctx.Query.DayStart.Month;
        var day = local != DateTime.MinValue ? local.Day : ctx.Query.DayStart.Day;

        result.RelayName = ctx.Query.RelayName ?? result.RelayName;
        // 入口帖常常没挂话题（视频/动态），活动名往往要等链上别的棒才认得出来 —— 补一次。
        if (string.IsNullOrWhiteSpace(result.RelayName))
        {
            result.RelayName = ctx.Result.Nodes
                .Select(n => n.Facts?.RelayName)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(result.RelayName) && string.IsNullOrWhiteSpace(ctx.Query.RelayName))
                ctx.Query.RelayName = result.RelayName;   // ChainWriter/报告也要用
        }
        result.Slug = $"{year}_{SanitizeSlug(result.RelayName ?? "接力")}_{month:00}-{day:00}";

        result.Audit.Add(new ChainAuditEntry
        {
            Step = AuditStepRequestCount, Direction = WalkDirection.None, Method = FetchMethod.Unknown,
            Detail = _http.RequestCount.ToString(CultureInfo.InvariantCulture), Success = true,
        });
        result.Audit.Add(new ChainAuditEntry
        {
            Step = AuditStepCookie, Direction = WalkDirection.None, Method = FetchMethod.Unknown,
            Detail = _http.UsingLoginCookie ? "使用登录态（cookie）" : "匿名（未用登录态）", Success = true,
        });
        result.Audit.Add(new ChainAuditEntry
        {
            Step = "汇总", Direction = WalkDirection.Both, Method = FetchMethod.Unknown,
            Detail = $"节点 {result.Nodes.Count}，缺口 {result.Gaps.Count}，请求 {_http.RequestCount}，末棒标记 {(result.ForwardComplete ? "有" : "无")}，活动名 {result.RelayName ?? "未识别"}",
            Success = true,
        });
    }

    private static string SanitizeSlug(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsWhiteSpace(ch)) { sb.Append('_'); continue; }
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        var s = sb.ToString().Trim('_', '.');
        if (s.Length == 0) s = "接力";
        return s.Length > 40 ? s[..40] : s;
    }

    // ================================================================ 小工具

    private PostFacts? ReadFactsCached(WalkContext ctx, PostRef reference, WalkDirection dir, FetchMethod method, out string? error)
    {
        error = null;
        var key = reference.Id;
        if (ctx.ReadCache.TryGetValue(key, out var cached)) return cached;

        PostFacts? facts = null;
        using (var scope = AuditScope.Begin(ctx.Result, "读候选", dir, method, reference.Url))
        {
            try
            {
                facts = _reader.Read(reference, ctx.Log, ctx.Ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }
            if (facts is not null) scope.MarkSuccess($"{facts.PostId} 作者={facts.AuthorName}");
        }

        // 失败也缓存：同一 postId 在一次遍历里只读一次，避免对风控接口反复重试
        ctx.ReadCache[key] = facts;
        if (facts is not null && !string.IsNullOrWhiteSpace(facts.PostId) && facts.PostId != key)
            ctx.ReadCache[facts.PostId] = facts;
        return facts;
    }

    private static bool InSearchWindow(PostCandidate cand, RelayQuery query)
    {
        if (cand.PubTs <= 0) return true; // 拿不到发布时间的候选只能靠验收判定，不能提前扔
        var start = new DateTimeOffset(query.SearchStart).ToUnixTimeSeconds();
        var end = new DateTimeOffset(query.SearchEnd).ToUnixTimeSeconds();
        return cand.PubTs >= start && cand.PubTs <= end;
    }

    private static long DistanceToSlot(PostCandidate cand, DateTime? slot)
    {
        if (slot is not { } s || cand.PubTs <= 0) return long.MaxValue / 2;
        var pub = DateTimeOffset.FromUnixTimeSeconds(cand.PubTs).ToLocalTime().DateTime;
        return Math.Abs((pub - s).Ticks);
    }

    private static bool IsVisited(WalkContext ctx, PostFacts facts, PostRef fallback)
        => LookupVisited(ctx, facts, fallback) is not null;

    private static bool Covers(WalkDirection existing, WalkDirection requested)
        => existing == requested || existing == WalkDirection.Both;

    private static string Fmt(DateTime time) => time == DateTime.MinValue ? "未指定" : time.ToString("yyyy-MM-dd HH:mm");

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Trim();
        if (t.Length <= max) return t;
        var cut = Math.Max(0, max - 1);
        return t[..cut] + "…";
    }

    internal static string MethodLabel(FetchMethod method) => method switch
    {
        FetchMethod.StartLink => "入口帖",
        FetchMethod.Supplement => "补充链接",
        FetchMethod.TopicCards => "话题卡片",
        FetchMethod.SpaceOpusFeed => "空间动态",
        FetchMethod.ArticleSearch => "文章搜索",
        FetchMethod.VideoSearch => "视频搜索",
        FetchMethod.MembersMid => "评论会员",
        FetchMethod.UserSearch => "用户搜索",
        FetchMethod.TopicAuthorTable => "话题作者表",
        FetchMethod.AiAssist => "AI 兜底",
        FetchMethod.Manual => "人工",
        _ => "未知",
    };

    internal static string DirLabel(WalkDirection dir) => dir switch
    {
        WalkDirection.Forward => "Forward（往后）",
        WalkDirection.Backward => "Backward（往前）",
        WalkDirection.Both => "双向",
        _ => "无",
    };

    // ================================================================ 内部类型

    private readonly record struct Expansion(ChainNode Node, WalkDirection Directions);

    private readonly record struct ReadCandidate(PostCandidate Candidate, PostFacts Facts, MatchOutcome Outcome);

    private enum NeighborStatus { Found, Skip, StopDirection, Meeting, Failed }

    private readonly record struct NeighborResolution(NeighborStatus Status, ChainNode? Node, ChainNode? Existing, string Message)
    {
        public static NeighborResolution Found(ChainNode node) => new(NeighborStatus.Found, node, null, "");
        public static NeighborResolution Skip(string message) => new(NeighborStatus.Skip, null, null, message);
        public static NeighborResolution StopDirection(string message) => new(NeighborStatus.StopDirection, null, null, message);
        public static NeighborResolution Meeting(string message, ChainNode existing) => new(NeighborStatus.Meeting, null, existing, message);
        public static NeighborResolution Failed(string message) => new(NeighborStatus.Failed, null, null, message);
    }

    private sealed class DirectionState
    {
        public DirectionState(WalkDirection direction) => Direction = direction;
        public WalkDirection Direction { get; }
        public bool Stopped { get; set; }
        public string? StopReason { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    private sealed class WalkContext
    {
        public WalkContext(ChainResult result, RelayQuery query)
        {
            Result = result;
            Query = query;
        }

        public ChainResult Result { get; }
        public RelayQuery Query { get; }
        public Dictionary<string, ChainNode> Visited { get; } = new(StringComparer.Ordinal);
        public Queue<Expansion> Queue { get; } = new();
        /// <summary>postId → 详情（null 表示读过但失败，不再重读）。</summary>
        public Dictionary<string, PostFacts?> ReadCache { get; } = new(StringComparer.Ordinal);
        public DirectionState Forward { get; } = new(WalkDirection.Forward);
        public DirectionState Backward { get; } = new(WalkDirection.Backward);
        public Action<string>? Log { get; set; }
        public CancellationToken Ct { get; set; }

        public DirectionState StateOf(WalkDirection dir) => dir == WalkDirection.Backward ? Backward : Forward;
    }
}
