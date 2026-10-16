#nullable enable
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>一个 opus 帖子（单链接模式的一根"候选棒"）。</summary>
public sealed class OpusEntry
{
    public string OpusId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public long PubTs { get; set; }
    public string BodyText { get; set; } = string.Empty;
    public string CommentsText { get; set; } = string.Empty;
    public string MergedText { get; set; } = string.Empty;
    public int CommentCount { get; set; }
    /// <summary>从正文/评论里发现的 opus 链接（用于继续遍历）。</summary>
    public List<string> LinkedOpusIds { get; set; } = new();
    /// <summary>@ 提及的 (昵称, UID)（用于在没链接时定位下一棒）。</summary>
    public List<(string Name, string Mid)> Mentions { get; set; } = new();
    /// <summary>被访问的深度（起点为 0）。</summary>
    public int Depth { get; set; }
    public string OpusUrl => $"https://www.bilibili.com/opus/{OpusId}";
}

/// <summary>单链接采集结果。</summary>
public sealed class SingleLinkResult
{
    public List<OpusEntry> Entries { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// 单链接模式采集器（接力**没有建 topic** 的情形，见 ai_guild.md 的"单个接力链接"）。
///
/// 为什么不能只靠 JSON 接口：实测 `x/polymer/web-dynamic/v1/detail` 对 DRAW 型帖子
/// 的 `desc.text` 为 **null**、`rich_text_nodes` 为空、`major.draw` 只有图片 URL ——
/// **正文完全拿不到**。而 opus 页面的 `__INITIAL_STATE__.detail.modules[].module_content.paragraphs`
/// 有完整正文（实测 116 段、含 @ 与链接节点）。所以两个来源都要用：
///   - HTML/__INITIAL_STATE__ → 正文（paragraphs）与评论 oid
///   - JSON dynamic/detail     → 结构化元数据（id_str/type/author），作为交叉校验
///
/// 链路遍历：正文与评论里若出现 `/opus/&lt;id&gt;` 或 `bilibili.com/opus/&lt;id&gt;` 形式的链接，
/// 直接跟进；若只写了 `下一棒 @某人` 而没有链接，则记录 @ 的 UID 供人工确认
/// （实测多数接力帖只 @ 人、不给链接，无法自动跟进 —— 这是数据现实，不是实现缺陷）。
/// </summary>
public sealed class SingleLinkCollector
{
    private static readonly JsonSerializerOptions JsonlOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions PrettyOut = new(JsonlOut) { WriteIndented = true };

    private readonly BilibiliClient _client;

    /// <summary>链路遍历的最大跳数（防止环形链导致无限抓取）。</summary>
    public int MaxHops { get; init; } = 40;

    /// <summary>两次请求之间的额外间隔（评论接口也走它）。</summary>
    public int DelayMs { get; init; } = 700;

    public SingleLinkCollector(BilibiliClient client) => _client = client;

    /// <summary>从任意一个 opus 链接/ID 出发，抓取并遍历链路。</summary>
    public SingleLinkResult Collect(
        string startOpusId,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new SingleLinkResult();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((startOpusId, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, depth) = queue.Dequeue();
            if (id.Length < 6 || !visited.Add(id)) continue;
            if (visited.Count > MaxHops)
            {
                result.Warnings.Add($"已达到最大跳数 {MaxHops}，停止遍历（未访问的链接：{queue.Count} 个）");
                break;
            }

            OpusEntry? entry;
            try
            {
                entry = FetchEntry(id, depth, log, ct);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"抓取 opus/{id} 失败：{ex.Message}");
                continue;
            }
            if (entry is null)
            {
                result.Warnings.Add($"opus/{id} 解析失败（页面结构可能已变）");
                continue;
            }

            result.Entries.Add(entry);
            log?.Invoke($"  已抓 opus/{id}（作者 {entry.AuthorName}，正文 {entry.BodyText.Length} 字，评论 {entry.CommentCount} 条，链接 {entry.LinkedOpusIds.Count} 个）");

            foreach (var next in entry.LinkedOpusIds)
                if (!visited.Contains(next))
                    queue.Enqueue((next, depth + 1));
        }

        if (result.Entries.Count > 0 && result.Entries.All(e => e.LinkedOpusIds.Count == 0))
            result.Warnings.Add("所有帖子都没有指向其他 opus 的链接 —— 接力帖只写了「上一棒 @某人」而没有给链接。"
                              + "请在 TUI 里按 D 打开原帖核对，或把相邻棒次的链接一并提供。");

        return result;
    }

    // ---------------------------------------------------------------- 单帖抓取

    public OpusEntry? FetchEntry(string opusId, int depth, Action<string>? log, CancellationToken ct)
    {
        var html = _client.GetOpusHtmlAsync(opusId, ct).GetAwaiter().GetResult();
        var oc = OpusContentExtractor.FromHtml(html);
        if (oc is null) return null;

        // 评论
        if (!string.IsNullOrEmpty(oc.CommentOid))
        {
            try
            {
                var comments = _client.GetCommentsAsync(oc.CommentOid!, oc.CommentType, upMid: oc.AuthorMid, ct)
                                      .GetAwaiter().GetResult();
                oc.Comments.AddRange(comments);
            }
            catch (Exception ex)
            {
                log?.Invoke($"    （评论抓取失败：{ex.Message}）");
            }
        }

        var entry = new OpusEntry
        {
            OpusId = oc.OpusId.Length > 0 ? oc.OpusId : opusId,
            Title = oc.Title,
            AuthorMid = oc.AuthorMid,
            AuthorName = oc.AuthorName,
            PubTs = oc.PubTs,
            BodyText = oc.BodyText,
            CommentsText = oc.CommentsText,
            MergedText = oc.MergedText,
            CommentCount = oc.Comments.Count,
            Depth = depth,
        };

        // 收集链接与 @ 提及（正文 + 评论）
        var linked = new HashSet<string>(StringComparer.Ordinal);
        var mentions = new List<(string, string)>();
        foreach (var span in oc.AllSpans())
        {
            if (span.AtMid is { Length: > 0 } mid && ulong.TryParse(mid, out _) && mid.Length >= 4)
                mentions.Add((span.AtName ?? mid, mid));

            var url = span.JumpUrl;
            if (string.IsNullOrEmpty(url)) continue;
            var m = Regex.Match(url, @"/opus/(\d{6,})");
            if (m.Success && m.Groups[1].Value != entry.OpusId)
                linked.Add(m.Groups[1].Value);
        }

        entry.LinkedOpusIds = linked.OrderBy(x => x, StringComparer.Ordinal).ToList();
        entry.Mentions = mentions.Distinct().ToList();
        return entry;
    }

    // ---------------------------------------------------------------- 落盘

    /// <summary>
    /// 写入 work/opus_&lt;起始id&gt;/ 下的文件，产出与 topic 模式同构的 review.jsonl，
    /// 使既有的 TUI 纠错与 render 流程可以直接复用。
    /// </summary>
    public async Task<string> WriteAsync(
        string workRoot, string startOpusId, SingleLinkResult result, CancellationToken ct = default)
    {
        var dir = Path.Combine(workRoot, "opus_" + startOpusId);
        Directory.CreateDirectory(dir);

        // 原始 opus 列表
        await using (var w = new StreamWriter(Path.Combine(dir, "opus.jsonl"), false, new UTF8Encoding(false)))
        {
            foreach (var e in result.Entries)
            {
                await w.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    opus_id = e.OpusId,
                    opus_url = e.OpusUrl,
                    title = e.Title,
                    author_name = e.AuthorName,
                    author_mid = e.AuthorMid,
                    pub_ts = e.PubTs,
                    depth = e.Depth,
                    body_text = e.BodyText,
                    comments_text = e.CommentsText,
                    comment_count = e.CommentCount,
                    linked_opus_ids = e.LinkedOpusIds,
                    mentions = e.Mentions.Select(x => new { name = x.Name, mid = x.Mid }),
                }, JsonlOut));
            }
        }

        // 复用既有解析器：把每个 opus 当成一张"卡片"来解析接力学
        var bars = new List<ParsedBar>();
        string? topicTag = SummaryExtractor.InferTopicTag(
            result.Entries.Select(e => new TopicCard { MergedText = e.MergedText, AuthorName = e.AuthorName }),
            null);

        foreach (var e in result.Entries)
        {
            var card = new TopicCard
            {
                Id = e.OpusId,
                Type = "DYNAMIC_TYPE_DRAW",
                AuthorMid = e.AuthorMid,
                AuthorName = e.AuthorName,
                PubTs = e.PubTs,
                PubTimeLabel = e.PubTs > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(e.PubTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : string.Empty,
                MergedText = e.MergedText,
            };
            card.TextSources["opus.body"] = e.BodyText;
            if (e.CommentsText.Length > 0) card.TextSources["opus.comments"] = e.CommentsText;

            var bar = BarParser.Parse(card, topicTag);
            BarParser.ComputeDeviation(bar);
            bar.Notes.Add($"来源：单链接模式（{e.OpusUrl}，深度 {e.Depth}）");
            if (e.Mentions.Count > 0)
                bar.Notes.Add("正文/评论中的 @ 提及：" + string.Join("、", e.Mentions.Select(m => $"@{m.Name}({m.Mid})")));
            bars.Add(bar);
        }

        // 与 topic 模式一致的两遍处理
        var validation = ArtifactValidator.ValidateFromBars(bars, null, topicTag);

        var reviewPath = Path.Combine(dir, "review.jsonl");
        await using (var w = new StreamWriter(reviewPath, false, new UTF8Encoding(false)))
        {
            foreach (var b in validation.Bars)
            {
                await w.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    card_id = b.CardId,
                    bar = b.BarNumber,
                    bar_raw = b.BarRaw,
                    is_ex = b.IsEx,
                    declared_time = b.DeclaredTime,
                    pub_ts = b.PubTs,
                    pub_time = b.PubTs > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(b.PubTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : string.Empty,
                    deviation_minutes = b.TimeDeviationMinutes,
                    poster_name = b.PosterName,
                    poster_mid = b.AuthorMid,
                    creator_in_text = b.CreatorInText,
                    resolved_author = b.ResolvedAuthor,
                    resolved_author_link = b.ResolvedAuthorLink,
                    prev_name = b.PrevName,
                    next_name = b.NextName,
                    looks_like_proxy = b.IsProxy,
                    proxy_name = b.ProxyName,
                    opus_url = b.OpusUrl,
                    type = string.Empty,
                    type_label = b.WorkKindLabel ?? string.Empty,
                    unattributable = b.IsUnattributable,
                    is_summary = b.IsSummary,
                    matched_style = b.MatchedStyle,
                    merged_text = b.MergedText,
                    conflicts = b.Conflicts,
                    notes = b.Notes,
                }, JsonlOut));
            }
        }

        // 清单（供 --render 读取标题/链接；不含 topic_id，用 opus 起始 id 代替）
        var manifest = new
        {
            mode = "single-link",
            start_opus_id = startOpusId,
            start_opus_url = $"https://www.bilibili.com/opus/{startOpusId}",
            fetched = result.Entries.Count,
            attributed = validation.Attributed,
            unattributable = validation.Unattributable,
            collected_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            warnings = result.Warnings,
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "subject.json"),
            JsonSerializer.Serialize(manifest, PrettyOut), new UTF8Encoding(false), ct);

        return dir;
    }
}
