#nullable enable
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>一次采集的元信息，写入 work/&lt;topicId&gt;/subject.json。</summary>
public sealed class CollectManifest
{
    public long TopicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TopicUrl { get; set; } = string.Empty;
    public int DynamicsReported { get; set; }
    public int DiscussReported { get; set; }
    public int FetchedCards { get; set; }
    public int Pages { get; set; }
    public int EntriesWithText { get; set; }
    public int EntriesWithoutText { get; set; }
    public string CollectedAt { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// M2 采集层：抓取话题全部卡片，原始响应与解析结果落盘到 work/&lt;topicId&gt;/。
/// 落盘遵循"先原始、后派生"：cards.raw.jsonl 保存接口原文（可离线重放），cards.jsonl 保存归一化结果。
/// </summary>
public sealed class TopicCollector
{
    private static readonly JsonSerializerOptions JsonlOut = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义，便于人工查看
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions PrettyOut = new(JsonlOut) { WriteIndented = true };

    private readonly BilibiliClient _client;

    public TopicCollector(BilibiliClient client) => _client = client;

    public async Task<CollectManifest> CollectAsync(
        long topicId,
        string workRoot,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var subject = await _client.GetSubjectAsync(topicId, ct);
        log?.Invoke($"话题：{subject.Name}  (id={subject.TopicId})");
        log?.Invoke($"接口声称 dynamics={subject.Dynamics}  discuss={subject.Discuss}  view={subject.View}");

        var collected = await _client.CollectAllAsync(topicId, m => log?.Invoke("  " + m), ct);
        var cards = collected.Cards;

        var dir = Path.Combine(workRoot, subject.TopicId.ToString());
        Directory.CreateDirectory(dir);

        var topPath = Path.Combine(dir, "top.json");
        var rawPath = Path.Combine(dir, "cards.raw.jsonl");
        var normPath = Path.Combine(dir, "cards.jsonl");
        var manifestPath = Path.Combine(dir, "subject.json");

        // 话题身份原文
        if (_client.LastTopRawJson is { Length: > 0 } topRaw)
            await File.WriteAllTextAsync(topPath, topRaw, new UTF8Encoding(false), ct);

        // 原始卡片（未改动）
        await using (var w = new StreamWriter(rawPath, false, new UTF8Encoding(false)))
        {
            foreach (var c in cards)
                await w.WriteLineAsync(JsonSerializer.Serialize(c, JsonlOut));
        }

        // 归一化卡片
        var normalized = cards.Select(CardTextExtractor.Extract).ToList();
        await using (var w = new StreamWriter(normPath, false, new UTF8Encoding(false)))
        {
            foreach (var tc in normalized)
            {
                await w.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = tc.Id,
                    type = tc.Type,
                    author_mid = tc.AuthorMid,
                    author_name = tc.AuthorName,
                    pub_ts = tc.PubTs,
                    pub_time = tc.PubTimeLabel,
                    opus_url = tc.OpusUrl,
                    space_url = tc.SpaceUrl,
                    text_sources = tc.TextSources,
                    merged_text = tc.MergedText,
                }, JsonlOut));
            }
        }

        var manifest = new CollectManifest
        {
            TopicId = subject.TopicId,
            Name = subject.Name,
            Description = subject.Description,
            TopicUrl = subject.TopicUrl,
            DynamicsReported = subject.Dynamics,
            DiscussReported = subject.Discuss,
            FetchedCards = cards.Count,
            Pages = collected.Pages,
            EntriesWithText = normalized.Count(c => c.TextSources.Count > 0),
            EntriesWithoutText = normalized.Count(c => c.TextSources.Count == 0),
            CollectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // ---- 断言 1：卡片数 == 接口声称的 dynamics ----
        if (cards.Count != subject.Dynamics)
            manifest.Warnings.Add($"断言1未通过：抓到 {cards.Count} 条，接口声称 {subject.Dynamics} 条（差 {cards.Count - subject.Dynamics}）");
        else
            log?.Invoke($"断言1通过：抓到 {cards.Count} 条 == 接口声称 {subject.Dynamics} 条");

        if (manifest.EntriesWithoutText > 0)
            manifest.Warnings.Add($"有 {manifest.EntriesWithoutText} 条卡片完全取不到正文（需人工保存页面兜底）");
        log?.Invoke($"正文覆盖：{manifest.EntriesWithText}/{cards.Count} 条卡片取到文本");

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, PrettyOut), new UTF8Encoding(false), ct);

        log?.Invoke($"已写入 {dir}");
        foreach (var f in new[] { "top.json", "cards.raw.jsonl", "cards.jsonl", "subject.json" })
        {
            var p = Path.Combine(dir, f);
            log?.Invoke(File.Exists(p) ? $"  · {f}  ({new FileInfo(p).Length:N0} 字节)" : $"  · {f}  (未生成)");
        }

        return manifest;
    }

    // ---------------------------------------------------------------- 离线重放

    /// <summary>从 work/&lt;topicId&gt;/cards.jsonl 读回归一化卡片（离线，不联网）。</summary>
    public static List<TopicCard> LoadCards(string workRoot, long topicId)
    {
        var path = Path.Combine(workRoot, topicId.ToString(), "cards.jsonl");
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到已采集的数据：{path}。请先执行 --collect {topicId}。", path);

        var list = new List<TopicCard>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var tc = new TopicCard
            {
                Id = GetString(root, "id"),
                Type = GetString(root, "type"),
                AuthorMid = GetLong(root, "author_mid"),
                AuthorName = GetString(root, "author_name"),
                PubTs = GetLong(root, "pub_ts"),
                PubTimeLabel = GetString(root, "pub_time"),
            };
            if (root.TryGetProperty("text_sources", out var ts) && ts.ValueKind == JsonValueKind.Object)
                foreach (var p in ts.EnumerateObject())
                    tc.TextSources[p.Name] = p.Value.GetString() ?? string.Empty;
            if (root.TryGetProperty("merged_text", out var mt))
                tc.MergedText = mt.GetString() ?? string.Empty;
            list.Add(tc);
        }
        return list;
    }

    /// <summary>读取采集清单（用于拿接口声称的 dynamics 做断言）。</summary>
    public static CollectManifest? LoadManifest(string workRoot, long topicId)
    {
        var path = Path.Combine(workRoot, topicId.ToString(), "subject.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<CollectManifest>(File.ReadAllText(path));
    }

    /// <summary>写入 M4 人工确认要读的 review.jsonl（含原始正文，便于在 TUI 里核对）。</summary>
    public static async Task WriteReviewAsync(string workRoot, long topicId, ValidationResult result, CancellationToken ct = default)
    {
        var dir = Path.Combine(workRoot, topicId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "review.jsonl");

        await using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var b in result.Bars)
        {
            await w.WriteLineAsync(JsonSerializer.Serialize(new
            {
                card_id = b.CardId,
                bar = b.BarNumber,
                bar_raw = b.BarRaw,
                is_ex = b.IsEx,
                declared_time = b.DeclaredTime,
                pub_ts = b.PubTs,
                pub_time = DateTimeOffset.FromUnixTimeSeconds(b.PubTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
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
                prev_original_author = b.PrevOriginalAuthor,
                prev_proxy_name = b.PrevProxyName,
                matched_style = b.MatchedStyle,
                opus_url = b.OpusUrl,
                type = b.IsSummary ? "SUMMARY" : string.Empty,
                type_label = b.WorkKindLabel ?? string.Empty,
                unattributable = b.IsUnattributable,
                is_summary = b.IsSummary,
                merged_text = b.MergedText,
                conflicts = b.Conflicts,
                notes = b.Notes,
            }, JsonlOut));
        }
    }

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
}
