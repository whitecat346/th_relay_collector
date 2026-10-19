#nullable enable
using System.Text.RegularExpressions;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「reader」
// 目标：从一段帖子文本（正文/评论/简介合并后）里抽出「上一棒 / 下一棒 / 棒号 / 时段 / 末棒标记」。
// 只允许改本文件与 Chain/PostReader.cs。
//
// 硬性要求（来自 m01437 用户指正 + Q3）：
//   1. 接力是「@ 下一个人的名字」驱动，**不是**「下一个人的链接」——定时发布时链接还不存在，
//      所以从 NeighborLink 里只能拿 Name/Uid，绝不要指望 RawText 里有可用链接。
//   2. 末棒显式标记（最后一棒/完结/收尾/收官/终棒 且指自己）优先于任何「下一棒」文字。
//   3. 棒号低可信：接力学里错标漏标很常见，抽不到就留空，不要猜。
//   4. 视频的棒信息常在评论里（简介只给棒数），所以输入是 MergedText 而不是 BodyText。
//
// 实现要点（为什么这样切）：
//   * 全部正则在 TextNormalizer.Normalize 之后的文本上跑：全角数字统一成半角，
//     「第８棒」「14：00」才不会漏。Normalize 是逐字符 1:1 替换，所以下标可以直接
//     回查原始 MergedText 的行，RawText / LastMarkRaw 依然取原文。
//   * 抽「本条自己的」棒号/时段/代发之前，先把「上一棒：…」「下一棒：…」所在的整段
//     子句挖空（MaskNeighborClauses）——否则「上一棒：第14棒 21:00」会把邻居的棒号
//     和时段算到本条头上。末棒标记必须在挖空之前判（它可能就写在「下一棒」那句里，
//     例如「下一棒：咱就是最后一棒@自己」）。
//   * 段落之间、评论之间都可能是换行，所以「上一棒」后面的名字只在同一行里找，
//     找不到就放弃（宁缺毋滥，棒号/邻居都是低可信字段）。
// ===========================================================================

/// <summary>把帖子文本解析成邻居与棒事实（纯函数，不发请求）。</summary>
public static class NeighborExtractor
{
    // 棒号：阿拉伯数字或中文数字（「第六棒」「第二十四棒」）
    private const string NumberPattern = @"[0-9]{1,3}|[零〇○一二两三四五六七八九十]{1,4}";

    private static readonly Regex BarRegex = new(
        @"第\s*(?<num>" + NumberPattern + @")\s*棒", RegexOptions.Compiled);

    /// <summary>没有「第」的写法（「24棒」）；前面紧邻数字/中文数字时不算，免得切进「第15棒」。</summary>
    private static readonly Regex BareBarRegex = new(
        @"(?<!第)(?<![\d零〇○一二两三四五六七八九十])(?<num>[0-9]{1,2})\s*棒(?!号)", RegexOptions.Compiled);

    private static readonly Regex ExRegex = new(
        @"(?<![A-Za-z])(?<kw>EX|Ex|ex)\s*(?<num>[0-9]{1,2})?\s*棒", RegexOptions.Compiled);

    private static readonly Regex ExtraBarRegex = new(@"(?<kw>额外棒|彩蛋棒)", RegexOptions.Compiled);

    /// <summary>棒号后紧跟的时段（「第15棒 23:00」）。</summary>
    private static readonly Regex TimeAfterBarRegex = new(
        @"[|｜丨:：·\s]*(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})(?![0-9])", RegexOptions.Compiled);

    /// <summary>标题里的时段：`【…24h·16：00】`。</summary>
    private static readonly Regex TitleTimeRegex = new(
        @"【[^】]{0,60}?(?<![\d:])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?<q>[^0-9】]{0,8})?】",
        RegexOptions.Compiled);

    /// <summary>独立时段必须紧跟「棒/代发/发布…」才算本条时段，避免把 duration_text（01:58）当时段。</summary>
    private static readonly Regex StandaloneTimeRegex = new(
        @"(?<![\d:])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*点?\s*(?:棒|代发|代投|发布|投稿|开始|更新)",
        RegexOptions.Compiled);

    private static readonly Regex PointBarRegex = new(
        @"(?<![\d:])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*点\s*棒", RegexOptions.Compiled);

    /// <summary>
    /// 行首的时段 + 下一个接棒人：「17:00 @茶猫祈祷中」。
    /// 实测这一场（topic 1349127）全场的棒次都是这么写的：**没有「第N棒」，只有时段 + @**。
    /// 约束：必须在本行开头（允许缩进/前置符号），后面紧跟 @ 或至少空白；
    /// 这样不会把视频时长（01:58）或正文里的时钟（23:00 睡觉）误当成本棒时段。
    /// </summary>
    private static readonly Regex LineStartSlotRegex = new(
        @"(?m)^[ \t　]*(?:第)?(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?:棒)?[ \t　]*(?=@|＠)",
        RegexOptions.Compiled);

    /// <summary>行首时段后面没有 @、但跟着「棒」或「|」的写法：「17:00 | ...」「17:00棒」。</summary>
    private static readonly Regex LineStartSlotLooseRegex = new(
        @"(?m)^[ \t　]*(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})\s*(?:棒|[|｜丨])",
        RegexOptions.Compiled);

    /// <summary>
    /// 行首裸时段（最宽松的兜底）：「16：30！」「18:30 阿雯@藏粮阁魔导书」
    /// 「19:00 作者:@羽衣的皇带鱼」。本场语料（topic 1349127）的时段不一定后接
    /// @ / 棒 / |，所以只认「行首（允许缩进）+ 1~2 位小时 + 冒号 + 2 位分钟」。
    /// `(?![0-9])` 必须保留：否则「172:30」这种数字串会被切出假时段。
    /// 它排在 DeclaredTime 的最后才用，既有写法（第N棒 23:00 / 【…16：00】/ 20:30 |）一个都不会被改坏。
    /// </summary>
    private static readonly Regex LineStartTimeRegex = new(
        @"(?m)^[ \t　]*(?<h>[0-9]{1,2})[:：](?<m>[0-9]{2})(?![0-9])", RegexOptions.Compiled);

    /// <summary>
    /// 行首时段后的 12 小时制标记：实测本场语料「10:08 PM」= 22:08，
    /// 与上午的「10:08」是两个不同的棒，所以必须换算成 24 小时制，不能直接记 10:08。
    /// </summary>
    private static readonly Regex MeridiemRegex = new(
        @"^[ \t]*(?<mer>AM|PM)(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 行首「时段 + 名字@账号」：「18:30 阿雯@藏粮阁魔导书」。
    /// 名字是真正的创作者，@ 后面的账号是发布账号（别人）——代发归属。
    /// 名字串排除 `：:`，这样「5：00 代发：@X」「10:08 约稿：@X」不会被当成这种形态。
    /// </summary>
    private static readonly Regex LineStartSlotNameAccountRegex = new(
        @"(?m)^[ \t　]*(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})(?![0-9])[ \t]*" +
        @"(?<name>[^\s@＠、，。；;：:|｜（）()\[\]【】<>]{1,30})[ \t]*@(?<acct>[^\s@＠、，。；;：:|｜（）()\[\]【】<>]{1,30})",
        RegexOptions.Compiled);

    private static readonly Regex LastMarkRegex = new(
        @"(?<kw>最后一棒|最后1棒|末棒|终棒|收官|收尾|完结)", RegexOptions.Compiled);

    private static readonly Regex StrongFirstMarkRegex = new(@"(?<kw>第一棒|第1棒|首棒)", RegexOptions.Compiled);

    /// <summary>「开局/开场」太日常（小说里随便用），必须带自指才认。</summary>
    private static readonly Regex WeakFirstMarkRegex = new(@"(?<kw>开局|开场)", RegexOptions.Compiled);

    private static readonly Regex SelfHintRegex = new(
        @"(?:咱|我(?!们)|本人|本棒|此棒|这棒|本篇|本帖|到此|就此|这里|这边)", RegexOptions.Compiled);

    private static readonly Regex PrevMarkerRegex = new(
        @"(?<!感谢\s)(?<!鸣谢\s)(?<!来自\s)(?<!画师\s)(?<!作者\s)(?<!由\s)(?<!协力\s)(?<!帮忙\s)(?<kw>上一棒次|上一棒|上一位|上棒|前一棒|前棒)",
        RegexOptions.Compiled);

    private static readonly Regex NextMarkerRegex = new(
        @"(?<!感谢\s)(?<!鸣谢\s)(?<!来自\s)(?<!画师\s)(?<!作者\s)(?<!由\s)(?<!协力\s)(?<!帮忙\s)(?<kw>下一棒次|下一棒|下一位|下棒|后一棒|后棒)",
        RegexOptions.Compiled);

    /// <summary>名字：@ 后面的昵称。真 @ 组件与手打 @ 都长这样。</summary>
    private static readonly Regex AtNameRegex = new(
        @"[@＠](?<name>[^\s@＠、，。：:；;|｜（）()\[\]【】<>]{1,30})", RegexOptions.Compiled);

    private static readonly Regex NeighborTimeRegex = new(
        @"(?<![0-9])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})(?![0-9])", RegexOptions.Compiled);

    /// <summary>「原作者（@代发人 代发）」/「（@代发人 代发）」。orig 若是"代发"这类词说明不是人名。</summary>
    private static readonly Regex ProxyPairRegex = new(
        @"(?<orig>[^\s@＠（(、,，|｜\n]{1,30})\s*[（(]\s*@?\s*(?<proxy>[^\s@＠（()）、,，\n]{1,20})\s*代(?:发|投|发布)",
        RegexOptions.Compiled);

    /// <summary>强代发信号：原作者/原画师…（说明发布者不是真正创作者）。</summary>
    private static readonly Regex StrongCreatorRegex = new(
        @"(?:原作者|原画师|原绘师|原文本|原曲|原案)\s*[:：]\s*@?\s*(?<who>[^\s@＠（(、,，。；;|｜\n]{1,30})",
        RegexOptions.Compiled);

    /// <summary>
    /// 正文署名：「作者：@羽衣的皇带鱼」。这是本场语料里最直接的创作者声明，
    /// 与发布账号不同即视为代发（规范优先级①）。
    /// 注意 `原作者：X` 也会命中（子串），语义一致，无需单独区分。
    /// </summary>
    private static readonly Regex AuthorLabelRegex = new(
        @"作者\s*[:：]\s*@?\s*(?<who>[^\s@＠、，。；;：:|｜（）()\[\]【】<>]{1,30})",
        RegexOptions.Compiled);

    /// <summary>弱署名信号：只写「画师：X」时通常只是署名，不一定是代发，不据此置 IsProxy。</summary>
    private static readonly Regex SoftCreditRegex = new(
        @"(?:画师|绘师|作者)\s*[:：]\s*@?\s*(?<who>[^\s@＠（(、,，。；;|｜\n]{1,30})",
        RegexOptions.Compiled);

    private static readonly Regex ProxyAloneRegex = new(
        @"(?:本作|本条|本篇|本文|本贴|此条)[^。\n]{0,4}代(?:发|投|发布)|代(?:发|投|发布)\s*[（(]",
        RegexOptions.Compiled);

    private static readonly Regex NameTokenRegex = new(
        @"[^\s@＠、，。；;|｜：:（）()\[\]【】<>]{1,30}", RegexOptions.Compiled);

    private static readonly string[] ProxyWords = { "代发", "代投", "代发布", "发布", "投稿", "发布者", "代发者" };

    private static readonly char[] ClauseTrimChars =
        { ' ', '\u3000', '：', ':', '|', '｜', '丨', '、', '，', ',', '-', '—', '·', '~', '～' };

    /// <summary>
    /// 就地填充 <paramref name="facts"/> 的 Prevs / Nexts / LastMark / FirstMark /
    /// BarRaw / BarNumber / DeclaredTime / IsEx / CreatorInText / IsProxy / ProxyName。
    /// </summary>
    public static void Extract(PostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var mergedRaw = string.IsNullOrWhiteSpace(facts.MergedText)
            ? $"{facts.BodyText}\n{facts.CommentsText}"
            : facts.MergedText;
        var text = TextNormalizer.Normalize(mergedRaw);
        var title = TextNormalizer.Normalize(facts.Title ?? string.Empty);

        // ① 末棒标记最先判：它优先于任何「下一棒」文字（硬性要求 2）。
        DetectLastMark(facts, text, mergedRaw, title);

        // ② 抽本条棒号/时段/代发前，把邻居子句挖空（见文件头「实现要点」）。
        var selfText = MaskNeighborClauses(text);

        DetectFirstMark(facts, selfText, title, mergedRaw);
        DetectBarAndTime(facts, selfText, title);
        DetectProxy(facts, selfText);
        DetectNeighbors(facts, text, mergedRaw);

        // ③ 末棒：原文里可能同时写了「下一棒」（最后一棒的人常写"咱就是最后一棒"却留着
        //    「下一棒」模板），按硬性要求 2 以末棒为准，清掉 Nexts 并留痕。
        if (facts.LastMark && facts.Nexts.Count > 0)
        {
            facts.Nexts.Clear();
            facts.ReaderWarnings.Add("原文有末棒标记，已按「末棒优先」忽略同文中的「下一棒」字样。");
        }
    }

    // -----------------------------------------------------------------------
    // 末棒 / 首棒
    // -----------------------------------------------------------------------

    private static void DetectLastMark(PostFacts facts, string text, string rawText, string title)
    {
        foreach (Match match in LastMarkRegex.Matches(text))
        {
            if (!IsSelfReference(text, match.Index, facts)) continue;
            facts.LastMark = true;
            facts.LastMarkRaw = LineAt(rawText, match.Index, 60);
            return;
        }

        foreach (Match match in LastMarkRegex.Matches(title))
        {
            if (!IsSelfReference(title, match.Index, facts)) continue;
            facts.LastMark = true;
            facts.LastMarkRaw = "标题：" + LineAt(title, match.Index, 40);
            return;
        }
    }

    private static void DetectFirstMark(PostFacts facts, string selfText, string title, string rawText)
    {
        // 「第一棒/首棒」是强标记；「开局/开场」必须带自指（小说里这些词太常见）。
        var strong = StrongFirstMarkRegex.Match(selfText);
        if (strong.Success)
        {
            facts.FirstMark = true;
            facts.FirstMarkRaw = LineAt(rawText, strong.Index, 60);
            return;
        }
        if (StrongFirstMarkRegex.IsMatch(title))
        {
            facts.FirstMark = true;
            facts.FirstMarkRaw = "标题：" + Truncate(title, 40);
            return;
        }

        foreach (Match match in WeakFirstMarkRegex.Matches(selfText))
        {
            if (!IsSelfReference(selfText, match.Index, facts)) continue;
            facts.FirstMark = true;
            facts.FirstMarkRaw = LineAt(rawText, match.Index, 60);
            return;
        }
        foreach (Match match in WeakFirstMarkRegex.Matches(title))
        {
            if (!IsSelfReference(title, match.Index, facts)) continue;
            facts.FirstMark = true;
            facts.FirstMarkRaw = "标题：" + Truncate(title, 40);
            return;
        }
    }

    /// <summary>标记是否指自己：前面一小段有「咱/我/本棒…」，或后面紧跟着 @自己的名字。</summary>
    private static bool IsSelfReference(string text, int index, PostFacts facts)
    {
        var preStart = Math.Max(0, index - 18);
        if (SelfHintRegex.IsMatch(text[preStart..index])) return true;

        var postEnd = Math.Min(text.Length, index + 40);
        if (facts.AuthorName.Length > 0)
        {
            foreach (Match at in AtNameRegex.Matches(text[index..postEnd]))
            {
                if (TextNormalizer.SameName(at.Groups["name"].Value, facts.AuthorName)) return true;
            }
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // 棒号 + 时段
    // -----------------------------------------------------------------------

    private static void DetectBarAndTime(PostFacts facts, string selfText, string title)
    {
        // 标题优先：标题里的「第N棒」通常就是本条自己的棒号，正文可能只复述邻居。
        string? afterBarTime = null;

        var bar = BarRegex.Match(title);
        var source = title;
        if (!bar.Success)
        {
            bar = BarRegex.Match(selfText);
            source = selfText;
        }

        if (bar.Success)
        {
            var numberRaw = bar.Groups["num"].Value;
            facts.BarRaw = numberRaw;
            facts.BarNumber = TextNormalizer.ChineseToInt(numberRaw);
            afterBarTime = TimeAfterBar(source, bar.Index + bar.Length);
        }

        // 没有「第」的「N棒」兜底。
        if (facts.BarRaw is null)
        {
            var bare = BareBarRegex.Match(selfText);
            if (bare.Success)
            {
                var numberRaw = bare.Groups["num"].Value;
                facts.BarRaw = numberRaw;
                facts.BarNumber = TextNormalizer.ChineseToInt(numberRaw);
                afterBarTime ??= TimeAfterBar(selfText, bare.Index + bare.Length);
            }
        }

        // EX / 额外棒 / 彩蛋棒。
        var ex = ExRegex.Match(title);
        if (!ex.Success) ex = ExRegex.Match(selfText);
        if (ex.Success)
        {
            facts.IsEx = true;
            facts.BarRaw ??= ex.Groups["kw"].Value;
            if (facts.BarNumber is null && ex.Groups["num"].Success)
                facts.BarNumber = TextNormalizer.ChineseToInt(ex.Groups["num"].Value);
        }
        else
        {
            var extra = ExtraBarRegex.Match(title);
            if (!extra.Success) extra = ExtraBarRegex.Match(selfText);
            if (extra.Success)
            {
                facts.IsEx = true;
                facts.BarRaw ??= extra.Groups["kw"].Value;
            }
        }

        // 时段优先级：标题【…HH:MM…】→ 棒号后紧跟 → 「HH:MM 代发/棒…」→「HH:MM点棒」。
        facts.DeclaredTime = DeclaredTime(facts, selfText, title, afterBarTime);
    }

    private static string? DeclaredTime(PostFacts facts, string selfText, string title, string? afterBarTime)
    {
        var titleTime = TitleTimeRegex.Match(title);
        if (titleTime.Success)
        {
            return TextNormalizer.NormalizeTime(titleTime.Groups["h"].Value, titleTime.Groups["m"].Value);
        }

        if (afterBarTime is not null) return afterBarTime;

        // 第一行有内容的行若以「HH:MM …」开头，它就是本棒声明（本场每一棒都这么写）。
        // 正文后文里的「18:30点棒」只是引用，属最弱信号，必须排在这条之后，
        // 否则「10:08 PM 阿雯」这种帖会被后文引用抢成 18:30。
        var firstLineTime = FirstContentLineTime(selfText);
        if (firstLineTime is not null) return firstLineTime;

        var standalone = StandaloneTimeRegex.Match(selfText);
        if (standalone.Success)
        {
            return TextNormalizer.NormalizeTime(standalone.Groups["h"].Value, standalone.Groups["m"].Value);
        }

        var pointBar = PointBarRegex.Match(selfText);
        if (pointBar.Success)
        {
            return TextNormalizer.NormalizeTime(pointBar.Groups["h"].Value, pointBar.Groups["m"].Value);
        }

        // 本场（topic 1349127 一类）的写法：第一行就是「17:00 @下一个人」，没有「第N棒」。
        var slot = LineStartSlotRegex.Match(selfText);
        if (slot.Success)
        {
            return TextNormalizer.NormalizeTime(slot.Groups["h"].Value, slot.Groups["m"].Value);
        }
        var slotLoose = LineStartSlotLooseRegex.Match(selfText);
        if (slotLoose.Success)
        {
            return TextNormalizer.NormalizeTime(slotLoose.Groups["h"].Value, slotLoose.Groups["m"].Value);
        }

        // 标题里没有【…】时，如果标题本身写成「第8棒 14:00 …」也能吃到棒后时段：
        var titleBar = BarRegex.Match(title);
        if (titleBar.Success) return TimeAfterBar(title, titleBar.Index + titleBar.Length);

        // 最后兜底：行首裸时段。本场真实语料里「16：30！」「18:30 阿雯@…」
        // 「19:00 作者:@…」都不带 @/棒/| 紧随，上面的精确规则全漏；
        // 放在最后是为了不改坏任何既有写法（第N棒 23:00 / 【…16：00】/ 20:30 |）。
        foreach (Match lineStart in LineStartTimeRegex.Matches(selfText))
        {
            if (TrySlotTime(lineStart, selfText, out var slotTime)) return slotTime;
        }

        return null;
    }

    /// <summary>
    /// 取「第一行有内容的行」行首的 HH:MM 时段；没有则返回 null。
    /// 为什么要单独判：正文里出现「18:30点棒」往往只是引用（如「其实是18:30点棒的插图」），
    /// 而首行行首的时段才是本棒声明，所以它必须排在 StandaloneTimeRegex 之前。
    /// </summary>
    private static string? FirstContentLineTime(string selfText)
    {
        var lineStart = 0;
        while (lineStart < selfText.Length)
        {
            var lineEnd = selfText.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = selfText.Length;
            var line = selfText[lineStart..lineEnd];
            if (line.Trim().Length > 0)
            {
                var match = LineStartTimeRegex.Match(line);
                return match.Success && TrySlotTime(match, line, out var time) ? time : null;
            }

            lineStart = lineEnd + 1;
        }

        return null;
    }

    private static string? TimeAfterBar(string text, int from)
    {
        if (from >= text.Length) return null;
        var tail = text[from..Math.Min(text.Length, from + 24)];
        var match = TimeAfterBarRegex.Match(tail);
        return match.Success
            ? TextNormalizer.NormalizeTime(match.Groups["h"].Value, match.Groups["m"].Value)
            : null;
    }

    /// <summary>校验 regex 里的 h/m 组是否落在合法 24 小时制范围内（小时 0..24、分钟 0..59）。</summary>
    private static bool TryValidHourMinute(Match match)
    {
        if (!int.TryParse(match.Groups["h"].Value, out var hour)) return false;
        if (!int.TryParse(match.Groups["m"].Value, out var minute)) return false;
        return hour is >= 0 and <= 24 && minute is >= 0 and <= 59;
    }

    /// <summary>
    /// 行首时段 → 「H:mm」（24 小时制、分钟两位、半角冒号）。
    /// 顺带处理 12 小时制标记：本场语料「10:08 PM」= 22:08，与上午的「10:08」是两个不同的棒。
    /// </summary>
    private static bool TrySlotTime(Match match, string text, out string time)
    {
        time = string.Empty;
        if (!TryValidHourMinute(match)) return false;
        if (!int.TryParse(match.Groups["h"].Value, out var hour)) return false;
        if (!int.TryParse(match.Groups["m"].Value, out var minute)) return false;

        var tailStart = match.Index + match.Length;
        if (tailStart < text.Length)
        {
            var tail = text[tailStart..Math.Min(text.Length, tailStart + 8)];
            var meridiem = MeridiemRegex.Match(tail);
            if (meridiem.Success)
            {
                // 12 小时制下小时只能 1..12；「23:00 PM」这种非法写法直接放弃。
                if (hour is < 1 or > 12) return false;
                var pm = meridiem.Groups["mer"].Value.Equals("PM", StringComparison.OrdinalIgnoreCase);
                hour = pm ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour);
            }
        }

        if (hour is < 0 or > 24) return false;
        time = $"{hour}:{minute:00}";
        return true;
    }

    /// <summary>名字串恰好是 AM/PM 时不是名字（「10:08 PM @X」里的 PM）。</summary>
    private static bool IsMeridiemWord(string word) =>
        word.Length == 2
        && (word.Equals("AM", StringComparison.OrdinalIgnoreCase) || word.Equals("PM", StringComparison.OrdinalIgnoreCase));

    // -----------------------------------------------------------------------
    // 代发 / 原作者
    // -----------------------------------------------------------------------

    private static void DetectProxy(PostFacts facts, string selfText)
    {
        // 规范优先级①~③：正文声明的创作者优先于发布账号，命中即停。
        // 命中后旧的「代发/署名」规则仍然会跑，但它们一律用 ??= 或只置 true，不会覆盖已判定的归属。
        _ = TryAuthorLabel(facts, selfText)
            || TrySlotNameAccount(facts, selfText)
            || TrySlotAtMention(facts, selfText);

        // ①「X（@Y 代发）」：X 是被代发的真正创作者，Y 是代发人。
        var pair = ProxyPairRegex.Match(selfText);
        if (pair.Success)
        {
            var original = TextNormalizer.CleanMention(pair.Groups["orig"].Value);
            var proxy = TextNormalizer.CleanMention(pair.Groups["proxy"].Value);
            if (original.Length > 0 && !IsProxyWord(original))
            {
                facts.IsProxy = true;
                facts.CreatorInText ??= original;
                if (proxy.Length > 0 && !TextNormalizer.SameName(proxy, facts.AuthorName)) facts.ProxyName = proxy;
            }
        }

        // ②「原作者：X」/「原画师：X」：强信号；但鸣谢行里的「原画师」是谢辞，不算归属。
        var strong = StrongCreatorRegex.Match(selfText);
        if (strong.Success && !IsAcknowledgementLine(selfText, strong.Index))
        {
            var who = TextNormalizer.CleanMention(strong.Groups["who"].Value);
            if (who.Length > 0 && !IsProxyWord(who))
            {
                facts.IsProxy = true;
                facts.CreatorInText ??= who;
            }
        }

        // ③「画师：X」这类只是署名，不置 IsProxy（否则普通画师帖全成代发）。
        // 同样跳过「鸣谢…画师：X」「感谢约稿插图作者：X」这种谢辞行。
        foreach (Match soft in SoftCreditRegex.Matches(selfText))
        {
            if (IsAcknowledgementLine(selfText, soft.Index)) continue;
            var who = TextNormalizer.CleanMention(soft.Groups["who"].Value);
            if (who.Length > 0 && !IsProxyWord(who) && !TextNormalizer.SameName(who, facts.AuthorName))
                facts.CreatorInText ??= who;
            break;
        }

        // ④ 只写了「本作为代发」/「代发（…」没写主体。
        if (!facts.IsProxy && ProxyAloneRegex.IsMatch(selfText))
        {
            facts.IsProxy = true;
        }

        if (!facts.IsProxy) return;

        if (facts.CreatorInText is null && facts.ProxyName is null)
            facts.ReaderWarnings.Add("文中标注了代发/代投，但没写清原作者与代发人，无法判断真正创作者。");
        else if (facts.CreatorInText is null)
            facts.ReaderWarnings.Add($"代发：代发人为 {facts.ProxyName}，但正文未写明原作者；发布账号为 {facts.AuthorName}。");
        else
            facts.ReaderWarnings.Add($"代发：正文声明的创作者为 {facts.CreatorInText}；发布账号为 {facts.AuthorName}，两者是否同一人需人工确认。");
    }

    private static bool IsProxyWord(string word) => ProxyWords.Contains(word, StringComparer.Ordinal);

    /// <summary>
    /// 这一行在「作者：/画师：」之前是不是鸣谢语境。
    /// 例：「鸣谢约稿插图作者：@SAM271」是谢辞，不是本条创作者归属（13:00 蔼石那帖就是这种）。
    /// </summary>
    private static bool IsAcknowledgementLine(string text, int matchIndex)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, matchIndex - 1));
        var prefix = text[(lineStart + 1)..matchIndex];
        return prefix.Contains("鸣谢", StringComparison.Ordinal)
            || prefix.Contains("感谢", StringComparison.Ordinal)
            || prefix.Contains("致谢", StringComparison.Ordinal)
            || prefix.Contains("约稿", StringComparison.Ordinal)
            || prefix.Contains("插图", StringComparison.Ordinal);
    }

    /// <summary>
    /// 规范优先级①：正文「作者：@X」——X 就是本条声明的创作者，与发布者不同即视为代发。
    /// 「鸣谢约稿插图作者：@SAM271」这类鸣谢/致谢行不算创作者归属，所以先看「作者：」同一行前面有没有那些词。
    /// </summary>
    private static bool TryAuthorLabel(PostFacts facts, string selfText)
    {
        foreach (Match match in AuthorLabelRegex.Matches(selfText))
        {
            if (IsAcknowledgementLine(selfText, match.Index)) continue;

            var who = TextNormalizer.CleanMention(match.Groups["who"].Value);
            if (who.Length == 0 || IsProxyWord(who)) continue;

            facts.CreatorInText ??= who;
            if (!TextNormalizer.SameName(who, facts.AuthorName)) facts.IsProxy = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 规范优先级②：行首「时段 名字@账号」（18:30 阿雯@藏粮阁魔导书）——
    /// 名字才是创作者，@ 后面的账号是发布账号（别人），判为代发。
    /// </summary>
    private static bool TrySlotNameAccount(PostFacts facts, string selfText)
    {
        foreach (Match match in LineStartSlotNameAccountRegex.Matches(selfText))
        {
            if (!TryValidHourMinute(match)) continue;

            var name = TextNormalizer.CleanMention(match.Groups["name"].Value);
            if (name.Length == 0 || IsMeridiemWord(name) || IsProxyWord(name) || IsSelfPronoun(name)) continue;
            // 名字就是发布者本人：不算代发（例如「10:08 PM @交联纤维素」被误配成「PM@交联纤维素」）。
            if (TextNormalizer.SameName(name, facts.AuthorName)) continue;

            facts.CreatorInText ??= name;
            facts.IsProxy = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 规范优先级③：行首「时段 @名」（17:00 @茶猫祈祷中）——名与发布者不同、
    /// 且**确实出现在真实 @ 组件（Mentions）里**，才算创作者；这样「上一棒：@X」
    /// 「感谢 @X」之类不会被误判。
    /// </summary>
    private static bool TrySlotAtMention(PostFacts facts, string selfText)
    {
        foreach (Match time in LineStartTimeRegex.Matches(selfText))
        {
            if (!TrySlotTime(time, selfText, out _)) continue;

            var lineEnd = selfText.IndexOf('\n', time.Index + time.Length);
            if (lineEnd < 0) lineEnd = selfText.Length;
            var rest = selfText[(time.Index + time.Length)..lineEnd].TrimStart(' ', '\t');
            if (rest.Length == 0 || (rest[0] != '@' && rest[0] != '＠')) continue;

            var at = AtNameRegex.Match(rest);
            if (!at.Success) continue;

            var name = TextNormalizer.CleanMention(at.Groups["name"].Value);
            if (name.Length == 0 || IsProxyWord(name) || IsSelfPronoun(name)) continue;
            if (TextNormalizer.SameName(name, facts.AuthorName)) continue;
            if (!InMentions(facts, name)) continue;

            facts.CreatorInText ??= name;
            facts.IsProxy = true;
            return true;
        }

        return false;
    }

    /// <summary>名字是否真的出现在本条的被 @ 列表里（必须，否则普通正文里的 @ 会被误判成创作者）。</summary>
    private static bool InMentions(PostFacts facts, string name)
    {
        foreach (var mention in facts.Mentions)
        {
            if (mention.Name.Length > 0 && TextNormalizer.SameName(mention.Name, name)) return true;
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // 邻居（上一棒 / 下一棒）
    // -----------------------------------------------------------------------

    private static void DetectNeighbors(PostFacts facts, string text, string rawText)
    {
        var markers = new List<(int Index, bool IsPrev, Match Match)>();
        foreach (Match match in PrevMarkerRegex.Matches(text)) markers.Add((match.Index, true, match));
        foreach (Match match in NextMarkerRegex.Matches(text)) markers.Add((match.Index, false, match));

        var ordinal = 0;
        foreach (var marker in markers.OrderBy(x => x.Index))
        {
            AddNeighbor(facts, text, rawText, marker.Match, marker.IsPrev, ordinal++);
        }
    }

    private static void AddNeighbor(PostFacts facts, string text, string rawText, Match marker, bool isPrev, int ordinal)
    {
        var clause = ClauseAfter(text, marker.Index + marker.Length);
        if (clause.Length == 0) return;

        var (name, uid) = PickName(facts, clause);
        if (name.Length == 0) return;

        // 同一个名字只记一次（原文常把上一棒/下一棒写两遍）。
        if (facts.Prevs.Concat(facts.Nexts).Any(x => TextNormalizer.SameName(x.Name, name))) return;

        var link = new NeighborLink
        {
            IsPrev = isPrev,
            Name = name,
            Uid = uid,
            RawText = LineAt(rawText, marker.Index, 80),
            Ordinal = ordinal,
        };

        var time = NeighborTimeRegex.Match(clause);
        if (time.Success)
        {
            link.DeclaredTime = TextNormalizer.NormalizeTime(time.Groups["h"].Value, time.Groups["m"].Value);
        }

        var bar = BarRegex.Match(clause);
        if (bar.Success) link.BarRaw = bar.Groups["num"].Value;

        var pair = ProxyPairRegex.Match(clause);
        if (pair.Success)
        {
            var original = TextNormalizer.CleanMention(pair.Groups["orig"].Value);
            var proxy = TextNormalizer.CleanMention(pair.Groups["proxy"].Value);
            if (original.Length > 0 && !IsProxyWord(original))
            {
                link.IsProxy = true;
                if (proxy.Length > 0) link.ProxyName = proxy;
            }
        }

        if (isPrev) facts.Prevs.Add(link);
        else facts.Nexts.Add(link);
    }

    /// <summary>标记之后到行尾的那句子句；本行只有标记、名字换行以 @ 开头时并到下一行。</summary>
    private static string ClauseAfter(string text, int from)
    {
        if (from >= text.Length) return string.Empty;

        var end = text.IndexOf('\n', from);
        if (end < 0) end = text.Length;
        var clause = text[from..end];

        var trimmed = clause.Trim(ClauseTrimChars);
        if (trimmed.Length == 0 && end < text.Length - 1)
        {
            var nextEnd = text.IndexOf('\n', end + 1);
            if (nextEnd < 0) nextEnd = text.Length;
            var next = text[(end + 1)..nextEnd].Trim();
            if (next.StartsWith('@') || next.StartsWith('＠')) clause = next;
        }
        return clause;
    }

    /// <summary>从子句里取邻居名字：先认 @（真 @ 与手打 @ 都对），再退化成第一个名字 token。</summary>
    private static (string Name, string? Uid) PickName(PostFacts facts, string clause)
    {
        foreach (Match at in AtNameRegex.Matches(clause))
        {
            var name = TextNormalizer.CleanMention(at.Groups["name"].Value);
            if (name.Length == 0 || IsSelfPronoun(name)) continue;
            if (TextNormalizer.SameName(name, facts.AuthorName)) continue;
            name = TrimMarkerSuffix(name);
            // 原文常见「上一棒：@雾雨梦灵下一棒:@龙胆紫苑」——两个 @ 之间没有分隔符，
            // 正则会把「雾雨梦灵下一棒」当成一个名字。此时用真实 @ 组件名做前缀对齐。
            var fromMention = MatchMentionPrefix(facts, name);
            if (fromMention is not null) return fromMention.Value;
            if (name.Length == 0) continue;
            return (name, LookupUid(facts, name));
        }

        var trimmed = clause.TrimStart(ClauseTrimChars)
            .TrimStart('是', '为', '有', '由', '和', '与', '跟');
        var token = NameTokenRegex.Match(trimmed);
        if (!token.Success) return (string.Empty, null);

        var fallback = TextNormalizer.CleanMention(TrimMarkerSuffix(token.Value));
        if (fallback.Length == 0 || IsSelfPronoun(fallback)) return (string.Empty, null);
        if (TextNormalizer.SameName(fallback, facts.AuthorName)) return (string.Empty, null);
        if (StartsLikeFiller(fallback)) return (string.Empty, null);

        // 既没有 @ 也没有冒号时，子句很可能只是叙述（实测评论「我去，上一棒怎么是 啊」被
        // 抽成邻居「怎么是」，白跑一轮定位）。这种情形只接受能对上真实 @ 组件名的名字。
        var looksDeclaration = clause.Contains(':') || clause.Contains('：') || clause.Contains('@') || clause.Contains('＠');
        if (!looksDeclaration) return MatchMentionPrefix(facts, fallback) ?? (string.Empty, null);

        return (fallback, LookupUid(facts, fallback));
    }

    /// <summary>去掉名字尾部的「下一棒/上一棒」等标记词（无分隔符书写时粘在名字后面）。</summary>
    private static string TrimMarkerSuffix(string name)
    {
        var result = name.Trim();
        string[] suffixes = { "下一棒", "上一棒", "下一位", "上一位", "下一楼", "上一楼" };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (result.Length <= suffix.Length || !result.EndsWith(suffix, StringComparison.Ordinal)) continue;
                result = result[..^suffix.Length].TrimEnd();
                changed = true;
            }
        }
        return result;
    }

    /// <summary>用真实 @ 组件（Mentions）纠正名字：既是校验也是 uid 来源。</summary>
    private static (string Name, string? Uid)? MatchMentionPrefix(PostFacts facts, string name)
    {
        if (name.Length < 2) return null;
        foreach (var mention in facts.Mentions)
        {
            if (mention.Name.Length < 2) continue;
            if (TextNormalizer.SameName(mention.Name, name))
                return (mention.Name, string.IsNullOrWhiteSpace(mention.Uid) ? null : mention.Uid);
            if (name.StartsWith(mention.Name, StringComparison.Ordinal) && name.Length > mention.Name.Length)
                return (mention.Name, string.IsNullOrWhiteSpace(mention.Uid) ? null : mention.Uid);
        }
        return null;
    }

    private static string? LookupUid(PostFacts facts, string name)
    {
        foreach (var mention in facts.Mentions)
        {
            if (!TextNormalizer.SameName(mention.Name, name)) continue;
            if (!string.IsNullOrWhiteSpace(mention.Uid)) return mention.Uid;
        }
        return null;
    }

    /// <summary>「咱/我/本人…」是自指，不是邻居。</summary>
    private static bool IsSelfPronoun(string name)
    {
        if (name.Length == 0) return false;
        if (name.StartsWith('咱') || name.StartsWith('俺')) return true;
        return name is "我" or "本人" or "自己" or "本棒" or "此棒" or "这棒" or "本帖" or "本贴";
    }

    /// <summary>子句退化成名字 token 时常见的填充词开头（"的成绩"、"了两句"）。</summary>
    private static bool StartsLikeFiller(string name)
    {
        return name.StartsWith('的') || name.StartsWith('了') || name.StartsWith('是')
            || name.StartsWith('在') || name.StartsWith('有') || name.StartsWith('都')
            || name.StartsWith('也') || name.StartsWith('还') || name.StartsWith('就')
            || name.StartsWith('被') || name.StartsWith('把') || name.StartsWith('这')
            || name.StartsWith('那') || name.StartsWith('其') || name.StartsWith('之');
    }

    // -----------------------------------------------------------------------
    // 邻居子句屏蔽
    // -----------------------------------------------------------------------

    /// <summary>
    /// 把「上一棒/下一棒」所在子句替换成空格（保留换行）。
    /// 注意：这里只屏蔽本条自己的棒号/时段/代发抽取用的文本；末棒标记与邻居抽取另用自己的文本。
    /// </summary>
    private static string MaskNeighborClauses(string text)
    {
        if (text.Length == 0) return text;

        var chars = text.ToCharArray();
        var maskedAny = false;

        void MaskClause(Match marker)
        {
            var start = marker.Index;
            var lineEnd = text.IndexOf('\n', start);
            if (lineEnd < 0) lineEnd = text.Length;

            var rest = text[(start + marker.Length)..lineEnd].Trim(ClauseTrimChars);
            if (rest.Length == 0 && lineEnd < text.Length - 1)
            {
                var nextEnd = text.IndexOf('\n', lineEnd + 1);
                if (nextEnd < 0) nextEnd = text.Length;
                var next = text[(lineEnd + 1)..nextEnd].Trim();
                if (next.StartsWith('@') || next.StartsWith('＠')) lineEnd = nextEnd;
            }

            for (var i = start; i < lineEnd; i++)
            {
                if (chars[i] != '\n') chars[i] = ' ';
                maskedAny = true;
            }
        }

        foreach (Match match in PrevMarkerRegex.Matches(text)) MaskClause(match);
        foreach (Match match in NextMarkerRegex.Matches(text)) MaskClause(match);

        return maskedAny ? new string(chars) : text;
    }

    // -----------------------------------------------------------------------
    // 小工具
    // -----------------------------------------------------------------------

    /// <summary>取下标所在的那一行（RawText / LastMarkRaw 要原样展示，过长截断）。</summary>
    private static string LineAt(string text, int index, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var at = Math.Clamp(index, 0, text.Length);
        var start = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        var end = text.IndexOf('\n', at);
        if (end < 0) end = text.Length;
        var line = text[start..end].Trim();
        if (line.Length > maxLength) line = line[..maxLength] + "…";
        return line;
    }

    private static string Truncate(string text, int maxLength)
    {
        var trimmed = text.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] + "…" : trimmed;
    }
}
