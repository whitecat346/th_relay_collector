#nullable enable
using System.Text.RegularExpressions;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>从总结动态里抽出的一个棒位。</summary>
public sealed class RosterSlot
{
    /// <summary>时段，如 "0:00"、"5:20"。</summary>
    public string Time { get; set; } = string.Empty;
    /// <summary>时段后的修饰词，如 "特殊棒"、"应援棒"；普通整点棒为空。</summary>
    public string Qualifier { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string WorkUrl { get; set; } = string.Empty;

    public bool IsSpecial => Qualifier.Length > 0;
}

/// <summary>
/// 总结动态解析器 —— 对应 ai_guild.md 里"收集方法优先级 1：根据 tag 中包含的总结专栏得到全部信息"。
///
/// 实测两套格式：
///   ① 逐行式（topic 1348289 的主办总结）：
///        0:00 |@御宫樱寻喵
///        https://www.bilibili.com/opus/1251670295345889287?spm_id_from=...
///        5:20特殊棒 |@鴉CORVUS
///        7:00 |@少鹅e
///   ② 清单式（topic 1343333 的总结专栏）：正文里逐段列 "第N棒 HH:MM 创作者"
///
/// 逐行式是**优先级最高的数据源**：它是主办方自己发布的完整名册，
/// 一次请求就能拿到「时段 → 创作者 → 作品链接」的直接映射，比逐棒猜正文可靠得多。
/// </summary>
public static class SummaryExtractor
{
    /// <summary>「HH:MM 修饰词? |@名字」——总结名册的一行。</summary>
    private static readonly Regex SlotRegex = new(
        @"(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?<q>[^\s|｜丨@]{0,8}?)\s*[|｜丨]\s*@\s*(?<who>[^\s@<（(、,，|｜\r\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>总结动态的特征词。</summary>
    private static readonly Regex SummaryKeywordRegex = new(
        @"圆满结束|收获了|感谢各位|感谢你们|活动平台|回顾这场|全部作者|本届|名册|节目单",
        RegexOptions.Compiled);

    /// <summary>
    /// 判定一张卡片是否为"总结/名册"卡：
    ///   ① 命中特征词（圆满结束/感谢各位…）且能抽出 ≥4 个棒位；或
    ///   ② 正文里罗列了 ≥6 个不同棒号（实测 topic 1101507 的总结卡没有任何特征词，
    ///      只是一口气写了「第一棒0:00@A … 第二棒1:00@B …」，靠棒号密度才能认出来）。
    /// </summary>
    public static bool IsSummaryCard(TopicCard card, out List<RosterSlot> slots)
    {
        slots = ExtractSlots(card.MergedText ?? string.Empty);
        var text = card.MergedText ?? string.Empty;
        if (slots.Count >= 4 && SummaryKeywordRegex.IsMatch(text)) return true;

        var barNumbers = Regex.Matches(text, @"第\s*([0-9]{1,3}|[零〇○一二两三四五六七八九十]{1,4})\s*棒")
                              .Select(m => m.Groups[1].Value)
                              .Distinct()
                              .Count();
        return barNumbers >= 6;
    }

    /// <summary>从文本里抽出所有「时段 |@创作者」，并尽量把紧随其后的作品链接绑上去。</summary>
    public static List<RosterSlot> ExtractSlots(string text)
    {
        var slots = new List<RosterSlot>();
        if (string.IsNullOrWhiteSpace(text)) return slots;

        text = TextNormalizer.Normalize(text);
        var matches = SlotRegex.Matches(text);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var slot = new RosterSlot
            {
                Time = TextNormalizer.NormalizeTime(m.Groups["h"].Value, m.Groups["m"].Value),
                Qualifier = m.Groups["q"].Value.Trim(),
                Creator = TextNormalizer.CleanMention(m.Groups["who"].Value),
            };

            // 该棒位之后、下一个棒位之前，若出现一条 B站链接，视为该棒的作品链接
            int start = m.Index + m.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            if (end > start)
            {
                var segment = text[start..Math.Min(end, start + 400)];
                var link = Regex.Match(segment, @"https?://(?:www\.)?(?:bilibili\.com/(?:opus|video|read)/[^\s\)]+|t\.bilibili\.com/[^\s\)]+)");
                if (link.Success) slot.WorkUrl = link.Value.TrimEnd('?', '&');
            }

            slots.Add(slot);
        }
        return slots;
    }

    /// <summary>
    /// 全卡片扫描：找出总结卡，返回其名册。
    /// 若存在多张候选，取棒位数最多的那张。
    /// </summary>
    public static (TopicCard? Card, List<RosterSlot> Slots) FindRoster(IEnumerable<TopicCard> cards)
    {
        TopicCard? best = null;
        var bestSlots = new List<RosterSlot>();

        foreach (var c in cards)
        {
            if (IsSummaryCard(c, out var slots) && slots.Count > bestSlots.Count)
            {
                best = c;
                bestSlots = slots;
            }
        }
        return (best, bestSlots);
    }

    /// <summary>按时段去重合并多个名册（同一时段只保留第一个）。</summary>
    public static List<RosterSlot> Merge(IEnumerable<RosterSlot> slots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RosterSlot>();
        foreach (var s in slots)
            if (seen.Add(s.Time + "|" + s.Qualifier))
                result.Add(s);
        return result;
    }

    /// <summary>
    /// 推断"话题标签"：在所有卡片里找出现次数最多的 #……#。
    /// 实测用途（topic 1348289）：该活动的棒次正文里**没有"第N棒"**，
    /// 只在视频标题/正文里以「【凤栖白泽庭竹林组24h-7:00】」「上一棒6:00@Yummysan」出现时段，
    /// 而 #凤栖白泽庭竹林组24h# 这个标签是判定"这条动态属于本接力"的最强信号（31/36 条带它）。
    /// </summary>
    public static string? InferTopicTag(IEnumerable<TopicCard> cards, string? topicName = null)
    {
        var cardList = cards.ToList();
        if (cardList.Count == 0) return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in cardList)
        {
            if (string.IsNullOrEmpty(c.MergedText)) continue;
            foreach (Match m in Regex.Matches(c.MergedText, @"#([^#\s]{2,40})#"))
            {
                var tag = m.Groups[1].Value.Trim();
                if (tag.Length < 2) continue;
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }
        if (counts.Count == 0) return null;

        // 覆盖率优先：只有覆盖大部分"带标签卡片"的标签才是本活动的标签。
        // 实测教训：① topic 1343535 里最频繁的是 #因幡帝#（10/34），
        //             而完整话题名 #「百变因幡帝」10.1帝之日创作接力# 覆盖 33/34；
        //           ② topic 1348289 的完整标签只覆盖 15/36 张卡片（参与者的帖子里常省略），
        //             所以分母取"带标签的卡片数"而不是全部卡片，否则会漏掉正确标签。
        int withAnyTag = cardList.Count(c => !string.IsNullOrEmpty(c.MergedText)
                                             && Regex.IsMatch(c.MergedText, @"#[^#\s]{2,40}#"));
        int basis = Math.Max(2, withAnyTag > 0 ? withAnyTag : cardList.Count);
        int minCoverage = Math.Max(2, (int)Math.Ceiling(basis * 0.4));
        var viable = counts.Where(kv => kv.Value >= minCoverage).ToList();
        if (viable.Count == 0) viable = counts.OrderByDescending(kv => kv.Value).Take(1).ToList();

        string? Overlap(string tag)
        {
            if (string.IsNullOrWhiteSpace(topicName)) return null;
            string best = string.Empty;
            for (int i = 0; i < tag.Length; i++)
                for (int j = i + 2; j <= tag.Length; j++)
                {
                    var sub = tag[i..j];
                    if (sub.Length > best.Length && topicName.Contains(sub, StringComparison.Ordinal))
                        best = sub;
                }
            return best.Length >= 2 ? best : null;
        }

        return viable
            .OrderByDescending(kv => Overlap(kv.Key)?.Length ?? 0)
            .ThenByDescending(kv => kv.Key.Length)
            .ThenByDescending(kv => kv.Value)
            .First().Key;
    }

    /// <summary>
    /// 逐棒（**单行**）的「HH:MM [修饰词] | @名字」——topic 1348289 每根棒的真实写法。
    /// 关键：who 字符类里**必须排除 \n**。早期版本漏了它，正则跨行匹配后被"跨行守卫"整体拒掉，
    /// 结果 36 张卡片一条都没解析出时段（表现为 25 条"未归属"）。
    /// </summary>
    private static readonly Regex LineTimeAtNameRegex = new(
        @"(?<![\d:])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?<q>[^\s|｜丨@0-9\n]{0,6})\s*[|｜丨]\s*@\s*(?<who>[^\s@<（(、,，|｜\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>多行式（总结名册）：允许修饰词跨到下一行前，供 SummaryExtractor 专门抽取。</summary>
    private static readonly Regex TimeAtNameRegex = new(
        @"(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?<q>[^|｜丨@\n]{0,10}?)\s*[|｜丨]\s*@\s*(?<who>[^\s@<（(、,，|｜\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>
    /// 视频标题里的时段（实测三种写法，且时间后面可能还跟修饰词）：
    ///   【凤栖白泽庭竹林组24h·16：00】        → 16:00
    ///   【凤栖白泽庭竹林组24h 11:00】         → 11:00
    ///   【凤栖白泽庭竹林组24h-7:00】          → 7:00
    ///   【凤栖白泽庭竹林组24h·应援棒18:30】    → 18:30（时间在修饰词之后）
    /// 关键约束：时间必须**紧跟分隔符**（空格/·/-/：）出现。
    /// 早期版本用了 `\s*`，导致「24h·16：00」里的 `24` 被当成 24:00，
    /// 又因为 "h" 后面没有紧跟 '】' 而整体匹配失败 → 该棒拿不到时段。
    /// </summary>
    private static readonly Regex TitleTimeRegex = new(
        @"(?<![\d:])(?<sep>[ \u00b7·\-—–_|｜:：])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?<q>[^0-9】]{0,8})?】",
        RegexOptions.Compiled);

    /// <summary>"本作为代发，画师：X" / "本作为代发，作者@X" —— 明示代发且给出原作者。</summary>
    private static readonly Regex ProxyCreditRegex = new(
        @"本作(?:为|是)?代发[，,、\s]*(?:画师|作者|绘师)\s*[:：]?\s*@?\s*(?<who>[^\s@<（(、,，|｜\r\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>"HH:MM点棒：@X" —— 时段在前、创作者在后的写法（topic 1343535 的第29棒）。</summary>
    private static readonly Regex PointBarWhoRegex = new(
        @"(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*点棒\s*[：:]?\s*@\s*(?<who>[^\s@<（(、,，|｜]{1,30})",
        RegexOptions.Compiled);

    /// <summary>
    /// 行首「HH:MM @名字」——**没有竖线分隔**的写法。
    /// 实测 topic 1349127 第 23 棒：「17:00 @茶猫祈祷中」（作者龙胆紫苑代发）。
    /// 上面几个正则都要求时间与 @ 之间有 | ｜ 丨，这类帖子会整条拿不到时段，
    /// 于是被当成"不在整点槽位上"的插曲排除掉——棒号会整体错位一格。
    /// </summary>
    private static readonly Regex LineStartTimeAtNameRegex = new(
        @"(?m)^[ \t　]*(?:第)?(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?:棒)?[ \t　]*"
        + @"(?<q>[^\s@＠0-9\n|｜丨]{0,6})?[ \t　]*[@＠]\s*(?<who>[^\s@<（(、,，|｜\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>
    /// 无"第N棒"格式时的补充解析：从正文/标题里取「时段 + 创作者」。
    /// 返回 true 表示找到时段；creator 可能为空（需再用邻居指认补）。
    /// </summary>
    public static bool TryParseTimeAndCreator(string mergedText, bool hasTopicTag, out string? time, out string? qualifier, out string? creator)
        => TryParseTimeAndCreator(mergedText, null, hasTopicTag, out time, out qualifier, out creator);

    /// <summary>
    /// 带字段区分版本的解析：<paramref name="titleText"/> 只由标题类字段拼成。
    /// 必须区分的原因：视频的 duration_text（"01:58"）混进整段文本后会被标题时间正则误当成棒次时间。
    /// </summary>
    public static bool TryParseTimeAndCreator(
        string mergedText,
        string? titleText,
        bool hasTopicTag,
        out string? time,
        out string? qualifier,
        out string? creator)
    {
        time = null; qualifier = null; creator = null;
        if (string.IsNullOrWhiteSpace(mergedText) && string.IsNullOrWhiteSpace(titleText)) return false;

        var text = TextNormalizer.Normalize(mergedText);
        var title = TextNormalizer.Normalize(titleText ?? string.Empty);

        // 1) 标题里的「HH:MM】」最可靠（时间由作者写在标题里）
        if (hasTopicTag && title.Length > 0)
        {
            var t = TitleTimeRegex.Match(title);
            if (t.Success)
            {
                time = TextNormalizer.NormalizeTime(t.Groups["h"].Value, t.Groups["m"].Value);
                if (t.Groups["q"].Success) qualifier = t.Groups["q"].Value.Trim();
            }
        }

        // 2) 「HH:MM点棒：@X」写法
        var pb = PointBarWhoRegex.Match(text);
        if (pb.Success)
        {
            time ??= TextNormalizer.NormalizeTime(pb.Groups["h"].Value, pb.Groups["m"].Value);
            creator = TextNormalizer.CleanMention(pb.Groups["who"].Value);
            return true;
        }

        // 3) 逐棒的「HH:MM [修饰词] | @名字」（单行，who 里不含换行）
        var line = LineTimeAtNameRegex.Match(text);
        if (line.Success && line.Groups["h"].Value.Length <= 2)
        {
            time ??= TextNormalizer.NormalizeTime(line.Groups["h"].Value, line.Groups["m"].Value);
            if (qualifier is null && line.Groups["q"].Success) qualifier = line.Groups["q"].Value.Trim();
            creator ??= TextNormalizer.CleanMention(line.Groups["who"].Value);
        }

        // 3b) 兜底：多行式（总结名册里的一行），仍要求匹配片段内不含换行
        if (time is null)
        {
            var m = TimeAtNameRegex.Match(text);
            if (m.Success && m.Groups["h"].Value.Length <= 2 && !m.Value.Contains('\n'))
            {
                time = TextNormalizer.NormalizeTime(m.Groups["h"].Value, m.Groups["m"].Value);
                creator ??= TextNormalizer.CleanMention(m.Groups["who"].Value);
            }
        }

        // 3c) 行首「HH:MM @名字」（没有竖线分隔）——实测 topic 1349127 的「17:00 @茶猫祈祷中」
        if (time is null)
        {
            var head = LineStartTimeAtNameRegex.Match(text);
            if (head.Success && head.Groups["h"].Value.Length <= 2)
            {
                time = TextNormalizer.NormalizeTime(head.Groups["h"].Value, head.Groups["m"].Value);
                if (qualifier is null && head.Groups["q"].Success) qualifier = head.Groups["q"].Value.Trim();
                creator ??= TextNormalizer.CleanMention(head.Groups["who"].Value);
            }
        }

        // 4) 明示代发的原作者署名
        var pc = ProxyCreditRegex.Match(text);
        if (pc.Success) creator = TextNormalizer.CleanMention(pc.Groups["who"].Value);

        return time is not null || creator is not null;
    }
}
