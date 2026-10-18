#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「reader」
// 目标：把一条帖子（opus 动态/专栏、视频）读成 PostFacts（含上下棒 @、棒号、时段、末棒标记）。
// 只允许改本文件与 Chain/NeighborExtractor.cs。签名已冻结，勿改。
//
// 可用原料（既有代码，直接复用，不要复制粘贴实现）：
//   Data/OpusContent.cs      → OpusContentExtractor.FromHtml（括号配平提取 __INITIAL_STATE__）、ParseComments
//   Data/SingleLinkCollector.cs → 现成的 opus 读取流程（HTML + 评论 + spans）
//   Data/BarParser.cs        → BarParser.Parse(TopicCard, topicTag)、ComputeDeviation
//   Data/SummaryExtractor.cs → InferTopicTag / IsSummaryCard
//   Data/TextNormalizer.cs   → 中文数字、时段归一、昵称比对
//   Chain/ChainHttp.cs       → GetText / GetApiJson（含限速与 412/-352 退避）
//
// 实测结论（决定了下面的写法）：
//   1. opus 的统一入口是 https://www.bilibili.com/opus/<id>：动态（detail.type=0）与
//      专栏（detail.type=1）同一套 __INITIAL_STATE__；t.bilibili.com 页面是移动端 JS 壳，
//      只有一个 3KB 的空 div，**不能**直接 GetText，必须换成 opus 形式（PostRef.Url 已经这么给）。
//   2. FromHtml 只暴露 id/作者/标题/正文/评论，**话题名与 detail.type 不在它的模型里**，
//      所以这里再用 ExtractInitialState 自己补一次（话题名是 RelayName 的首选来源）。
//   3. 评论必须 sort=2（按时间）并且翻页：真正写「上一棒/下一棒」的往往是评论区里
//      UP 自己补的那条，按热度排序（sort=0）实测恒返空。
//   4. 视频的棒信息常在 UP 的置顶评论里（简介只写「第15棒 23:00」），所以正文与评论
//      要分别记 BodyText / CommentsText，MergedText 再去重合并后交给 NeighborExtractor。
//   5. locator 的「文章搜索」阶梯返回的是**专栏 cv 号**（实测 8 位），上游会把它包成
//      PostRef.Opus("<cvId>")，但 opus/<cvId> 只是个 3.5KB 的通用空壳页（没有
//      __INITIAL_STATE__）。所以 ReadOpus 在「取不到 detail 且 id 是纯数字且不足 12 位」时
//      改用 x/article/view 接口兜底；正常 19 位 opus id 绝不走这条路。
// ===========================================================================

/// <summary>读取一条帖子并产出接力学事实。</summary>
public sealed class ChainPostReader : IPostReader
{
    /// <summary>单帖请求上限（正文页/详情接口 + 评论页）。实测正常帖子 1~4 次就够。</summary>
    private const int MaxRequestsPerPost = 12;

    /// <summary>评论最多翻 10 页：带棒信息的评论经常被压在第三页之后，但不能无限翻。</summary>
    private const int MaxCommentPages = 10;

    private const int CommentPageSize = 20;

    /// <summary>文本里的 @（RICH/AT 组件之外的手打 @）：只有名字没有 uid，但必须保留。</summary>
    private static readonly Regex AtInTextRegex = new(
        @"@(?<name>[^\s@、，。：:；;|｜（）()\[\]【】<>]{1,30})", RegexOptions.Compiled);

    /// <summary>裸时段（HH:MM / HH：MM），用于回填 FieldSources 时做宽松定位。</summary>
    private static readonly Regex TimePattern = new(
        @"(?<![0-9])(?<h>[0-9]{1,2})\s*[:：]\s*(?<m>[0-9]{2})(?![0-9])", RegexOptions.Compiled);

    private static readonly Regex ProxyPattern = new("代发|代投|代发布|原作者|原画师|原绘师|原曲|原案", RegexOptions.Compiled);

    /// <summary>专栏接口 content 里的块级换行标签（实测 &lt;br&gt;/&lt;/br&gt;/&lt;/p&gt; 与纯换行混用）。</summary>
    private static readonly Regex HtmlBreakTag = new(
        @"<\s*(?:br|/br|/p|/div|/li|/h[1-6])\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>去掉其余 HTML 标签（a/span/strong/img…），不保留 href/src。</summary>
    private static readonly Regex HtmlAnyTag = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>Quill Delta 里 @ 组件的 link 形如 https://space.bilibili.com/25279854?spm_id_from=…。</summary>
    private static readonly Regex SpaceLinkPattern = new(
        @"space\.bilibili\.com/(?<uid>[0-9]{1,20})", RegexOptions.Compiled);

    private readonly ChainHttp _http;

    public ChainPostReader(ChainHttp http) => _http = http;

    /// <inheritdoc />
    public PostFacts Read(PostRef reference, Action<string>? log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(reference.Id))
            throw new ChainFetchException("PostRef.Id 为空，无法读取帖子。");
        if (reference.Kind == PostKind.Topic)
            throw new ChainFetchException("话题入口下一轮：本轮只读 opus / 视频，话题入口要先展开成棒次卡片。");

        // 请求预算按 ChainHttp.RequestCount 算——它包含内部重试，才是真正的出口次数。
        var baseline = _http.RequestCount;
        return reference.Kind == PostKind.Video
            ? ReadVideo(reference, baseline, log, ct)
            : ReadOpus(reference, baseline, log, ct);
    }

    // -----------------------------------------------------------------------
    // opus（动态 / 专栏）
    // -----------------------------------------------------------------------

    private PostFacts ReadOpus(PostRef reference, int requestBaseline, Action<string>? log, CancellationToken ct)
    {
        var facts = NewFacts(reference);
        log?.Invoke($"读取 opus 页面：{reference.Url}");

        string html;
        try
        {
            html = _http.GetText(reference.Url, allowHtml: true, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 正文页都读不到才算整条失败；评论失败不进这里。
            throw new ChainFetchException($"opus {reference.Id} 页面读取失败：{ex.Message}", ex);
        }

        var content = OpusContentExtractor.FromHtml(html);
        if (content is null)
        {
            // 专栏 cv 号兜底（见文件头第 5 条实测结论）：页面是通用空壳，必须换专栏接口。
            // 判据只用「纯数字且位数 < 12」：opus id 19 位、cv 号 8 位，不会误伤正常 opus。
            if (IsColumnId(reference.Id))
            {
                log?.Invoke($"opus {reference.Id} 页面没有 __INITIAL_STATE__.detail（疑似专栏 cv 号），改用专栏接口读取。");
                return ReadOpusViaArticle(reference, requestBaseline, log, ct);
            }

            throw new ChainFetchException(
                $"opus {reference.Id} 页面里没有 __INITIAL_STATE__.detail（可能已删除、仅登录可见或页面形态变了）。");
        }

        facts.PostId = string.IsNullOrWhiteSpace(content.OpusId) ? reference.Id : content.OpusId;
        facts.AuthorUid = content.AuthorMid;
        facts.AuthorName = content.AuthorName;
        facts.PubTs = content.PubTs;
        facts.Title = NullIfBlank(content.Title);
        facts.BodyText = content.BodyText.Trim();

        facts.FieldSources["AuthorUid"] = "正文";
        facts.FieldSources["AuthorName"] = "正文";
        facts.FieldSources["PubTs"] = "正文";
        facts.FieldSources["BodyText"] = "正文";
        if (facts.Title is not null) facts.FieldSources["Title"] = "标题";

        ReadOpusDetail(html, facts);

        log?.Invoke($"opus 形态：{facts.WorkKindLabel ?? "未知"}；话题：{facts.RelayName ?? "无"}；作者：{facts.AuthorName}");

        var comments = new List<CommentItem>();
        ReadComments(content.CommentOid, content.CommentType, content.AuthorMid, reference.Url,
            comments, facts, requestBaseline, ct, log);

        FillTexts(facts, facts.BodyText, "正文", comments);
        FillMentions(facts, content.BodySpans, comments, "正文");
        NeighborExtractor.Extract(facts);
        FillDerivedSources(facts, "正文");
        return facts;
    }

    // -----------------------------------------------------------------------
    // opus 的专栏 cv 号兜底（x/article/view）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 按专栏 cv 号读文章。为什么不用接口里的 data.opus.opus_id 回读 opus 页：那等于把一次
    /// 读取变成两次页面请求，而专栏接口已经给全了标题/作者/时间/正文/话题，评论另走 type=12。
    /// 字段映射：title → Title、author.mid/name → AuthorUid/AuthorName、publish_time → PubTs、
    /// content（HTML 片段）→ BodyText、topic_info → RelayName/TopicId。
    /// </summary>
    private PostFacts ReadOpusViaArticle(PostRef reference, int requestBaseline, Action<string>? log, CancellationToken ct)
    {
        var facts = NewFacts(reference);
        var cvId = reference.Id.Trim();
        var url = "https://api.bilibili.com/x/article/view?id=" + Uri.EscapeDataString(cvId);
        log?.Invoke($"读取专栏接口：{url}");

        string json;
        try
        {
            json = GetArticleJson(url, reference.Url, log, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChainFetchException($"专栏 {cvId} 接口读取失败：{ex.Message}", ex);
        }

        var articleId = 0L;
        var bodySpans = new List<BodySpan>();
        using (var doc = JsonDocument.Parse(json))
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new ChainFetchException($"专栏 {cvId} 接口没有 data（可能已删除或仅登录可见）。");

            // PostId/Url 保持上游给的 cv 号与 opus URL：链路上要能回跳，换成 19 位 opus id 反而跳不回去。
            articleId = LongProp(data, "id");
            facts.Title = NullIfBlank(StrProp(data, "title"));
            facts.PubTs = LongProp(data, "publish_time");
            if (facts.PubTs <= 0) facts.PubTs = LongProp(data, "ctime");

            if (data.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
            {
                facts.AuthorUid = LongProp(author, "mid");
                facts.AuthorName = StrProp(author, "name") ?? string.Empty;
            }

            facts.BodyText = ArticleContentToText(StrProp(data, "content"), bodySpans);
            facts.WorkKindLabel = "文章";
            facts.FieldSources["WorkKindLabel"] = "接口";
            ReadArticleTopic(data, facts);

            if (facts.Title is not null) facts.FieldSources["Title"] = "接口";
            if (facts.AuthorUid > 0) facts.FieldSources["AuthorUid"] = "接口";
            facts.FieldSources["AuthorName"] = "接口";
            facts.FieldSources["PubTs"] = "接口";
            facts.FieldSources["BodyText"] = "专栏正文";

            log?.Invoke($"专栏接口：作者 {facts.AuthorName}（{facts.AuthorUid}）；话题：{facts.RelayName ?? "无"}；"
                        + $"正文 {facts.BodyText.Length} 字");
        }

        var comments = new List<CommentItem>();
        if (articleId > 0)
        {
            // 专栏评论固定 type=12；oid 用接口返回的 id（与 cv 号相同，但不自己拼更稳）。
            ReadComments(articleId.ToString(CultureInfo.InvariantCulture), 12, facts.AuthorUid, reference.Url,
                comments, facts, requestBaseline, ct, log);
        }
        else
        {
            facts.ReaderWarnings.Add("专栏接口没有返回 id，跳过评论读取（棒信息可能只在评论里）。");
        }

        FillTexts(facts, facts.BodyText, "专栏正文", comments);
        FillMentions(facts, bodySpans, comments, "专栏正文");
        NeighborExtractor.Extract(facts);
        FillDerivedSources(facts, "专栏正文");
        return facts;
    }

    /// <summary>
    /// 专栏接口对匿名请求有独立的软限流：实测连续请求会返回 code=-509「请求过于频繁」，
    /// 隔十几秒再试又好了。ChainHttp 只对页面风控（412/-352）退避，管不到这个接口，
    /// 所以这里做一次有界重试——否则一次瞬时限流会被当成「帖子读不到」，链路直接断。
    /// 实测要等 20~40s 才恢复，所以最多 5 次、间隔 4/8/10/10 秒；其它错误（-404 等）立即抛出。
    /// </summary>
    private string GetArticleJson(string url, string referer, Action<string>? log, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _http.GetApiJson(url, referer, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ChainFetchException ex) when (attempt < maxAttempts && IsSoftRateLimit(ex))
            {
                var wait = TimeSpan.FromSeconds(Math.Min(10, 4 * attempt));
                log?.Invoke($"专栏接口限流（-509），第 {attempt} 次重试前等待 {wait.TotalSeconds:0} 秒。");
                if (ct.WaitHandle.WaitOne(wait))
                    ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>-509 = 请求过于频繁（接口级软限流），与 -352 风控不是一回事。</summary>
    private static bool IsSoftRateLimit(ChainFetchException ex) =>
        ex.Message.Contains("-509", StringComparison.Ordinal)
        || ex.Message.Contains("请求过于频繁", StringComparison.Ordinal);

    /// <summary>专栏接口的 topic_info（{topic_id, topic_name}）语义等同于 opus 的 MODULE_TYPE_TOPIC。</summary>
    private static void ReadArticleTopic(JsonElement data, PostFacts facts)
    {
        if (!data.TryGetProperty("topic_info", out var topic) || topic.ValueKind != JsonValueKind.Object) return;

        var name = NullIfBlank(StrProp(topic, "topic_name"));
        if (name is not null)
        {
            facts.RelayName = name;
            facts.FieldSources["RelayName"] = "话题";
        }

        var topicId = LongProp(topic, "topic_id");
        if (topicId > 0)
        {
            facts.TopicId = topicId;
            facts.FieldSources["TopicId"] = "话题";
        }
    }

    /// <summary>
    /// 是否是专栏 cv 号。opus id 实测 19 位、cv 号实测 8 位，所以「纯数字且不足 12 位」这个
    /// 判据很干净；不认 "cv" 前缀，因为上游 LinkResolver 给的就是裸数字。
    /// </summary>
    private static bool IsColumnId(string id)
    {
        if (id.Length == 0 || id.Length >= 12) return false;
        foreach (var ch in id)
        {
            if (ch is < '0' or > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// 专栏接口的 content 实测有三种形态：纯文本（真换行）、HTML 片段（&lt;br&gt;/&lt;/br&gt; 与换行混用）、
    /// 以及 Quill Delta JSON（{"ops":[{"insert":"…"},{"attributes":{"link":"…"},"insert":"@某人"}]}）。
    /// 三种都要能读：先按 Delta 试（以 { 开头才是候选），不成立再按 HTML/纯文本处理。
    /// </summary>
    private static string ArticleContentToText(string? content, List<BodySpan> spans)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            var deltaText = QuillDeltaToText(trimmed, spans);
            if (deltaText is not null) return deltaText;
        }

        return HtmlToText(content);
    }

    /// <summary>
    /// 解析 Quill Delta。Delta 里 @ 组件是「insert = @昵称 + attributes.link = space.bilibili.com/&lt;mid&gt;」，
    /// 正好能把 uid 补回来（正文 HTML/纯文本里的手打 @ 是拿不到 uid 的）。
    /// 返回 null 表示这不是 Delta（交给 HTML/纯文本分支），解析失败不抛异常。
    /// </summary>
    private static string? QuillDeltaToText(string json, List<BodySpan> spans)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) return null;

            var builder = new StringBuilder();
            foreach (var op in ops.EnumerateArray())
            {
                // insert 只取字符串形态；图片/视频这类 object insert 直接跳过（这里只要有文字的棒信息）。
                if (!op.TryGetProperty("insert", out var insert) || insert.ValueKind != JsonValueKind.String) continue;

                var text = insert.GetString() ?? string.Empty;
                builder.Append(text);

                var link = op.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
                    ? StrProp(attributes, "link")
                    : null;
                if (link is null) continue;

                var linkMatch = SpaceLinkPattern.Match(link);
                if (!linkMatch.Success) continue;

                var name = TextNormalizer.CleanMention(text);
                if (name.Length == 0) continue;
                spans.Add(new BodySpan
                {
                    Text = text,
                    AtName = name,
                    AtMid = linkMatch.Groups["uid"].Value,
                    JumpUrl = link,
                });
            }

            return builder.ToString().Trim();
        }
        catch (JsonException)
        {
            // 正文本身以 { 开头的普通文本（不是 Delta）：返回 null 让它走 HTML/纯文本分支。
            return null;
        }
    }

    /// <summary>
    /// 专栏接口的 content 是 HTML 片段（实测也见过纯文本与 &lt;br&gt; 混用）：先把 br/p 等块级标签
    /// 换成换行，再去标签、反转义实体，最后压掉多余空行。为什么不引 HtmlAgilityPack：
    /// 不引 NuGet，而这里只会出现 br/p/a/span 这几类标签。
    /// </summary>
    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = HtmlBreakTag.Replace(html, "\n");
        text = HtmlAnyTag.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// 从 __INITIAL_STATE__.detail 补 FromHtml 没有的东西：形态（动态/文章）、话题（RelayName 首选）、
    /// 以及 basic.uid 兜底。为什么不让 OpusContent 加字段：那个文件是共用的冻结实现，不属于本任务。
    /// </summary>
    private static void ReadOpusDetail(string html, PostFacts facts)
    {
        var maybeState = OpusContentExtractor.ExtractInitialState(html);
        if (maybeState is not JsonElement state) return;
        if (!state.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object) return;

        if (detail.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.Number)
        {
            // 0 = 动态，1 = 专栏（文章）；其余形态留空，宁可没有标签也不要瞎猜。
            var label = typeEl.GetInt32() switch
            {
                0 => "动态",
                1 => "文章",
                _ => null,
            };
            if (label is not null)
            {
                facts.WorkKindLabel = label;
                facts.FieldSources["WorkKindLabel"] = "接口";
            }
        }

        if (detail.TryGetProperty("basic", out var basic) && basic.ValueKind == JsonValueKind.Object)
        {
            if (facts.AuthorUid <= 0
                && long.TryParse(StrProp(basic, "uid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
                && uid > 0)
            {
                facts.AuthorUid = uid;
            }
        }

        if (!detail.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return;

        foreach (var module in modules.EnumerateArray())
        {
            if (!string.Equals(StrProp(module, "module_type"), "MODULE_TYPE_TOPIC", StringComparison.Ordinal)) continue;
            if (!module.TryGetProperty("module_topic", out var topic) || topic.ValueKind != JsonValueKind.Object) continue;

            var name = NullIfBlank(StrProp(topic, "name"));
            if (name is not null)
            {
                facts.RelayName = name;
                facts.FieldSources["RelayName"] = "话题";
            }

            if (long.TryParse(StrProp(topic, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var topicId)
                && topicId > 0)
            {
                facts.TopicId = topicId;
                facts.FieldSources["TopicId"] = "话题";
            }
        }
    }

    // -----------------------------------------------------------------------
    // 视频
    // -----------------------------------------------------------------------

    private PostFacts ReadVideo(PostRef reference, int requestBaseline, Action<string>? log, CancellationToken ct)
    {
        var facts = NewFacts(reference);
        var id = reference.Id.Trim();

        // av 号先换成 aid：view 接口两种都收，但统一成 aid 后评论接口就不用再换一次。
        var query = id.StartsWith("av", StringComparison.OrdinalIgnoreCase)
            ? "aid=" + Uri.EscapeDataString(id[2..])
            : "bvid=" + Uri.EscapeDataString(id);
        var url = "https://api.bilibili.com/x/web-interface/view?" + query;
        log?.Invoke($"读取视频详情：{url}");

        string json;
        try
        {
            json = _http.GetApiJson(url, reference.Url, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChainFetchException($"视频 {id} 详情读取失败：{ex.Message}", ex);
        }

        long aid;
        using (var doc = JsonDocument.Parse(json))
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new ChainFetchException($"视频 {id} 详情接口没有 data（可能已失效或仅登录可见）。");

            var bvid = NullIfBlank(StrProp(data, "bvid"));
            facts.PostId = bvid ?? id;
            facts.Title = NullIfBlank(StrProp(data, "title"));
            aid = LongProp(data, "aid");
            var cid = LongProp(data, "cid");
            facts.PubTs = LongProp(data, "pubdate");
            if (facts.PubTs <= 0) facts.PubTs = LongProp(data, "ctime");

            facts.BodyText = ReadVideoDesc(data);
            if (facts.BodyText.Length > 0) facts.FieldSources["BodyText"] = "简介";
            if (facts.Title is not null) facts.FieldSources["Title"] = "标题";

            if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
            {
                facts.AuthorUid = LongProp(owner, "mid");
                facts.AuthorName = StrProp(owner, "name") ?? string.Empty;
                facts.FieldSources["AuthorUid"] = "接口";
                facts.FieldSources["AuthorName"] = "接口";
            }
            facts.FieldSources["PubTs"] = "接口";

            facts.WorkKindLabel = "视频";
            facts.FieldSources["WorkKindLabel"] = "接口";
            log?.Invoke($"视频 aid={aid} cid={cid}；简介 {facts.BodyText.Length} 字");
        }

        var comments = new List<CommentItem>();
        if (aid > 0)
        {
            ReadComments(aid.ToString(CultureInfo.InvariantCulture), 1, facts.AuthorUid, reference.Url,
                comments, facts, requestBaseline, ct, log);
        }
        else
        {
            facts.ReaderWarnings.Add("视频详情里没有 aid，跳过评论读取（棒信息可能只在评论里）。");
        }

        FillTexts(facts, facts.BodyText, "简介", comments);
        FillMentions(facts, Array.Empty<BodySpan>(), comments, "评论");
        NeighborExtractor.Extract(facts);
        FillDerivedSources(facts, "简介");
        return facts;
    }

    /// <summary>视频简介：新接口是 desc 字符串，空时退到 desc_v2[].raw_text（老结构）。</summary>
    private static string ReadVideoDesc(JsonElement data)
    {
        var desc = StrProp(data, "desc") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();

        if (data.TryGetProperty("desc_v2", out var descV2) && descV2.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in descV2.EnumerateArray())
            {
                var raw = StrProp(item, "raw_text");
                if (!string.IsNullOrWhiteSpace(raw)) return raw.Trim();
            }
        }
        return string.Empty;
    }

    // -----------------------------------------------------------------------
    // 评论（opus / 视频共用）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 逐页读评论。失败只记 ReaderWarnings，已经读到的页保留——单条评论页挂掉不该让整条帖子失败。
    /// 停止条件：没有下一页（num*size &gt;= count）或本页没有顶层回复；最多 10 页 / 12 次请求。
    /// </summary>
    private void ReadComments(
        string? oid,
        int type,
        long upMid,
        string referer,
        List<CommentItem> sink,
        PostFacts facts,
        int requestBaseline,
        CancellationToken ct,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(oid))
        {
            facts.ReaderWarnings.Add("评论 oid 缺失，跳过评论读取（棒信息可能只在评论里）。");
            return;
        }

        for (var page = 1; page <= MaxCommentPages; page++)
        {
            if (_http.RequestCount - requestBaseline >= MaxRequestsPerPost - 1)
            {
                facts.ReaderWarnings.Add($"已到单帖 {MaxRequestsPerPost} 次请求上限，评论只读到第 {page - 1} 页。");
                break;
            }

            var url = $"https://api.bilibili.com/x/v2/reply?oid={Uri.EscapeDataString(oid)}&type={type}"
                      + $"&sort=2&ps={CommentPageSize}&pn={page}";

            string json;
            try
            {
                json = _http.GetApiJson(url, referer, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                facts.ReaderWarnings.Add($"评论第 {page} 页读取失败（保留已读到的 {page - 1} 页）：{ex.Message}");
                break;
            }

            var parsedCount = 0;
            var hasNext = false;
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    facts.ReaderWarnings.Add($"评论第 {page} 页没有 data，停止翻页。");
                    break;
                }

                var parsed = OpusContentExtractor.ParseComments(data, upMid);
                parsedCount = parsed.Count;
                sink.AddRange(parsed);

                var (count, next) = ReadPageInfo(data, page);
                if (count > 0) facts.CommentCount = count;
                hasNext = next && parsedCount > 0;
            }

            log?.Invoke($"评论第 {page} 页：{parsedCount} 条（累计 {sink.Count} 条）");
            if (!hasNext) break;
        }
    }

    private static (int Count, bool HasNext) ReadPageInfo(JsonElement data, int page)
    {
        var count = 0;
        var num = page;
        var size = CommentPageSize;

        if (data.TryGetProperty("page", out var pageInfo) && pageInfo.ValueKind == JsonValueKind.Object)
        {
            count = IntProp(pageInfo, "count");
            if (pageInfo.TryGetProperty("num", out var numEl) && numEl.ValueKind == JsonValueKind.Number)
                num = numEl.GetInt32();
            if (pageInfo.TryGetProperty("size", out var sizeEl)
                && sizeEl.ValueKind == JsonValueKind.Number
                && sizeEl.GetInt32() > 0)
            {
                size = sizeEl.GetInt32();
            }
        }

        var hasNext = count > 0 && num > 0 && (long)num * size < count;
        return (count, hasNext);
    }

    // -----------------------------------------------------------------------
    // 合并正文 + 评论
    // -----------------------------------------------------------------------

    private static void FillTexts(PostFacts facts, string bodyText, string bodySource, List<CommentItem> comments)
    {
        var body = bodyText.Trim();
        var blocks = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var comment in comments)
        {
            var text = comment.Text.Trim();
            if (text.Length == 0) continue;
            // 评论常整段复述简介/正文；够长才判重，避免把「喵」这种短评论误删。
            if (body.Length >= 8 && body.Contains(text, StringComparison.Ordinal)) continue;
            if (!seen.Add(text)) continue;
            blocks.Add(text);
        }

        facts.CommentsText = blocks.Count == 0 ? string.Empty : string.Join("\n", blocks);
        if (blocks.Count > 0)
        {
            facts.FieldSources["CommentsText"] = "评论";
            facts.FieldSources["CommentCount"] = "接口";
        }

        var builder = new StringBuilder(body);
        if (blocks.Count > 0)
        {
            builder.Append("\n\n【评论区】\n");
            builder.Append(facts.CommentsText);
            facts.FieldSources["MergedText"] = $"{bodySource}+评论";
        }
        else
        {
            facts.FieldSources["MergedText"] = bodySource;
        }

        facts.MergedText = builder.ToString().Trim();
    }

    // -----------------------------------------------------------------------
    // @ 提及
    // -----------------------------------------------------------------------

    /// <summary>
    /// 收集 @ 提及。实测：真正的 @ 组件会带 uid（正文 rich.rid / 评论 members[]），
    /// 但手打的「@名字」是纯文本、没有 uid —— 这类必须保留 Name 并让 Uid=null，
    /// 因为下一棒经常就是手打出来的（定时发布时对方还没发帖，根本配不出 AT 组件）。
    /// </summary>
    private static void FillMentions(
        PostFacts facts,
        IEnumerable<BodySpan> bodySpans,
        IEnumerable<CommentItem> comments,
        string bodySource)
    {
        var merged = facts.MergedText;
        var found = new List<(string Name, string? Uid, int Index)>();
        var fromBody = false;
        var fromComments = false;

        bool TryAdd(string? rawName, string? uid, bool isBody)
        {
            var name = TextNormalizer.CleanMention(rawName ?? string.Empty);
            if (name.Length == 0) return false;
            if (found.Any(x => TextNormalizer.SameName(x.Name, name))) return false;

            var index = merged.IndexOf("@" + name, StringComparison.Ordinal);
            if (index < 0) index = merged.IndexOf(name, StringComparison.Ordinal);
            found.Add((name, NullIfBlank(uid), index < 0 ? int.MaxValue : index));
            if (isBody) fromBody = true;
            else fromComments = true;
            return true;
        }

        foreach (var span in bodySpans)
        {
            if (!string.IsNullOrEmpty(span.AtName) || !string.IsNullOrEmpty(span.AtMid))
                TryAdd(span.AtName, span.AtMid, isBody: true);
        }

        foreach (var comment in comments)
        {
            foreach (var span in comment.Spans)
            {
                if (!string.IsNullOrEmpty(span.AtName) || !string.IsNullOrEmpty(span.AtMid))
                    TryAdd(span.AtName, span.AtMid, isBody: false);
            }
        }

        // 手打 @：位置直接取自匹配下标，用来判断来源是正文还是评论。
        foreach (Match match in AtInTextRegex.Matches(merged))
        {
            var name = TextNormalizer.CleanMention(match.Groups["name"].Value);
            if (name.Length == 0) continue;
            if (found.Any(x => TextNormalizer.SameName(x.Name, name))) continue;
            found.Add((name, null, match.Index));
            if (match.Index < facts.BodyText.Length) fromBody = true;
            else fromComments = true;
        }

        facts.Mentions = found
            .OrderBy(x => x.Index)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select((x, ordinal) => new AtMention
            {
                Name = x.Name,
                Uid = x.Uid,
                Ordinal = ordinal,
                Context = x.Index == int.MaxValue ? string.Empty : LineAt(merged, x.Index, 120),
            })
            .ToList();

        if (facts.Mentions.Count > 0)
        {
            facts.FieldSources["Mentions"] = fromBody && fromComments
                ? $"{bodySource}+评论"
                : fromBody ? bodySource : "评论";
        }
    }

    // -----------------------------------------------------------------------
    // 来源回填
    // -----------------------------------------------------------------------

    /// <summary>
    /// 把 NeighborExtractor 抽出来的字段登记来源（正文/简介/评论）。
    /// 为什么要在抽完之后做：抽的时候拿到的是归一化文本，只有回到原始正文/评论里
    /// 按原文片段再找一次，来源才靠谱。
    /// </summary>
    private static void FillDerivedSources(PostFacts facts, string bodySource)
    {
        var body = TextNormalizer.Normalize(facts.BodyText);
        var comments = TextNormalizer.Normalize(facts.CommentsText);

        void Mark(string field, string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            facts.FieldSources[field] = Locate(body, comments, raw, bodySource);
        }

        Mark("BarRaw", facts.BarRaw);
        Mark("BarNumber", facts.BarRaw);
        Mark("IsEx", facts.IsEx ? "EX" : null);
        Mark("DeclaredTime", facts.DeclaredTime);
        Mark("LastMark", facts.LastMarkRaw);
        Mark("FirstMark", facts.FirstMarkRaw);
        Mark("Prevs", facts.Prevs.Count > 0 ? facts.Prevs[0].RawText : null);
        Mark("Nexts", facts.Nexts.Count > 0 ? facts.Nexts[0].RawText : null);
        Mark("CreatorInText", facts.CreatorInText);
        Mark("ProxyName", facts.ProxyName);
        Mark("IsProxy", facts.IsProxy ? ProxyPattern.Match(body + "\n" + comments).Value : null);
    }

    private static string Locate(string body, string comments, string raw, string bodySource)
    {
        if (body.Contains(raw, StringComparison.Ordinal)) return bodySource;
        if (comments.Contains(raw, StringComparison.Ordinal)) return "评论";

        // 时段在原文里可能写全角冒号、带空格，归一化片段不一定逐字相等：按 HH/MM 再找一次。
        var time = TimePattern.Match(raw);
        if (time.Success)
        {
            var loose = $@"(?<![0-9]){time.Groups["h"].Value}\s*[:：]\s*{time.Groups["m"].Value}(?![0-9])";
            if (Regex.IsMatch(body, loose)) return bodySource;
            if (Regex.IsMatch(comments, loose)) return "评论";
        }
        return bodySource;
    }

    // -----------------------------------------------------------------------
    // 小工具
    // -----------------------------------------------------------------------

    private static PostFacts NewFacts(PostRef reference)
    {
        var facts = new PostFacts
        {
            Kind = reference.Kind,
            PostId = reference.Id,
            Url = reference.Url,
        };
        facts.FieldSources["PostId"] = "入口";
        facts.FieldSources["Kind"] = "入口";
        facts.FieldSources["Url"] = "入口";
        return facts;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? StrProp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }

    private static long LongProp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var number)) return number;
        if (el.ValueKind == JsonValueKind.String
            && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static int IntProp(JsonElement obj, string name)
    {
        var value = LongProp(obj, name);
        return value is > int.MaxValue or < int.MinValue ? 0 : (int)value;
    }

    /// <summary>取 <paramref name="index"/> 所在的那一行（用于 Context / RawText），过长截断。</summary>
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
}
