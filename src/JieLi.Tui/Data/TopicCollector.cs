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
    /// <summary>逐帖详情补全：成功 / 失败条数（posts.jsonl）。</summary>
    public int DetailsFetched { get; set; }
    public int DetailsFailed { get; set; }
    /// <summary>按届过滤：选中那一届的年份（0 = 未过滤）。</summary>
    public int EditionYear { get; set; }
    /// <summary>过滤后参与解析的卡片数。</summary>
    public int CardsInEdition { get; set; }
    /// <summary>被「按届过滤」排除的卡片数。</summary>
    public int CardsDroppedByEdition { get; set; }
    /// <summary>按届过滤的人读说明。</summary>
    public string EditionNote { get; set; } = string.Empty;
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
        CancellationToken ct = default,
        int? yearFilter = null,
        DateTime? dateFilter = null,
        bool allYears = false)
    {
        var subject = await _client.GetSubjectAsync(topicId, ct);
        log?.Invoke($"话题：{subject.Name}  (id={subject.TopicId})");
        log?.Invoke($"接口声称 dynamics={subject.Dynamics}  discuss={subject.Discuss}  view={subject.View}");

        var collected = await _client.CollectAllAsync(topicId, m => log?.Invoke("  " + m), ct);
        var cards = collected.Cards;
        // 归一化（只做文本提取，建立 id → 卡片 的对照；按届过滤用的是它的发布时间）
        var topics = cards.Select(CardTextExtractor.Extract).ToList();

        // ---- 按届过滤：同一个话题常被这场年度接力反复复用，整锅端会把不同年份混成一条链 ----
        YearSelection? selection = null;
        if (!allYears)
        {
            selection = TopicYearFilter.Select(topics, yearFilter, dateFilter);
            if (selection.Filtered)
            {
                log?.Invoke(selection.Note);
                log?.Invoke("  （要看别的年份：--year 2024 或 --date 2024-09-17；确认不要过滤：--all-years）");
            }
            else
            {
                log?.Invoke("按届过滤：全部卡片都属于同一届，未排除任何卡片");
            }
        }
        else if (cards.Count > 0)
        {
            log?.Invoke("--all-years：不做按届过滤（话题里所有年份的卡片都会参与解析）");
        }

        var keptIds = selection?.Kept.Select(t => t.Id).ToHashSet(StringComparer.Ordinal) ?? null;
        var droppedIds = selection?.Dropped.Select(t => t.Id).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var kept = keptIds is null ? cards : cards.Where(c => c.IdStr is not null && keptIds.Contains(c.IdStr)).ToList();
        var dropped = cards.Where(c => c.IdStr is not null && droppedIds.Contains(c.IdStr)).ToList();

        var dir = Path.Combine(workRoot, subject.TopicId.ToString());
        Directory.CreateDirectory(dir);

        var topPath = Path.Combine(dir, "top.json");
        var rawPath = Path.Combine(dir, "cards.raw.jsonl");
        var normPath = Path.Combine(dir, "cards.jsonl");
        var droppedPath = Path.Combine(dir, "cards.dropped.jsonl");
        var manifestPath = Path.Combine(dir, "subject.json");

        // 话题身份原文
        if (_client.LastTopRawJson is { Length: > 0 } topRaw)
            await File.WriteAllTextAsync(topPath, topRaw, new UTF8Encoding(false), ct);

        // 原始卡片（未改动）：只留选中那一届；被排除的另存 cards.dropped.jsonl 供审计
        await using (var w = new StreamWriter(rawPath, false, new UTF8Encoding(false)))
        {
            foreach (var c in kept)
                await w.WriteLineAsync(JsonSerializer.Serialize(c, JsonlOut));
        }
        if (dropped.Count > 0)
        {
            await using var w = new StreamWriter(droppedPath, false, new UTF8Encoding(false));
            foreach (var c in dropped)
                await w.WriteLineAsync(JsonSerializer.Serialize(c, JsonlOut));
        }

        // 归一化卡片
        var normalized = kept.Select(CardTextExtractor.Extract).ToList();
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
            EditionYear = selection?.Year ?? 0,
            CardsInEdition = kept.Count,
            CardsDroppedByEdition = dropped.Count,
            EditionNote = selection?.Note ?? (allYears ? "--all-years：未做按届过滤" : string.Empty),
            CollectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        // ---- 断言 1：卡片数 == 接口声称的 dynamics ----
        if (cards.Count != subject.Dynamics)
            manifest.Warnings.Add($"断言1未通过：抓到 {cards.Count} 条，接口声称 {subject.Dynamics} 条（差 {cards.Count - subject.Dynamics}）");
        else
            log?.Invoke($"断言1通过：抓到 {cards.Count} 条 == 接口声称 {subject.Dynamics} 条");

        if (manifest.EntriesWithoutText > 0)
            manifest.Warnings.Add($"有 {manifest.EntriesWithoutText} 条卡片完全取不到正文（需人工保存页面兜底）");
        if (dropped.Count > 0)
            manifest.Warnings.Add(selection?.Note ?? $"按届过滤排除了 {dropped.Count} 条卡片");
        log?.Invoke($"正文覆盖：{manifest.EntriesWithText}/{kept.Count} 条卡片取到文本");

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, PrettyOut), new UTF8Encoding(false), ct);

        log?.Invoke($"已写入 {dir}");
        foreach (var f in new[] { "top.json", "cards.raw.jsonl", "cards.jsonl", "cards.dropped.jsonl", "subject.json" })
        {
            var p = Path.Combine(dir, f);
            log?.Invoke(File.Exists(p) ? $"  · {f}  ({new FileInfo(p).Length:N0} 字节)" : $"  · {f}  (未生成)");
        }

        return manifest;
    }

    // ---------------------------------------------------------------- 离线重放

    /// <summary>
    /// 从 work/&lt;topicId&gt;/cards.jsonl 读回归一化卡片（离线，不联网）。
    /// 如果同一目录下有 posts.jsonl（逐帖详情补全的产物），把正文段落与评论并入
    /// TextSources（detail.body / detail.comments）并重算 MergedText —— 解析层因此
    /// 能看到「卡面首行」之外的完整信息。
    /// </summary>
    public static List<TopicCard> LoadCards(string workRoot, long topicId)
    {
        var list = LoadCardsWithoutDetails(workRoot, topicId);

        var postsPath = Path.Combine(workRoot, topicId.ToString(), "posts.jsonl");
        if (File.Exists(postsPath))
        {
            var byId = new Dictionary<string, TopicCard>(StringComparer.Ordinal);
            foreach (var c in list)
                if (!string.IsNullOrEmpty(c.Id)) byId[c.Id] = c;

            foreach (var line in File.ReadLines(postsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = GetString(root, "card_id");
                if (!byId.TryGetValue(id, out var card)) continue;

                card.DetailUrl = GetString(root, "url");
                card.DetailFetched = string.IsNullOrEmpty(GetString(root, "error"));

                var body = GetString(root, "body_text");
                if (!string.IsNullOrWhiteSpace(body)) card.TextSources["detail.body"] = body;

                var comments = GetString(root, "comments_text");
                if (!string.IsNullOrWhiteSpace(comments)) card.TextSources["detail.comments"] = comments;
            }

            foreach (var card in list) card.MergedText = ComposeMerged(card);
        }

        return list;
    }

    /// <summary>把正文、卡面文字、评论按优先级拼成 MergedText，并去掉重复内容。</summary>
    private static string ComposeMerged(TopicCard card)
    {
        var parts = new List<string>();
        var seen = new List<string>();

        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var t = text.Trim();
            // 卡面文字通常只是正文首行的摘要，重复时不再追加（否则同一句「第N棒」出现两次）
            if (t.Length >= 8 && seen.Any(s => s.Contains(t, StringComparison.Ordinal))) return;
            if (seen.Any(s => s.Contains(t, StringComparison.Ordinal) && t.Length * 2 < s.Length)) return;
            parts.Add(t);
            seen.Add(t);
        }

        if (card.TextSources.TryGetValue("detail.body", out var body)) Add(body);
        foreach (var kv in card.TextSources)
            if (kv.Key is not ("detail.body" or "detail.comments")) Add(kv.Value);
        if (card.TextSources.TryGetValue("detail.comments", out var comments)) Add(comments);

        return string.Join(" ⏎ ", parts);
    }

    /// <summary>只读 cards.jsonl，不合并 posts.jsonl（采集/补全阶段用，避免自己读自己刚写的派生数据）。</summary>
    public static List<TopicCard> LoadCardsWithoutDetails(string workRoot, long topicId)
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
            // 重复投稿 / 非棒次插曲：不进入人工确认清单，避免把 CSV 撑出多余的行
            if (b.IsDuplicate) continue;
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

    /// <summary>
    /// 写出被排除的卡片（同一棒位的重复投稿 / 非棒次插曲）。
    /// 它们不进入 review.jsonl（否则会变成 CSV 里的 N/A 行），但必须可审计：
    /// 报告里要能回答「为什么这条没进归档」。
    /// </summary>
    public static async Task WriteIgnoredAsync(string workRoot, long topicId, ValidationResult result, CancellationToken ct = default)
    {
        if (result.Ignored.Count == 0) return;
        var dir = Path.Combine(workRoot, topicId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ignored.jsonl");
        await using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var i in result.Ignored)
            await w.WriteLineAsync(JsonSerializer.Serialize(new
            {
                card_id = i.CardId,
                author = i.Author,
                time = i.Time,
                reason = i.Reason,
            }, JsonlOut));
    }

    /// <summary>把「逐帖详情补全」的统计回写到 subject.json（采集清单）。</summary>
    public static void ApplyDetailReport(string workRoot, long topicId, TopicDetailReport report)
    {
        var path = Path.Combine(workRoot, topicId.ToString(), "subject.json");
        if (!File.Exists(path)) return;
        var manifest = JsonSerializer.Deserialize<CollectManifest>(File.ReadAllText(path));
        if (manifest is null) return;
        manifest.DetailsFetched = report.Fetched;
        manifest.DetailsFailed = report.Failed;
        if (report.Failed > 0)
            manifest.Warnings.Add($"详情补全 {report.Failed}/{report.Total} 条失败（见 posts.jsonl 的 error 字段）");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, PrettyOut), new UTF8Encoding(false));
    }

    private static string GetString(JsonElement e, string name)        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
}
