#nullable enable
using System.Text;
using System.Text.Json;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>采集过程中的严重错误（数据源异常），调用方应中止并报错，而不是产出残缺产物。</summary>
public sealed class CollectException : Exception
{
    public CollectException(string message) : base(message) { }
}

/// <summary>话题卡片的一页结果。</summary>
public sealed class TopicPage
{
    public int PageNumber { get; init; }
    public string? OffsetIn { get; init; }
    public string? OffsetOut { get; init; }
    public bool HasMore { get; init; }
    public List<CardItem> Items { get; init; } = new();
}

/// <summary>全量抓取结果：卡片 + 实际翻页数。</summary>
public sealed class CollectResult
{
    public List<CardItem> Cards { get; init; } = new();
    public int Pages { get; init; }
}

public sealed class TopicSubject
{
    public long TopicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TopicUrl { get; set; } = string.Empty;
    /// <summary>话题声称的动态总数 —— "卡片数 == Dynamics" 是硬断言。</summary>
    public int Dynamics { get; set; }
    public int Discuss { get; set; }
    public long View { get; set; }
}

/// <summary>
/// B站话题接口客户端（免登录，实测可用；不依赖任何 Cookie）。
/// 设计要点：
///   1) sort_by=3 是"最新"（时间序），默认的 2=热门会导致**按时间排列的语义失效**，因此强制传 3。
///   2) page_size 被服务端钳制为 20，传 50/100 无效，所以不做批量尝试。
///   3) 翻页用返回的 offset 游标；若 has_more=true 但游标未推进或返回空页 → 抛 CollectException（硬失败，
///      否则会静默产出残缺归档）。
/// </summary>
public sealed class BilibiliClient : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private bool _hasRequested;

    /// <summary>两次请求之间的最小间隔，避免触发风控。</summary>
    public int DelayMs { get; init; } = 800;

    public BilibiliClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
    }

    // ---------------------------------------------------------------- 身份

    /// <summary>最近一次"话题详情"接口的原始响应（供采集层落盘，便于离线重放）。</summary>
    public string? LastTopRawJson { get; private set; }

    public async Task<TopicSubject> GetSubjectAsync(long topicId, CancellationToken ct = default)
    {
        var url = $"https://api.bilibili.com/x/topic/web/details/top?topic_id={topicId}";
        var json = await GetJsonAsync(url, topicId, ct);
        LastTopRawJson = json;
        var resp = JsonSerializer.Deserialize<TopicTopResponse>(json);
        if (resp is null) throw new CollectException("话题详情接口返回无法解析的内容。");
        if (resp.Code != 0) throw new CollectException($"话题详情接口返回 code={resp.Code} message={resp.Message}");
        var item = resp.Data?.TopDetails?.TopicItem
                   ?? throw new CollectException("话题详情接口未返回 top_details.topic_item（话题可能不存在或已被删除）。");

        return new TopicSubject
        {
            TopicId = item.Id,
            Name = item.Name ?? string.Empty,
            Description = item.Description ?? string.Empty,
            TopicUrl = $"https://www.bilibili.com/v/topic/detail?topic_id={item.Id}",
            Dynamics = item.Dynamics,
            Discuss = item.Discuss,
            View = item.View,
        };
    }

    // ---------------------------------------------------------------- 分页

    /// <summary>抓取一页（page_size 固定 20，实际以服务端为准）。</summary>
    public async Task<TopicPage> GetPageAsync(long topicId, int pageNumber, string? offset, CancellationToken ct = default)
    {
        var url = $"https://api.bilibili.com/x/topic/web/details/cards?topic_id={topicId}&sort_by=3&page_size=20";
        if (!string.IsNullOrEmpty(offset)) url += $"&offset={Uri.EscapeDataString(offset)}";

        var json = await GetJsonAsync(url, topicId, ct);
        var resp = JsonSerializer.Deserialize<TopicCardsResponse>(json)
                   ?? throw new CollectException("话题卡片接口返回无法解析的内容。");
        if (resp.Code != 0) throw new CollectException($"话题卡片接口返回 code={resp.Code} message={resp.Message}");

        var list = resp.Data?.TopicCardList ?? new TopicCardList();
        return new TopicPage
        {
            PageNumber = pageNumber,
            OffsetIn = offset,
            OffsetOut = list.Offset,
            HasMore = list.HasMore,
            Items = ParseItems(json),
        };
    }

    /// <summary>
    /// 按时间序抓取全部卡片。
    /// 硬失败条件（任一触发即抛异常，绝不返回"看起来完整"的部分数据）：
    ///   - has_more=true 但 offset 为空/未推进
    ///   - has_more=true 但本页 0 条
    ///   - 翻页次数超过安全上限
    /// </summary>
    public async Task<CollectResult> CollectAllAsync(
        long topicId,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var all = new List<CardItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? offset = null;
        const int maxPages = 60;
        int page = 0;

        for (page = 1; page <= maxPages; page++)
        {
            var p = await GetPageAsync(topicId, page, offset, ct);
            foreach (var it in p.Items)
            {
                var key = it.IdStr ?? Guid.NewGuid().ToString();
                if (seen.Add(key)) all.Add(it);
            }
            log?.Invoke($"第 {page} 页：本页 {p.Items.Count} 条，累计去重后 {all.Count} 条，has_more={p.HasMore.ToString().ToLowerInvariant()}");

            if (!p.HasMore) return new CollectResult { Cards = all, Pages = page };

            if (p.Items.Count == 0)
                throw new CollectException($"第 {page} 页返回 0 条但 has_more=true —— 数据源异常，已中止（避免产出残缺归档）。");

            if (string.IsNullOrEmpty(p.OffsetOut) || p.OffsetOut == offset)
                throw new CollectException($"第 {page} 页 has_more=true 但游标未推进（offset_in={offset ?? "null"}, offset_out={p.OffsetOut ?? "null"}）—— 数据源异常，已中止。");

            offset = p.OffsetOut;
        }

        throw new CollectException($"翻页超过 {maxPages} 页安全上限 —— 数据源异常，已中止。");
    }

    // ---------------------------------------------------------------- 内部

    private async Task<string> GetJsonAsync(string url, long topicId, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri($"https://www.bilibili.com/v/topic/detail?topic_id={topicId}");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        _lastRequestUtc = DateTime.UtcNow;

        if (!resp.IsSuccessStatusCode)
        {
            var head = body.Length > 160 ? body[..160] : body;
            throw new CollectException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} —— {url}\n{head}");
        }
        if (body.TrimStart().StartsWith('<'))
            throw new CollectException($"接口返回了 HTML 而非 JSON（端点可能已变更或被风控）—— {url}");

        return body;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (!_hasRequested) { _hasRequested = true; return; }
        var elapsed = (DateTime.UtcNow - _lastRequestUtc).TotalMilliseconds;
        var wait = DelayMs - (int)elapsed;
        if (wait > 0) await Task.Delay(wait, ct);
    }

    // ---------------------------------------------------------------- 单链接模式

    /// <summary>
    /// 抓取 opus 页面 HTML。
    /// 为什么必须抓 HTML 而不是只用 JSON 接口：实测 `x/polymer/web-dynamic/v1/detail`
    /// 对 DRAW 型帖子的 `desc.text` 为 null、`rich_text_nodes` 为空、`major.draw` 只有图片 URL，
    /// **正文完全拿不到**；而页面里的 `__INITIAL_STATE__.detail.modules[].module_content.paragraphs`
    /// 有完整正文（含 @ 与链接节点，实测 116 段）。
    /// </summary>
    public async Task<string> GetOpusHtmlAsync(string opusId, CancellationToken ct = default)
    {
        var url = $"https://www.bilibili.com/opus/{opusId}";
        await ThrottleAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        _lastRequestUtc = DateTime.UtcNow;

        if (!resp.IsSuccessStatusCode)
            throw new CollectException($"抓取 opus/{opusId} 失败：HTTP {(int)resp.StatusCode}");
        if (!body.Contains("__INITIAL_STATE__", StringComparison.Ordinal))
            throw new CollectException(
                $"opus/{opusId} 页面里没有 __INITIAL_STATE__（帖子可能已删除，或页面要求登录/需要 JS 渲染）");
        return body;
    }

    /// <summary>
    /// 抓取评论（含楼中楼）。实测 `x/v2/reply?oid=&lt;comment_id_str&gt;&amp;type=12` 免登录可用（code=0）；
    /// 而 `x/v2/reply/wbi/main` 返回 -403 访问权限不足，**不要用**。
    /// </summary>
    public async Task<List<CommentItem>> GetCommentsAsync(
        string oid, int type, long upMid, CancellationToken ct = default, int maxPages = 5)
    {
        var all = new List<CommentItem>();
        for (int pn = 1; pn <= maxPages; pn++)
        {
            var url = $"https://api.bilibili.com/x/v2/reply?oid={Uri.EscapeDataString(oid)}&type={type}"
                      + $"&sort=2&ps=20&pn={pn}";
            var json = await GetJsonAsync(url, 0, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 评论接口失败不该中断整个采集（有些帖子关闭了评论）
            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 0) break;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) break;

            var page = OpusContentExtractor.ParseComments(data, upMid);
            all.AddRange(page);

            int count = 0, size = 20, num = pn;
            if (data.TryGetProperty("page", out var pageEl))
            {
                if (pageEl.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number) count = c.GetInt32();
                if (pageEl.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number) size = Math.Max(1, s.GetInt32());
                if (pageEl.TryGetProperty("num", out var n) && n.ValueKind == JsonValueKind.Number) num = n.GetInt32();
            }
            if (page.Count == 0 || num * size >= count) break;
        }
        return all;
    }

    /// <summary>结构化元数据（交叉校验用）。实测 code=0 可用，但正文拿不到，只能补 id/type/author。</summary>
    public async Task<(string? IdStr, string? Type, long Mid, string? Name, long PubTs)?> GetDynamicDetailAsync(
        string opusId, CancellationToken ct = default)
    {
        var url = $"https://api.bilibili.com/x/polymer/web-dynamic/v1/detail?id={Uri.EscapeDataString(opusId)}";
        var json = await GetJsonAsync(url, 0, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var c) || c.GetInt32() != 0) return null;
        if (!root.TryGetProperty("data", out var d) || !d.TryGetProperty("item", out var item)) return null;

        string? idStr = item.TryGetProperty("id_str", out var i) ? i.GetString() : null;
        string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
        long mid = 0; string? name = null; long pubTs = 0;
        if (item.TryGetProperty("modules", out var mods) && mods.TryGetProperty("module_author", out var au))
        {
            if (au.TryGetProperty("mid", out var m) && m.ValueKind == JsonValueKind.Number) mid = m.GetInt64();
            if (au.TryGetProperty("name", out var n)) name = n.GetString();
            if (au.TryGetProperty("pub_ts", out var p) && p.ValueKind == JsonValueKind.Number) pubTs = p.GetInt64();
        }
        return (idStr, type, mid, name, pubTs);
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>把 items[] 里运行时键名（dynamic_card_item / video_card_item / …）归一化成 CardItem。</summary>
    private static List<CardItem> ParseItems(string json)
    {
        var result = new List<CardItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return result;
        if (!data.TryGetProperty("topic_card_list", out var tcl)) return result;
        if (!tcl.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return result;

        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in it.EnumerateObject())
            {
                if (!prop.Name.EndsWith("_card_item", StringComparison.Ordinal)) continue;
                var card = prop.Value.Deserialize<CardItem>();
                if (card is null) continue;
                result.Add(card);
                break;
            }
        }
        return result;
    }

    public void Dispose() => _http.Dispose();
}
