#nullable enable
using System.Text.RegularExpressions;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>
/// 从卡片正文里解析棒号 / 时段 / 上下棒 / 代发关系。
/// 实测语料里的写法（全部来自 topic_id=1343535，共 34 条卡片）：
///   A 第27棒 | 20:30 | @寒雨笙歌hy          （竖线分隔）
///   B 第 5棒 | 4:00 | @poomi                （棒号前后有空格）
///   C 第4棒 | 3：00 | @格奇_official        （全角冒号）
///   D 第10棒8：00                           （棒号与时段间无分隔符）
///   E 第 25棒 19：30 @texas_quality         （空格分隔）
///   F 第六棒 4：30 @syuugen                 （中文数字！漏掉会误报缺第6棒）
///   G 23:00点棒：@计时器钧                  （只有时段+点棒，没有"第N棒"）
///   H 第28棒 | 21：00 | @试作型拾九          （全角冒号+时间）
/// </summary>
public static class BarParser
{
    private const string NumPat = @"[0-9]{1,3}|[零〇○一二两三四五六七八九十]{1,4}";

    private static readonly Regex BarRegex = new(
        $@"第\s*(?<num>{NumPat})\s*棒", RegexOptions.Compiled);

    private static readonly Regex ExRegex = new(
        @"\b(?:EX|Ex|ex)\s*(?<n>[0-9]{1,2})?\s*棒", RegexOptions.Compiled);

    private static readonly Regex TimeAfterBarRegex = new(
        $@"第\s*(?:{NumPat})\s*棒\s*[|｜丨:：]?\s*(?<h>[0-9]{{1,2}})\s*[:：]\s*(?<m>[0-9]{{2}})",
        RegexOptions.Compiled);

    /// <summary>写法 G："23:00点棒"。</summary>
    private static readonly Regex TimePointBarRegex = new(
        @"(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*点?\s*棒", RegexOptions.Compiled);

    private static readonly Regex WhoRegex = new(
        $@"第\s*(?:{NumPat})\s*棒(?<tail>[^\n]{{0,24}}?)"
        + @"(?<!感谢\s*[:：]?\s*)(?<!鸣谢\s*[:：]?\s*)(?<!来自\s*[:：]?\s*)(?<!画师\s*[:：]?\s*)"
        + @"(?<!作者\s*[:：]?\s*)(?<!由\s*[:：]?\s*)(?<!协力\s*[:：]?\s*)(?<!帮忙\s*[:：]?\s*)"
        + @"@\s*(?<who>[^\s@<（(、,，|｜\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>
    /// 声明的创作者**没有被正确 @ 出来**（B站不会渲染成 &lt;a&gt; 标签）。
    /// 写法：「第18棒 19:00 作者：条（咖方代发）」「第一棒0:00可燃冰块」。
    /// 按用户要求归为异常，交人工处理。
    /// </summary>
    private static readonly Regex WhoUnlinkedRegex = new(
        $@"第\s*(?:{NumPat})\s*棒[^\n]{{0,24}}?(?:作者|画师|绘师|creator)\s*[:：]\s*(?<who>[^\s（(、,，|｜\n]{{1,30}})",
        RegexOptions.Compiled);

    /// <summary>
    /// 紧随棒号、未被 @ 的裸名字（如「第一棒0:00可燃冰块」）。
    /// who 里排除 代发/作者/画师 等尾注词。
    /// </summary>
    private static readonly Regex WhoBareAfterTimeRegex = new(
        $@"第\s*(?:{NumPat})\s*棒\s*[|｜丨:：]?\s*(?<h>[0-9]{{1,2}})\s*[:：]\s*(?<m>[0-9]{{2}})\s*(?<who>[^\s@<（(、,，|｜\n]{{2,30}})",
        RegexOptions.Compiled);

    /// <summary>出现"上一棒/下一棒"字样、但后面既没有 @ 也没有可识别名字。</summary>
    private static readonly Regex NeighborPerspectiveRegex = new(
        @"(?:上一棒|下一棒|前一棒|后一棒)\s*(?<tail>[^\n]{0,20})", RegexOptions.Compiled);

    /// <summary>"X（Y代发）" —— 实测 topic 1101507：「第18棒 19:00 @C1cada_3301 （咖方代发）」「作者：条（咖方代发）」。</summary>
    private static readonly Regex ProxyForCurrentRegex = new(
        @"(?<orig>[^\s@<（(、,，|｜\n]{1,30})\s*[（(]\s*@?\s*(?<proxy>[^\s@（()）、,，\n]{1,20})\s*代(?:发|投|发布)",
        RegexOptions.Compiled);

    /// <summary>
    /// 次要署名："感谢@X"、"约稿来自@X"、"画师：@X"——在有"第N棒 | HH:MM"格式的帖子里，
    /// 这些不是本棒的创作者。实测 topic 1348289 里「约稿来自@伊达大羊驼大人」曾被误当创作者。
    /// 这里在 @ 之前用固定宽度负向回顾做守卫（关键字不超过 4 个汉字，回顾窗口 5 个字符足够）。
    /// </summary>
    private static readonly Regex PrevRegex = new(
        @"(?<!感谢\s)(?<!鸣谢\s)(?<!来自\s)(?<!画师\s)(?<!作者\s)(?<!由\s)(?<!协力\s)(?<!帮忙\s)"
        + @"(?:上一棒|前一棒|上棒)\s*[|｜丨:：]?\s*@?\s*(?<name>[^\s@<（(、,，|｜]{1,30})",
        RegexOptions.Compiled);

    private static readonly Regex NextRegex = new(
        @"(?<!感谢\s)(?<!鸣谢\s)(?<!来自\s)(?<!画师\s)(?<!作者\s)(?<!由\s)(?<!协力\s)(?<!帮忙\s)"
        + @"(?:下一棒|后一棒|下棒)\s*[|｜丨:：]?\s*@?\s*(?<name>[^\s@<（(、,，|｜]{1,30})",
        RegexOptions.Compiled);

    /// <summary>
    /// "X（@Y 代发/代投）" —— 只在**紧跟上一棒/下一棒人名之后**才认，避免把无关句子里的"代发"误绑到本条。
    /// 实测：第27棒正文写 "上一棒：わふもるり（@沐南沐南沐南 代发）"，指的是上一棒由沐南代发，不是本条。
    /// </summary>
    private static readonly Regex PrevProxyRegex = new(
        @"(?:上一棒|前一棒)\s*[|｜丨:：]?\s*(?<orig>[^\s（(、,，|｜]{1,30})\s*[（(]\s*@?\s*(?<proxy>[^\s@（()）、,，]{1,30})\s*代(?:发|投|发布)",
        RegexOptions.Compiled);

    private static readonly Regex NextProxyRegex = new(
        @"(?:下一棒|后一棒)\s*[|｜丨:：]?\s*(?<orig>[^\s（(、,，|｜]{1,30})\s*[（(]\s*@?\s*(?<proxy>[^\s@（()）、,，]{1,30})\s*代(?:发|投|发布)",
        RegexOptions.Compiled);

    public static ParsedBar Parse(TopicCard card, string? topicTag = null)
    {
        var bar = new ParsedBar
        {
            CardId = card.Id,
            AuthorMid = card.AuthorMid,
            PosterName = card.AuthorName,
            PubTs = card.PubTs,
            OpusUrl = card.OpusUrl,
        };

        var text = TextNormalizer.Normalize(card.MergedText ?? string.Empty);
        bar.MergedText = text;
        bar.HasTopicTag = !string.IsNullOrEmpty(topicTag) && text.Contains(topicTag, StringComparison.Ordinal);
        bar.WorkKindLabel = InferWorkKind(card.Type, text);
        if (string.IsNullOrWhiteSpace(text))
        {
            bar.Notes.Add("卡片无正文");
            return bar;
        }

        // ---- 棒号：EX → 第N棒 → 仅"点棒"（写法 G，需人工确认棒号） ----
        var ex = ExRegex.Match(text);
        var numMatch = BarRegex.Match(text);
        var pointBar = TimePointBarRegex.Match(text);

        if (ex.Success && !numMatch.Success)
        {
            bar.IsEx = true;
            bar.BarRaw = "EX" + (ex.Groups["n"].Success ? ex.Groups["n"].Value : string.Empty);
            bar.ExIndex = ex.Groups["n"].Success && int.TryParse(ex.Groups["n"].Value, out var ei) ? ei : null;
            bar.MatchedStyle = "EX";
        }
        else if (numMatch.Success)
        {
            bar.BarRaw = numMatch.Groups["num"].Value;
            bar.BarNumber = TextNormalizer.ChineseToInt(bar.BarRaw);
            bar.MatchedStyle = Regex.IsMatch(bar.BarRaw, "^[0-9]+$") ? "第N棒" : "第N棒(中文数字)";
            if (bar.BarNumber is null)
                bar.Conflicts.Add($"棒号「{bar.BarRaw}」无法转成数字");
        }
        else if (pointBar.Success)
        {
            // 写法 G：只能拿到时段，棒号需要人工确认（实测：23:00点棒 → 第29棒）
            bar.MatchedStyle = "点棒式(无棒号)";
            bar.Notes.Add("正文只有「HH:MM点棒」，没有「第N棒」——棒号需人工确认");
        }

        // ---- 时段（棒号后优先，其次"HH:MM点棒"） ----
        var t = TimeAfterBarRegex.Match(text);
        if (t.Success)
            bar.DeclaredTime = TextNormalizer.NormalizeTime(t.Groups["h"].Value, t.Groups["m"].Value);
        else if (pointBar.Success)
            bar.DeclaredTime = TextNormalizer.NormalizeTime(pointBar.Groups["h"].Value, pointBar.Groups["m"].Value);
        else if (bar.BarNumber is null && !bar.IsEx)
        {
            // 无"第N棒"格式（实测 topic 1348289）：退回「HH:MM@名字」/ 标题「HH:MM】」写法。
            // 标题类字段单独拼出来传进去，避免 video 的 duration_text（"01:58"）被误当成棒次时间。
            var titleText = string.Join(" ⏎ ", card.TextSources
                .Where(kv => CardTextExtractor.IsTitleField(kv.Key))
                .Select(kv => kv.Value));
            if (SummaryExtractor.TryParseTimeAndCreator(text, titleText, bar.HasTopicTag, out var t2, out var q2, out var c2))
            {
                bar.DeclaredTime = t2;
                bar.MatchedStyle = c2 is { Length: > 0 } ? "HH:MM@名字" : "HH:MM(标题)";
                if (c2 is { Length: > 0 }) bar.CreatorInText = c2;
                if (q2 is { Length: > 0 })
                    bar.Notes.Add($"时段带修饰词「{q2}」");
            }
        }

        // ---- 正文里的创作者（先试 @ 形式，再试"作者：X"与裸名字兜底） ----
        var who = WhoRegex.Match(text);
        if (who.Success)
            bar.CreatorInText = TextNormalizer.CleanMention(who.Groups["who"].Value);
        else
        {
            var unlinked = WhoUnlinkedRegex.Match(text);
            if (unlinked.Success)
                bar.UnlinkedCreator = TextNormalizer.CleanMention(unlinked.Groups["who"].Value);
            else
            {
                var bare = WhoBareAfterTimeRegex.Match(text);
                if (bare.Success)
                {
                    var cand = TextNormalizer.CleanMention(bare.Groups["who"].Value);
                    if (cand.Length >= 2 && !cand.Contains('棒') && !cand.Contains("代发") && !cand.Contains("作者"))
                        bar.UnlinkedCreator = cand;
                }
            }
            if (bar.UnlinkedCreator is { Length: > 0 })
                bar.Conflicts.Add($"异常：创作者「{bar.UnlinkedCreator}」没有被正确 @ 出来（B站不会渲染成链接），需人工处理");
        }

        // ---- 本条的代发标注：「@原作者（某某代发）」 ----
        var proxyForCurrent = ProxyForCurrentRegex.Match(text);
        if (proxyForCurrent.Success)
        {
            bar.HasProxyAnnotation = true;
            bar.ProxyName = TextNormalizer.CleanMention(proxyForCurrent.Groups["proxy"].Value);
            var orig = TextNormalizer.CleanMention(proxyForCurrent.Groups["orig"].Value);
            if (orig.Length >= 1 && !orig.Contains('棒')) bar.CreatorInText = orig;
        }

        // ---- 上下棒（去掉包裹的方括号，实测有 "[めんどう15]" 这种写法） ----
        var prev = PrevRegex.Match(text);
        if (prev.Success) bar.PrevName = CleanLinkName(prev.Groups["name"].Value);
        var next = NextRegex.Match(text);
        if (next.Success) bar.NextName = CleanLinkName(next.Groups["name"].Value);

        // ---- 代发：只认"紧跟上一棒人名之后"的括号标注（这是别人的属性，不是本条的） ----
        var prevProxy = PrevProxyRegex.Match(text);
        if (prevProxy.Success)
        {
            bar.PrevOriginalAuthor = TextNormalizer.CleanMention(prevProxy.Groups["orig"].Value);
            bar.PrevProxyName = TextNormalizer.CleanMention(prevProxy.Groups["proxy"].Value);
        }
        var nextProxy = NextProxyRegex.Match(text);
        if (nextProxy.Success && bar.PrevProxyName is null)
        {
            bar.PrevOriginalAuthor = TextNormalizer.CleanMention(nextProxy.Groups["orig"].Value);
            bar.PrevProxyName = TextNormalizer.CleanMention(nextProxy.Groups["proxy"].Value);
        }

        ResolveAuthor(bar, card);
        return bar;
    }

    /// <summary>
    /// 判定本条的实际创作者，以及"发布者 vs 创作者"关系。
    /// 实测依据：主催号（沐南沐南沐南 mid=351852362）代发了大量棒次，
    /// 此时正文里紧跟棒号的 @名字 才是创作者；但空间链接仍指向发布者（否则链接点不开）。
    /// 关键：只有 who ≠ poster 时才是"本条代发"，绝不能因为上一棒/下一棒的代发标注而误判本条。
    /// </summary>
    private static void ResolveAuthor(ParsedBar bar, TopicCard card)
    {
        var who = bar.CreatorInText;

        if (who is not null && !TextNormalizer.SameName(who, card.AuthorName))
        {
            bar.IsProxy = true;
            bar.ProxyName = card.AuthorName;
            bar.ResolvedAuthor = who;
            bar.ResolvedAuthorLink = card.SpaceUrl;
            bar.Notes.Add($"代发：发布者 @{card.AuthorName}，正文标注创作者 @{who}（原作者 UID 未知）");
        }
        else
        {
            bar.IsProxy = false;
            bar.ProxyName = null;
            bar.ResolvedAuthor = card.AuthorName;
            bar.ResolvedAuthorLink = card.SpaceUrl;
        }

        if (bar.AuthorMid <= 0)
            bar.Conflicts.Add("卡片缺少作者 mid，无法生成空间链接");
    }

    /// <summary>清理被方括号/书名号包裹的名字。</summary>
    private static string CleanLinkName(string s)
    {
        s = TextNormalizer.CleanMention(s);
        s = s.Trim('[', ']', '【', '】', '「', '」');
        return s.Trim();
    }

    /// <summary>
    /// 把 B站 的卡片类型映射到既有 CSV 的"类型"词表（插画/视频/文章…）。
    /// 实测既有 683 行的词表：插画 436 / 文章 78 / 视频 42 / 插图 39 / 图片 29 / 漫画 8 / MMD 5 / 音乐 4 …
    /// 这里只能给出"形态级"的粗标签（DRAW→插画、AV→视频、ARTICLE→文章），
    /// 更细的类型（MMD/手书/COS/音乐…）需要从标题正文或人工确认得出 —— 因此它只是草稿值。
    /// </summary>
    public static string InferWorkKind(string? cardType, string text)
    {
        var t = cardType ?? string.Empty;
        if (t.Contains("DRAW", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("COS", StringComparison.OrdinalIgnoreCase) || text.Contains("cosplay", StringComparison.OrdinalIgnoreCase)) return "COS";
            if (text.Contains("MMD", StringComparison.OrdinalIgnoreCase)) return "MMD";
            if (text.Contains("手书", StringComparison.Ordinal)) return "手书";
            if (text.Contains("漫画", StringComparison.Ordinal)) return "漫画";
            return "插画";
        }
        if (t.Contains("AV", StringComparison.OrdinalIgnoreCase)) return "视频";
        if (t.Contains("ARTICLE", StringComparison.OrdinalIgnoreCase)) return "文章";
        if (t.Contains("FORWARD", StringComparison.OrdinalIgnoreCase)) return "转发";
        if (t.Contains("WORD", StringComparison.OrdinalIgnoreCase)) return "动态";
        return string.Empty;
    }

    /// <summary>计算"发布时刻"与"声明时段"的偏差（分钟）。</summary>
    public static void ComputeDeviation(ParsedBar bar)
    {
        if (bar.PubTs <= 0 || string.IsNullOrEmpty(bar.DeclaredTime)) return;
        if (!TimeSpan.TryParse(bar.DeclaredTime + ":00", out var declared)) return;

        var local = DateTimeOffset.FromUnixTimeSeconds(bar.PubTs).ToLocalTime();
        var declaredToday = local.Date.Add(declared);
        var diff = (local - declaredToday).TotalMinutes;
        bar.TimeDeviationMinutes = (int)Math.Round(diff);
    }
}
