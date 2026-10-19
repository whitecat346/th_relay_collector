#nullable enable
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>一次交叉校验的结果。</summary>
public sealed class ValidationResult
{
    public List<ParsedBar> Bars { get; init; } = new();
    public List<string> AssertionFailures { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    /// <summary>总结/名册动态的卡片 id（若找到）。</summary>
    public string? RosterCardId { get; set; }
    public List<RosterSlot> RosterSlots { get; set; } = new();

    public int Attributed => Bars.Count(b => !b.IsDuplicate && (b.BarNumber.HasValue || b.IsEx));
    public int Unattributable => Bars.Count(b => !b.IsDuplicate && b.IsUnattributable);
    public int ConflictCount => Bars.Count(b => !b.IsDuplicate && b.Conflicts.Count > 0);
    public int ProxyCount => Bars.Count(b => b.IsProxy);
    public int MaxBar => Bars.Where(b => b.BarNumber.HasValue).Select(b => b.BarNumber!.Value).DefaultIfEmpty(0).Max();

    /// <summary>
    /// 被排除的卡片：同一棒位的重复投稿、同一作者的非棒次第二帖。
    /// 它们不进入归档产出（CSV/md），但必须在报告里列出来，让"少了什么"是可审计的。
    /// </summary>
    public List<IgnoredCard> Ignored { get; } = new();

    /// <summary>时段网格的说明（没有走时段制时为空）。</summary>
    public string? SlotGridNote { get; set; }
}

/// <summary>一条被排除的卡片。</summary>
public sealed record IgnoredCard(string CardId, string Author, string Time, string Reason);

/// <summary>
/// M3 交叉校验器。三路"证据"的含义（设计阶段确定的口径）：
///   ① 卡片结构化多字段（desc.text / major.*.desc / major.*.title）—— 字段与正文来源（主）
///   ② 单棒页面 DOM 解析（人工保存的页面兜底）—— 字段的备份来源
///   ③ at 链图遍历 —— 独立复核"棒序对不对"（只提供第二个意见，不提供字段）
/// 断言口径：
///   - 卡片数 == 接口 dynamics → 硬失败（在采集层断言1）
///   - 棒号在 1..实际最高棒 上无缺失、无重复 → 硬失败（本类断言2）
///     ⚠ 不能用 dynamics 当上限：实测 dynamics=34 而最高棒号=30（差额是视频投稿/无关投稿），
///       拿 34 当上限会误报"缺第31~34棒"。
///   - 发布时刻与声明时段偏差 → 仅警告（实测存在发布延迟 193 分钟的正当反例）
/// </summary>
public static class ArtifactValidator
{
    /// <summary>偏差阈值（分钟）：超过则进"可疑"清单，但不阻断。</summary>
    public const int DeviationWarnMinutes = 120;

    /// <summary>
    /// 组织号判定阈值：同一个账号有 ≥N 次"正文标注的创作者 ≠ 自己"，
    /// 就认为它是代发型账号（主催号）。实测 沐南沐南沐南 mid=351852362 有 12 次。
    /// 用数据驱动而不是硬编码 mid——换一个活动、换一个主催，逻辑依然成立。
    /// </summary>
    private const int OrganizerThreshold = 2;

    public static ValidationResult Validate(IEnumerable<TopicCard> cards, int? dynamicsReported = null, string? topicName = null)
    {
        var cardList = cards.ToList();

        // 先推断话题标签：它是"这条动态属于本接力"的最强信号，供无"第N棒"格式的活动使用
        var tag = SummaryExtractor.InferTopicTag(cardList, topicName);

        var bars = new List<ParsedBar>();
        foreach (var card in cardList)
        {
            var bar = BarParser.Parse(card, tag);

            // 总结/名册卡本身不是一根棒。它里面罗列了全部棒位，
            // 若按普通卡片解析会被当成"0:00 那一棒"，导致后续棒号整体错位（实测 topic 1348289）。
            if (SummaryExtractor.IsSummaryCard(card, out _))
            {
                bar.BarNumber = null;
                bar.IsEx = false;
                bar.DeclaredTime = null;
                bar.MatchedStyle = "总结名册卡(不计为棒)";
                bar.Conflicts.Clear();
                bar.Notes.Clear();
                bar.Notes.Add("本条是活动的总结/名册动态，不作为一根棒");
                bar.IsSummary = true;
            }

            BarParser.ComputeDeviation(bar);
            bars.Add(bar);
        }

        return FinishValidation(bars, cardList, dynamicsReported, tag, SummaryExtractor.FindRoster(cardList));
    }

    /// <summary>
    /// 供**单链接模式**复用：帖子正文来自 opus 页面（而非话题卡片），已先行解析成 ParsedBar。
    /// 与 <see cref="Validate"/> 共用第二遍处理与断言，保证两种输入模式产出同构结果。
    ///
    /// ⚠ 关键差异：单链接模式**只知道链上的那几条帖子**，不能断言"1..最高棒 之间缺了哪些"——
    /// 实测只给 1 条帖子（第27棒）时会误报"缺棒 26"。因此这里把"缺棒断言"收紧为
    /// **只检查已知棒位之间的空隙**（首尾之外不算缺）。
    /// </summary>
    public static ValidationResult ValidateFromBars(
        List<ParsedBar> bars, int? dynamicsReported = null, string? topicTag = null)
    {
        // 名册发现需要 TopicCard 视图；用 ParsedBar 携带的 MergedText 还原即可
        var pseudo = bars.Select(b => new TopicCard
        {
            Id = b.CardId,
            MergedText = b.MergedText,
            AuthorName = b.PosterName,
        }).ToList();

        return FinishValidation(bars, pseudo, dynamicsReported, topicTag,
                                SummaryExtractor.FindRoster(pseudo),
                                missingBarsOnlyWithinKnownRange: true);
    }

    /// <summary>两种输入模式共用的第二遍处理与三道断言。</summary>
    private static ValidationResult FinishValidation(
        List<ParsedBar> bars,
        List<TopicCard> cardList,
        int? dynamicsReported,
        string? tag,
        (TopicCard? Card, List<RosterSlot> Slots) roster,
        bool missingBarsOnlyWithinKnownRange = false)
    {
        var result = new ValidationResult();
        result.Bars.AddRange(bars);

        // 名册（总结动态）：ai_guild.md 的"收集方法优先级 1"，用于校验棒号与时段
        var (rosterCard, rosterSlots) = roster;
        result.RosterCardId = rosterCard?.Id;
        result.RosterSlots = rosterSlots;

        // 无棒号时：先用名册反演（实测 1348289 的逐棒正文既没有棒号也没有时段，只能靠名册对齐）
        AssignBarNumbersFromRoster(bars, rosterSlots);

        // 仍无棒号时：退回"时段反演"（只对有时段的卡片有效）
        AssignBarNumbersByTime(bars, rosterSlots, result, tag);

        // ---------- 第二遍：数据驱动的"发布者是否创作者" ----------
        ApplyProxyHeuristic(bars);

        // ---------- 断言 2：棒号完整性（上限 = 实际最高棒号） ----------
        var numbered = bars.Where(b => b.BarNumber.HasValue).ToList();
        var dups = numbered.GroupBy(b => b.BarNumber!.Value).Where(g => g.Count() > 1)
                           .Select(g => g.Key).OrderBy(x => x).ToList();
        if (dups.Count > 0)
            result.AssertionFailures.Add($"棒号重复：{string.Join(", ", dups.Select(x => "第" + x + "棒"))}");

        if (numbered.Count == 0)
        {
            if (bars.Count > 0) result.AssertionFailures.Add("没有任何卡片解析出棒号");
        }
        else
        {
            int max = numbered.Max(b => b.BarNumber!.Value);
            int min = numbered.Min(b => b.BarNumber!.Value);

            // 单链接模式只知道链上的少数帖子：只检查**已知棒位之间**的空隙，
            // 首尾之外不算缺（否则只给 1 条第27棒时会误报"缺棒 26"）。
            int lo = missingBarsOnlyWithinKnownRange ? min : 1;
            int hi = missingBarsOnlyWithinKnownRange ? max : max;
            var missing = (lo <= hi ? Enumerable.Range(lo, hi - lo + 1) : Enumerable.Empty<int>())
                          .Where(i => !numbered.Any(b => b.BarNumber == i)).ToList();

            if (missing.Count > 0)
                result.AssertionFailures.Add(
                    $"棒号缺失（{(lo == 1 ? "1" : $"第{lo}棒")}..第{hi}棒之间）："
                    + string.Join(", ", missing.Select(x => "第" + x + "棒")));

            if (missingBarsOnlyWithinKnownRange)
                result.Warnings.Add($"单链接模式：只覆盖第 {min}–{max} 棒，两端之外的棒次未被采集，不计为缺失");
        }

        // ---------- 与接口声称的动态数对照（信息性，不硬失败） ----------
        if (dynamicsReported is { } dyn && dyn > 0)
        {
            int explained = bars.Count(b => !b.IsSummary && (b.BarNumber.HasValue || b.IsEx));
            int unnumbered = bars.Count - explained;
            if (dyn != bars.Count)
                result.Warnings.Add($"卡片数({bars.Count}) ≠ 接口声称的 dynamics({dyn})");
            if (unnumbered > 0)
                result.Warnings.Add($"有 {unnumbered} 条卡片没有棒号（未编号的投稿/无关内容），"
                                    + $"与最高棒号 {result.MaxBar} 一起解释了 dynamics({dyn}) 中的差额");
        }

        // ---------- ③ at 链图遍历复核 ----------
        CrossCheckChain(bars, result.Warnings);

        // ---------- 逐条标注问题 ----------
        foreach (var b in bars)
        {
            if (b.IsSummary || b.IsDuplicate) continue; // 总结/名册卡与已排除的重复卡不参与逐条问题标注

            // 上下棒定位：出现了"上一棒/下一棒"字样，却没有可识别的 @ 提及
            // （用户要求：对方没有被正确 @ 出来 → 归为异常，交人工处理）
            var markerCount = System.Text.RegularExpressions.Regex.Matches(
                b.MergedText ?? string.Empty, @"(?:上一棒|下一棒|前一棒|后一棒)").Count;
            var namedCount = (b.PrevName is { Length: > 0 } ? 1 : 0) + (b.NextName is { Length: > 0 } ? 1 : 0);
            if (markerCount > 0 && namedCount < markerCount)
                b.HasUnlinkedNeighbor = true;
            if (markerCount == 0)
                b.HasNoNeighborMarkers = true;

            if (b.HasUnlinkedNeighbor)
                b.Conflicts.Add("异常：标注了上一棒/下一棒但没有正确 @ 出对方，需人工补全");

            if (b.IsUnattributable)
                b.Conflicts.Add(b.DeclaredTime is null
                    ? "无法归属：正文里找不到棒号（需人工保存页面兜底）"
                    : $"无法归属：正文只有时段 {b.DeclaredTime}，没有棒号（需人工确认）");

            if (b.TimeDeviationMinutes is { } dev && Math.Abs(dev) > DeviationWarnMinutes)
                b.Notes.Add($"发布时刻与声明时段差 {dev} 分钟（超过 ±{DeviationWarnMinutes} 分钟阈值）");

            if (b.DeclaredTime is null && (b.BarNumber.HasValue || b.IsEx))
                b.Conflicts.Add("缺少声明时段");

            if (b.PrevName is null && b.BarNumber is > 1)
                b.Notes.Add("未标注上一棒");
            if (b.NextName is null && b.BarNumber.HasValue && b.BarNumber != result.MaxBar)
                b.Notes.Add("未标注下一棒");
        }

        // ---------- 被排除的卡片：必须在报告里可审计 ----------
        if (result.Ignored.Count > 0)
            result.Warnings.Add($"排除了 {result.Ignored.Count} 条非棒次卡片（重复投稿/插曲/约稿），它们不进入归档；"
                                + "逐条原因见报告的「已排除卡片」一节");

        return result;
    }

    /// <summary>
    /// 无「第N棒」格式时的棒号反演，三级：
    ///   ① 名册（总结动态）——名册顺序就是棒序，见 topic 1348289；
    ///   ② 「时段 = 棒位」网格——正文只写时段的接力（见 topic 1349127），由 <see cref="SlotGrid"/> 推断；
    ///   ③ 都推不出来就留 N/A，交给人工，绝不错位编号。
    /// 只在"本卡没有棒号"时生效，绝不覆盖正文里已写明的棒号。
    /// </summary>
    private static void AssignBarNumbersByTime(
        List<ParsedBar> bars, List<RosterSlot> roster, ValidationResult result, string? tag)
    {
        // 只对"带本活动话题标签 + 有时段"的卡片做时段反演，否则会把无关投稿也编号
        var candidates = bars.Where(b => b.BarNumber is null && !b.IsEx && !b.IsSummary
                                         && b.DeclaredTime is { Length: > 0 }
                                         && (b.HasTopicTag || b.CreatorInText is { Length: > 0 }))
                             .ToList();

        if (roster.Count > 0)
        {
            var byTime = new Dictionary<string, int>(StringComparer.Ordinal);
            int n = 0;
            foreach (var s in roster)
            {
                n++;
                if (!byTime.ContainsKey(s.Time)) byTime[s.Time] = n;
            }
            foreach (var b in candidates)
            {
                if (byTime.TryGetValue(b.DeclaredTime!, out var num))
                {
                    b.BarNumber = num;
                    b.MatchedStyle ??= "名册反演";
                    b.Notes.Add($"正文未写棒号，按名册中同时段的位置反演为第{num}棒");
                }
                else
                {
                    b.Conflicts.Add($"时段 {b.DeclaredTime} 不在总结名册里，无法确定棒号");
                }
            }
            if (candidates.Count > 0) return;

            // 名册在手但没有任何卡片带时段：仍可整体按名册顺序对齐（见 AssignBarNumbersFromRoster）
            return;
        }

        // ---- ② 没有名册：试「时段 = 棒位」网格（时段制接力的通用解法） ----
        var plan = SlotGrid.Infer(bars, tag);
        result.SlotGridNote = plan.Describe();
        result.Warnings.Add(plan.Describe());
        foreach (var w in plan.Warnings) result.Warnings.Add("时段网格：" + w);

        if (!plan.Ok)
        {
            foreach (var b in bars.Where(b => b.BarNumber is null && !b.IsEx && !b.IsSummary && !b.IsDuplicate))
                b.Conflicts.Add("既没有总结名册，也没能建立可靠的时段网格 —— 棒号需人工确认");
            return;
        }

        SlotGrid.Assign(bars, plan, result);

        // 网格赋值后仍有未归属的：把原因写清楚（多半是插曲/约稿/预热）
        foreach (var b in bars.Where(b => b.BarNumber is null && !b.IsEx && !b.IsSummary && !b.IsDuplicate
                                          && b.Conflicts.Count == 0))
        {
            b.Conflicts.Add("未落在时段网格的槽位上 —— 可能是插曲/约稿/预告，需人工确认是否算一棒");
        }
    }

    /// <summary>
    /// 用**总结名册**反演棒号——ai_guild.md 的"收集方法优先级 1"。
    /// 实测动机（topic 1348289）：逐棒正文里既没有"第N棒"也没有时段，
    /// 只有「上一棒@A / 下一棒@B」，单卡无从判断自己是第几棒；
    /// 而主办方的总结动态给出了完整的「时段 → 创作者 → 作品链接」列表。
    /// 三级匹配：① 作品链接里的 opus id == 卡片 id（最可靠）；② 创作者昵称（唯一命中时）；③ 正文里出现该棒的时段。
    /// </summary>
    private static void AssignBarNumbersFromRoster(List<ParsedBar> bars, List<RosterSlot> roster)
    {
        if (roster.Count == 0) return;

        var pending = bars.Where(b => b.BarNumber is null && !b.IsEx && !b.IsSummary).ToList();
        if (pending.Count == 0) return;

        // 已认领的名册槽位（一个棒位只能被一根棒认领，避免同人多帖造成重复编号）
        var used = new HashSet<int>();

        // 名册项编号（1-based，与名册顺序一致）
        var slotByOpusId = new Dictionary<string, (int Num, RosterSlot Slot)>(StringComparer.Ordinal);
        var slotsByName = new Dictionary<string, List<(int Num, RosterSlot Slot)>>(StringComparer.Ordinal);
        for (int i = 0; i < roster.Count; i++)
        {
            var s = roster[i];
            int num = i + 1;
            var id = ExtractOpusId(s.WorkUrl);
            if (id is not null) slotByOpusId.TryAdd(id, (num, s));
            if (s.Creator.Length > 0)
            {
                if (!slotsByName.TryGetValue(s.Creator, out var list)) slotsByName[s.Creator] = list = new();
                list.Add((num, s));
            }
        }

        // ① 作品链接里的 opus id（最可靠）
        foreach (var b in pending)
        {
            if (slotByOpusId.TryGetValue(b.CardId, out var hit) && !used.Contains(hit.Num))
            {
                MarkFromRoster(b, hit.Num, hit.Slot, "名册作品链接匹配");
                used.Add(hit.Num);
            }
        }

        // ② 创作者昵称唯一命中
        foreach (var b in pending.Where(x => x.BarNumber is null))
        {
            var name = b.ResolvedAuthor ?? b.PosterName;
            if (name.Length == 0) continue;
            if (slotsByName.TryGetValue(name, out var list))
            {
                var free = list.FirstOrDefault(x => !used.Contains(x.Num));
                if (free.Slot is not null && list.Count == 1)
                {
                    MarkFromRoster(b, free.Num, free.Slot, "名册创作者匹配");
                    used.Add(free.Num);
                }
            }
        }

        // ③ 正文里出现该棒的时段（如视频标题里的「16：00」）
        foreach (var b in pending.Where(x => x.BarNumber is null))
        {
            if (b.DeclaredTime is not { Length: > 0 }) continue;
            var idx = roster.FindIndex(s => s.Time == b.DeclaredTime);
            if (idx >= 0 && roster.Count(s => s.Time == b.DeclaredTime) == 1 && !used.Contains(idx + 1))
            {
                MarkFromRoster(b, idx + 1, roster[idx], "名册时段匹配");
                used.Add(idx + 1);
            }
        }

        // 仍未归属的：说明是同一人的重复投稿 / 名册未收录
        foreach (var b in pending.Where(x => x.BarNumber is null))
        {
            var name = b.ResolvedAuthor ?? b.PosterName;
            var sameNameSlots = slotsByName.TryGetValue(name, out var l) ? l.Count : 0;
            if (sameNameSlots > 0)
                b.Conflicts.Add($"该创作者在名册里有 {sameNameSlots} 个棒位，{name} 的这条动态无法确定属于哪一棒（疑似同人多帖或名册已收录另一条）");
            else
                b.Conflicts.Add("总结名册里没有该创作者的棒位（可能是陪跑/无关投稿，或名册未收录）");
        }
    }

    private static void MarkFromRoster(ParsedBar bar, int num, RosterSlot slot, string how)
    {
        bar.BarNumber = num;
        bar.MatchedStyle ??= how;
        if (bar.DeclaredTime is null) bar.DeclaredTime = slot.Time;
        bar.Notes.Add($"{how}：总结名册第 {num} 项（{slot.Time}{(slot.Qualifier.Length > 0 ? " " + slot.Qualifier : "")}）");
        if (bar.CreatorInText is null && slot.Creator.Length > 0) bar.CreatorInText = slot.Creator;
    }

    /// <summary>从作品链接里取出 opus id（用于与卡片 id 对齐）。</summary>
    private static string? ExtractOpusId(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/(?:opus|video|read)/([A-Za-z0-9]{6,})");
        if (!m.Success) return null;
        return m.Groups[1].Value;
    }

    public static int TimeToMinutes(string hhmm)
    {
        var parts = hhmm.Split(':');
        if (parts.Length != 2) return int.MaxValue;
        return (int.TryParse(parts[0], out var h) ? h : 0) * 60 + (int.TryParse(parts[1], out var m) ? m : 0);
    }

    /// <summary>
    /// 第二遍：用"该账号代发了几次"来判断它是不是代发型账号（主催号），再回头修正每条的代发判定。
    /// 动机（实测）：第25棒由 texas-engineer 发布，正文写 @texas_quality —— 这是同一个人改名，
    /// 但第24棒的"下一棒"和第26棒的"上一棒"都把他称作 texas-engineer，说明他既是发布者也是创作者。
    /// 若不加这层校正，会被误报成"代发"。
    /// </summary>
    private static void ApplyProxyHeuristic(List<ParsedBar> bars)
    {
        var ordered = bars.Where(b => b.BarNumber.HasValue)
                          .OrderBy(b => b.BarNumber!.Value)
                          .ToList();
        var byNumber = ordered.GroupBy(b => b.BarNumber!.Value)
                              .ToDictionary(g => g.Key, g => g.First());

        // ---- 预扫描：正文没点名创作者时，用邻居的 at 指认来补（这是③链路校验反哺①字段） ----
        foreach (var b in ordered)
        {
            if (b.CreatorInText is { Length: > 0 }) continue;
            var inferred = InferCreatorFromNeighbors(byNumber, b.BarNumber!.Value);
            if (inferred is { Length: > 0 } name)
            {
                b.CreatorInText = name;
                b.Notes.Add($"正文未点名创作者，由相邻棒的 at 指认推断为 @{name}");
            }
        }

        // ---- 统计：每个发布者有多少次"正文创作者 ≠ 自己" ----
        var mismatchCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in bars)
        {
            if (b.CreatorInText is not { Length: > 0 } who) continue;
            if (TextNormalizer.SameName(who, b.PosterName)) continue;
            mismatchCount[b.PosterName] = mismatchCount.GetValueOrDefault(b.PosterName) + 1;
        }

        var organizers = mismatchCount.Where(kv => kv.Value >= OrganizerThreshold)
                                      .Select(kv => kv.Key)
                                      .ToHashSet(StringComparer.Ordinal);

        foreach (var b in bars)
        {
            bool posterIsOrganizer = organizers.Contains(b.PosterName);

            if (posterIsOrganizer && b.CreatorInText is { Length: > 0 } who)
            {
                // 代发型账号发布 + 正文点名创作者 → 真代发
                b.IsProxy = true;
                b.ProxyName = b.PosterName;
                b.ResolvedAuthor = who;   // 保留发布者空间链接（原作者 UID 未知）
                if (!b.Notes.Any(n => n.StartsWith("代发：", StringComparison.Ordinal)))
                    b.Notes.Add($"代发：发布者 @{b.PosterName}（代发型账号），正文标注创作者 @{who}（原作者 UID 未知）");
            }
            else
            {
                // 非代发型账号：发布者即创作者（实测 who 与 poster 不同多为改名，不是代发）
                b.IsProxy = false;
                b.ProxyName = null;
                b.ResolvedAuthor = b.PosterName;
                b.ResolvedAuthorLink = b.AuthorMid > 0 ? $"https://space.bilibili.com/{b.AuthorMid}" : string.Empty;
                b.Notes.RemoveAll(n => n.StartsWith("代发：", StringComparison.Ordinal));
                if (b.CreatorInText is { Length: > 0 } w && !TextNormalizer.SameName(w, b.PosterName))
                    b.Notes.Add($"正文点名 @{w}，但该账号不是代发型账号 → 按发布者认定（可能是改名，原为「{w}」）");
            }
        }
    }

    /// <summary>
    /// 用邻居指认推断某棒的创作者：第 n-1 棒的"下一棒"与第 n+1 棒的"上一棒"应当都指向第 n 棒的人。
    /// 实测：第21棒正文没点名创作者（写的是「にお/nio」无 @），
    /// 但第20棒"下一棒：にお/nio"、第22棒"上一棒：にお" 一起把它指了出来。
    /// </summary>
    private static string? InferCreatorFromNeighbors(IReadOnlyDictionary<int, ParsedBar> byNumber, int num)
    {
        var candidates = new List<string>(2);
        if (byNumber.TryGetValue(num - 1, out var prev) && prev.NextName is { Length: > 0 } pn) candidates.Add(pn);
        if (byNumber.TryGetValue(num + 1, out var next) && next.PrevName is { Length: > 0 } nn) candidates.Add(nn);
        if (candidates.Count == 0) return null;

        // 多个候选：取最长的（"にお/nio" 优于 "にお"）；只看是否彼此一致（前缀关系即可）
        if (candidates.Count == 1) return candidates[0];
        return SameOrAlias(candidates[0], candidates[1])
            ? candidates.OrderByDescending(c => c.Length).First()
            : candidates[0];
    }

    /// <summary>
    /// 用 at 链独立复核棒序：第 n 棒标注的"下一棒"应当等于第 n+1 棒解析出的创作者；
    /// 反之"上一棒"应当等于第 n-1 棒的创作者。不一致只记警告
    /// （创作者可改名，或标注本身写错——实测有 "にお" vs "にお/nio"、"myu" vs "myueo42" 这类简称差异）。
    /// </summary>
    private static void CrossCheckChain(List<ParsedBar> bars, List<string> warnings)
    {
        // ---- 先补"0:00 那一棒没写第N棒"的情况：第1棒标注的"上一棒"就是它 ----
        PromoteUnnumberedZeroBar(bars);

        var byNumber = bars.Where(b => b.BarNumber.HasValue)
                           .GroupBy(b => b.BarNumber!.Value)
                           .ToDictionary(g => g.Key, g => g.ToList());
        int chainWarnings = 0;

        foreach (var (num, list) in byNumber.OrderBy(kv => kv.Key))
        {
            if (list.Count != 1) continue; // 重复棒号已在断言里报过
            var cur = list[0];

            // 本棒标注的"下一棒" vs 下一棒的实际创作者
            if (cur.NextName is { Length: > 0 } && byNumber.TryGetValue(num + 1, out var nexts) && nexts.Count == 1)
            {
                var nextCreator = CreatorOf(nexts[0]);
                if (!SameOrAlias(cur.NextName, nextCreator))
                    warnings.Add($"棒序复核：第{num}棒标注的下一棒「{cur.NextName}」≠ 第{num + 1}棒创作者「{nextCreator}」");
            }

            // 本棒标注的"上一棒" vs 上一棒的实际创作者
            if (cur.PrevName is { Length: > 0 } && byNumber.TryGetValue(num - 1, out var prevs) && prevs.Count == 1)
            {
                var prevCreator = CreatorOf(prevs[0]);
                if (!SameOrAlias(cur.PrevName, prevCreator))
                    warnings.Add($"棒序复核：第{num}棒标注的上一棒「{cur.PrevName}」≠ 第{num - 1}棒创作者「{prevCreator}」");
            }

            if (++chainWarnings > 40) break;
        }
    }

    /// <summary>
    /// topic 1343535 的首棒正文只写「第1棒 | 00:00」而另一张 0:00 的卡片没写"第N棒"，
    /// 于是"第1棒的上一棒"就把它指认出来了。这里把那种无编号、无上下棒的卡片补成第 0 棒。
    /// </summary>
    private static void PromoteUnnumberedZeroBar(List<ParsedBar> bars)
    {
        var first = bars.FirstOrDefault(b => b.BarNumber == 1 && b.PrevName is { Length: > 0 });
        if (first is null) return;
        var prevName = first.PrevName!;

        if (bars.Any(b => b.BarNumber == 0)) return;

        var phantom = bars.FirstOrDefault(b => b.BarNumber is null && !b.IsEx
                                               && b.PrevName is null && b.NextName is null
                                               && SameOrAlias(b.ResolvedAuthor ?? b.PosterName, prevName));
        if (phantom is null) return;

        phantom.BarNumber = 0;
        phantom.MatchedStyle ??= "0:00 无编号(邻居推断)";
        phantom.Notes.Add("正文没有「第N棒」，由第1棒的上一棒指认推断为第0棒");
    }

    /// <summary>
    /// 取"这一棒的创作者"用于链路复核。
    /// 注意优先级：非代发时以 ResolvedAuthor（= 发布者）为准——实测第25棒正文写 @texas_quality、
    /// 但发布者与相邻棒的指认都是 texas-engineer（同一人改名），此时以发布者为准才对得上链。
    /// </summary>
    private static string CreatorOf(ParsedBar b)
        => b.IsProxy && b.ResolvedAuthor is { Length: > 0 } r
            ? r
            : (b.ResolvedAuthor ?? b.CreatorInText ?? b.PosterName);

    /// <summary>判定"看起来是同一个人的简写"：一方是另一方前缀，或去掉分隔符后互为前缀。</summary>
    private static bool SameOrAlias(string a, string b)
    {
        if (TextNormalizer.SameName(a, b)) return true;

        static string Key(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in TextNormalizer.CleanMention(s))
            {
                if (char.IsWhiteSpace(ch) || ch is '_' or '-' or '.' or '/') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        var ka = Key(a);
        var kb = Key(b);
        if (ka.Length == 0 || kb.Length == 0) return false;
        return ka.StartsWith(kb, StringComparison.Ordinal) || kb.StartsWith(ka, StringComparison.Ordinal);
    }
}
