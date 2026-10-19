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

        // 上一次被中途打断（Ctrl+C / 管道提前关闭）时可能还有同类进程占着这个文件；
        // 直接 new StreamWriter 会抛 IOException，栈里看不出是「谁」占的，这里换成可读的提示。
        StreamWriter posts;
        try
        {
            posts = new StreamWriter(postsPath, false, new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"写不了 {postsPath}：文件被另一个进程占用。请检查是否还有另一个 jieli 在跑，关掉后重试。", ex);
        }

        using var w = posts;
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

    /// <summary>
    /// 人工补录一帖到话题 work 目录（`--add-link URL --into &lt;topic_id&gt;`）：
    /// 读详情 → 追加进 cards.jsonl（若这张卡还不存在）与 posts.jsonl（正文/评论），
    /// 之后 `--parse` 就把它当成本来就在话题里的一棒。
    ///
    /// 这样补录与「话题采集」走同一条解析/校验路径，产出的 CSV/md 与 TUI 清单完全同构。
    /// </summary>
    public static PostFacts AppendManual(
        string workRoot,
        long topicId,
        PostRef reference,
        Action<string>? log,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(workRoot, topicId.ToString());
        var cardsPath = Path.Combine(dir, "cards.jsonl");
        var postsPath = Path.Combine(dir, "posts.jsonl");
        if (!File.Exists(cardsPath))
            throw new FileNotFoundException($"找不到已采集的数据：{cardsPath}。请先执行 --collect {topicId}。", cardsPath);

        using var http = new ChainHttp();
        var reader = new ChainPostReader(http);
        var facts = reader.Read(reference, m => log?.Invoke("  " + m), ct);
        var cardId = string.IsNullOrWhiteSpace(facts.PostId) ? reference.Id : facts.PostId;

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(cardsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                    known.Add(id);
            }
            catch (JsonException) { /* 坏行跳过：不该因为一行坏数据挡住补录 */ }
        }

        if (known.Contains(cardId))
        {
            log?.Invoke($"  · {cardId} 已经在 cards.jsonl 里，只更新它的详情");
        }
        else
        {
            var typeText = facts.Kind switch
            {
                PostKind.Video => "video",
                PostKind.Topic => "topic",
                _ => "opus",
            };
            var cardLine = JsonSerializer.Serialize(new
            {
                id = cardId,
                type = typeText,
                author_mid = facts.AuthorUid,
                author_name = facts.AuthorName,
                pub_ts = facts.PubTs,
                pub_time = facts.PubLocal == DateTime.MinValue ? null : facts.PubLocal.ToString("yyyy-MM-dd HH:mm"),
                text_sources = new Dictionary<string, string>(),
                merged_text = string.Empty,
                manual = true,   // 人工补录标记（复核时一眼能看出这一条不是话题里的卡）
            }, JsonlOut);
            File.AppendAllText(cardsPath, cardLine + Environment.NewLine, new UTF8Encoding(false));
            log?.Invoke($"  + 已把 {cardId}（{facts.AuthorName}）补进 cards.jsonl");
        }

        var body = facts.BodyText?.Trim() ?? string.Empty;
        var comments = facts.CommentsText?.Trim() ?? string.Empty;
        var postLine = JsonSerializer.Serialize(new
        {
            card_id = cardId,
            kind = facts.Kind.ToString(),
            url = string.IsNullOrWhiteSpace(facts.Url) ? reference.Url : facts.Url,
            author_mid = facts.AuthorUid,
            author_name = facts.AuthorName,
            pub_ts = facts.PubTs,
            title = facts.Title,
            role = facts.WorkKindLabel,
            comment_count = facts.CommentCount,
            bar_raw = facts.BarRaw,
            bar_number = facts.BarNumber,
            declared_time = facts.DeclaredTime,
            creator_in_text = facts.CreatorInText,
            is_proxy = facts.IsProxy,
            proxy_name = facts.ProxyName,
            prevs = facts.Prevs.Select(p => new { name = p.Name, uid = p.Uid, bar = p.BarRaw, time = p.DeclaredTime }),
            nexts = facts.Nexts.Select(p => new { name = p.Name, uid = p.Uid, bar = p.BarRaw, time = p.DeclaredTime }),
            mentions = facts.Mentions.Select(m => new { name = m.Name, uid = m.Uid }),
            last_mark_raw = facts.LastMarkRaw,
            first_mark_raw = facts.FirstMarkRaw,
            body_text = body,
            comments_text = comments,
            body_chars = body.Length,
            comment_chars = comments.Length,
            error = (string?)null,
            warnings = facts.ReaderWarnings,
        }, JsonlOut);
        File.AppendAllText(postsPath, postLine + Environment.NewLine, new UTF8Encoding(false));
        log?.Invoke($"  + 已补进 posts.jsonl（正文 {body.Length} 字，评论 {comments.Length} 字）");
        return facts;
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
