#nullable enable
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using JieLi.Tui.Chain;

namespace JieLi.Tui.Data;

/// <summary>一次「逐帖补全」的结果统计。</summary>
public sealed class TopicDetailReport
{
    public int Total { get; set; }
    public int Fetched { get; set; }
    public int Failed { get; set; }
    public int TotalChars { get; set; }
    public List<string> Failures { get; } = new();
    public bool IsComplete => Failed == 0;
}

/// <summary>
/// 话题卡片的「详情补全」：话题接口只给卡面（desc.text / major.desc / 标题），
/// 拿不到正文段落、评论区、图片卡片里的文字。归档要的是每一棒的完整信息，
/// 所以必须按卡片指向的目标（opus / 专栏 cv / 视频 BV）逐个取详情页。
///
/// 产出 work/&lt;topicId&gt;/posts.jsonl（逐帖详情，可离线重放）；
/// <see cref="TopicCollector.LoadCards"/> 读它并把正文/评论并入 MergedText。
/// </summary>
public static class TopicDetailEnricher
{
    private static readonly JsonSerializerOptions JsonlOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>拉取全部卡片指向的详情页。单帖失败不致命（记进 posts.jsonl 的 error 字段）。</summary>
    public static TopicDetailReport Enrich(
        string workRoot,
        long topicId,
        Action<string>? log,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(workRoot, topicId.ToString());
        var rawPath = Path.Combine(dir, "cards.raw.jsonl");
        var cardsPath = Path.Combine(dir, "cards.jsonl");
        var postsPath = Path.Combine(dir, "posts.jsonl");

        if (!File.Exists(cardsPath))
            throw new FileNotFoundException($"找不到已采集的 cards.jsonl：{cardsPath}。请先执行 --collect。", cardsPath);

        var refs = File.Exists(rawPath) ? ReadRefs(rawPath) : new Dictionary<string, PostRef>(StringComparer.Ordinal);
        var cards = TopicCollector.LoadCardsWithoutDetails(workRoot, topicId);
        var report = new TopicDetailReport { Total = cards.Count };

        using var http = new ChainHttp();
        var reader = new ChainPostReader(http);

        using var w = new StreamWriter(postsPath, false, new UTF8Encoding(false));
        var index = 0;
        foreach (var card in cards.OrderBy(c => c.PubTs))
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var reference = refs.TryGetValue(card.Id, out var r)
                ? r
                : PostRef.Opus(card.Id);

            log?.Invoke($"[{index}/{cards.Count}] opus/{card.Id}（{card.AuthorName}）→ {reference.Url}");
            string? error = null;
            PostFacts? facts = null;
            try
            {
                facts = reader.Read(reference, m => log?.Invoke("  " + m), ct);
                report.Fetched++;
                report.TotalChars += (facts.BodyText?.Length ?? 0) + (facts.CommentsText?.Length ?? 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                report.Failed++;
                report.Failures.Add($"{card.Id}（{card.AuthorName}）：{error}");
                log?.Invoke($"  ✗ 读取失败：{error}");
            }

            var body = facts?.BodyText?.Trim() ?? string.Empty;
            var comments = facts?.CommentsText?.Trim() ?? string.Empty;
            if (facts is null && !string.IsNullOrWhiteSpace(card.MergedText))
                log?.Invoke("  · 保留卡面文字作为兜底（卡面只有首行摘要）");

            w.WriteLine(JsonSerializer.Serialize(new
            {
                card_id = card.Id,
                kind = (facts?.Kind ?? reference.Kind).ToString(),
                url = facts?.Url ?? reference.Url,
                author_mid = facts?.AuthorUid ?? card.AuthorMid,
                author_name = facts?.AuthorName is { Length: > 0 } n ? n : card.AuthorName,
                pub_ts = facts?.PubTs is > 0 ? facts!.PubTs : card.PubTs,
                title = facts?.Title,
                role = facts?.WorkKindLabel,
                comment_count = facts?.CommentCount ?? 0,
                bar_raw = facts?.BarRaw,
                bar_number = facts?.BarNumber,
                declared_time = facts?.DeclaredTime,
                creator_in_text = facts?.CreatorInText,
                is_proxy = facts?.IsProxy == true,
                proxy_name = facts?.ProxyName,
                prevs = facts?.Prevs.Select(p => new { name = p.Name, uid = p.Uid, bar = p.BarRaw, time = p.DeclaredTime }),
                nexts = facts?.Nexts.Select(p => new { name = p.Name, uid = p.Uid, bar = p.BarRaw, time = p.DeclaredTime }),
                mentions = facts?.Mentions.Select(m => new { name = m.Name, uid = m.Uid }),
                last_mark_raw = facts?.LastMarkRaw,
                first_mark_raw = facts?.FirstMarkRaw,
                body_text = body,
                comments_text = comments,
                body_chars = body.Length,
                comment_chars = comments.Length,
                error,
                warnings = facts?.ReaderWarnings,
            }, JsonlOut));
        }

        log?.Invoke($"详情补全：{report.Fetched}/{report.Total} 成功"
                    + (report.Failed > 0 ? $"，{report.Failed} 条失败" : string.Empty)
                    + $"（正文+评论共 {report.TotalChars:N0} 字）");
        log?.Invoke($"已写入 {postsPath}");
        return report;
    }

    /// <summary>从原始卡片里读「卡片 → 目标帖子」的映射（视频卡给 bvid、专栏卡给 cv 号）。</summary>
    private static Dictionary<string, PostRef> ReadRefs(string rawPath)
    {
        var map = new Dictionary<string, PostRef>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(rawPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                // 卡片键名形如 dynamic_card_item / video_card_item / article_card_item；
                // 但 cards.raw.jsonl 落盘时可能已经把 CardItem 摊平到顶层（顶层直接有 id_str），两种都认。
                JsonElement? item = null;
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name.EndsWith("_card_item", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        item = p.Value;
                        break;
                    }
                }
                if (item is null)
                {
                    if (root.TryGetProperty("id_str", out var ownId) && ownId.ValueKind == JsonValueKind.String)
                        item = root;
                    else
                        continue;
                }

                var idStr = Str(item.Value, "id_str");
                if (string.IsNullOrEmpty(idStr)) continue;

                // 视频卡：major.archive.bvid；专栏卡：major.article.id
                if (item.Value.TryGetProperty("modules", out var modules)
                    && modules.ValueKind == JsonValueKind.Object
                    && modules.TryGetProperty("module_dynamic", out var dyn)
                    && dyn.ValueKind == JsonValueKind.Object
                    && dyn.TryGetProperty("major", out var major)
                    && major.ValueKind == JsonValueKind.Object)
                {
                    var majorType = Str(major, "type") ?? string.Empty;
                    if (major.TryGetProperty("archive", out var archive) && archive.ValueKind == JsonValueKind.Object)
                    {
                        var bvid = Str(archive, "bvid");
                        if (!string.IsNullOrEmpty(bvid))
                        {
                            map[idStr] = PostRef.Video(bvid);
                            continue;
                        }
                    }
                    if (major.TryGetProperty("article", out var article) && article.ValueKind == JsonValueKind.Object)
                    {
                        var aid = Str(article, "id") ?? Str(article, "id_str");
                        if (!string.IsNullOrEmpty(aid))
                        {
                            map[idStr] = PostRef.Opus(aid);
                            continue;
                        }
                    }
                    _ = majorType;
                }

                map[idStr] = PostRef.Opus(idStr);
            }
            catch (JsonException)
            {
                // 单行坏数据不影响其余卡片
            }
        }
        return map;
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }
}
