#nullable enable
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/UserResolver.cs / Chain/SpaceFinder.cs / Chain/UserCache.cs。
//
// 职责：搜索类端点（匿名可用，已实测）：
//   x/web-interface/wbi/search/type?search_type=bili_user&keyword=  → uname/mid
//   x/web-interface/wbi/search/type?search_type=article&keyword=   → id/author/mid/title/pub_time
//   x/web-interface/wbi/search/type?search_type=video&keyword=     → bvid/author/mid/title/pubdate
//   x/polymer/web-dynamic/v1/opus/feed/space?host_mid=&page=&offset=&type=all → 空间帖子列表（见 SpaceFinder）
// 搜索结果是 HTML 高亮片段（含 <em class="keyword"> 标签），取文本前必须清理。
//
// 为什么用 wbi/search/type 而不是旧的 x/web-interface/search/type：
//   实测（2026-10）旧路径对匿名请求返回 HTTP 412「request was banned」，
//   而 wbi/search/type 同一后端、同参数、匿名却返回 code=0 且字段一致。
//   换路径只是绕开单端点风控，参数含义不变。
// ===========================================================================

/// <summary>搜索端点封装。</summary>
public sealed class ChainSearchClient
{
    /// <summary>匿名可用、无签名要求的搜索入口（见文件头注释）。</summary>
    private const string SearchTypeApi = "https://api.bilibili.com/x/web-interface/wbi/search/type";

    /// <summary>视频搜索翻页上限（每次调用最多 5 页，硬约束）。</summary>
    private const int MaxVideoPages = 5;

    /// <summary>每页条数（b 站搜索固定 20，用来判断"是否最后一页"）。</summary>
    private const int PageSize = 20;

    /// <summary>搜索结果标题里的高亮标签（如 &lt;em class="keyword"&gt;）与其它 HTML 片段。</summary>
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>搜索接口里出现过的两种时间格式：完整日期，以及只有「月-日 时:分」。</summary>
    private static readonly string[] TimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd",
        "MM-dd HH:mm:ss", "MM-dd HH:mm", "MM-dd",
    };

    private readonly ChainHttp _http;

    /// <summary>
    /// 本轮是否已经吃过风控挑战（v_voucher）。一旦触发，后续所有搜索直接跳过：
    /// 实测挑战短时间内不会解除，继续打只会每次白等 30 秒冷却，让一次遍历从 3 分钟拖到 10 分钟以上。
    /// 搜索是「锦上添花」的手段（话题卡片才是主干），跳过它不是致命问题。
    /// </summary>
    private bool _challenged;

    /// <summary>是否已经为「风控挑战」付过一次 30 秒冷却（只付一次）。</summary>
    private bool _challengeCooledDown;

    public ChainSearchClient(ChainHttp http) => _http = http;

    /// <summary>本轮是否已触发风控挑战（报告/日志用）。</summary>
    public bool ChallengeHit => _challenged;

    /// <summary>已触发挑战时记一条说明并返回 true（调用方据此直接返回空结果）。</summary>
    private bool ShortCircuit(string what, Action<string>? log)
    {
        if (!_challenged) return false;
        log?.Invoke($"[搜索] 本轮已触发风控挑战，跳过{what}（改用话题卡片/空间动态等不受影响的路径）");
        return true;
    }

    /// <summary>用户搜索（bili_user）。返回可能为空的候选列表（Exact 由 Resolve 判）。</summary>
    public IReadOnlyList<UserRef> SearchUsers(string keyword, Action<string>? log, CancellationToken ct)
    {
        var list = new List<UserRef>();
        if (string.IsNullOrWhiteSpace(keyword)) return list;
        if (ShortCircuit("用户搜索", log)) return list;

        var url = $"{SearchTypeApi}?search_type=bili_user&keyword={Uri.EscapeDataString(keyword.Trim())}&page=1";
        string json;
        try
        {
            json = _http.GetApiJson(url, ct: ct);
        }
        catch (ChainFetchException ex)
        {
            if (ex.Message.Contains("v_voucher", StringComparison.Ordinal)) _challenged = true;
            log?.Invoke($"[搜索] bili_user「{keyword}」请求失败：{ex.Message}");
            return list;
        }

        using var doc = TryParseJson(json, $"bili_user「{keyword}」", log);
        if (doc is null || !TryData(doc, out var data)) return list;

        var seen = new HashSet<long>();
        foreach (var item in ResultItems(data))
        {
            var uid = GetLong(item, "mid");
            if (uid <= 0 || !seen.Add(uid)) continue;
            var name = CleanText(GetStr(item, "uname"));
            if (name.Length == 0) continue;
            list.Add(new UserRef { Uid = uid, Name = name, Method = FetchMethod.UserSearch, Exact = false });
        }

        log?.Invoke($"[搜索] FetchMethod.UserSearch「{keyword}」→ {list.Count} 个用户候选"
            + (list.Count > 0 ? $"（第一个 {list[0].Uid} {list[0].Name}）" : ""));
        return list;
    }

    /// <summary>专栏搜索（article）。onlyMid 非空时只保留该作者的文章。</summary>
    public IReadOnlyList<PostCandidate> SearchArticles(string keyword, long? onlyMid, Action<string>? log, CancellationToken ct)
    {
        var list = new List<PostCandidate>();
        if (string.IsNullOrWhiteSpace(keyword)) return list;
        if (ShortCircuit("文章搜索", log)) return list;

        // 文章搜索是弱手段，只取搜索排序第一页（20 条）即可；翻页交给人工/后续版本。
        var url = $"{SearchTypeApi}?search_type=article&keyword={Uri.EscapeDataString(keyword.Trim())}&page=1";
        string json;
        try
        {
            json = _http.GetApiJson(url, ct: ct);
        }
        catch (ChainFetchException ex)
        {
            if (ex.Message.Contains("v_voucher", StringComparison.Ordinal)) _challenged = true;
            log?.Invoke($"[搜索] article「{keyword}」请求失败：{ex.Message}");
            return list;
        }

        using var doc = TryParseJson(json, $"article「{keyword}」", log);
        if (doc is null || !TryData(doc, out var data)) return list;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ResultItems(data))
        {
            // 文章搜索的 id 是专栏 cv 号（不是动态 opus id）；PostRef 没有 Article 种类，
            // 这里先按 Opus 记，读取端需要按 id 兜底读 read/cv。字段名见 ChainModels 注释。
            var id = GetStr(item, "id");
            if (id.Length == 0 || !seen.Add(id)) continue;

            var mid = GetLong(item, "mid");
            if (onlyMid.HasValue && mid != onlyMid.Value) continue;

            list.Add(new PostCandidate
            {
                Ref = PostRef.Opus(id),
                Title = CleanText(GetStr(item, "title")),
                PubTs = ParseUnix(item, "pub_time", "pubdate", "ctime"),
                Method = FetchMethod.ArticleSearch,
                Why = $"文章搜索「{keyword.Trim()}」命中"
                    + (onlyMid.HasValue ? "，作者 mid 与目标一致" : "")
                    + $"（专栏 cv{id}）",
            });
        }

        log?.Invoke($"[搜索] FetchMethod.ArticleSearch「{keyword}」onlyMid={onlyMid?.ToString() ?? "-"} → {list.Count} 条");
        return list;
    }

    /// <summary>视频搜索（video，按发布时间倒序）。翻页上限 5 页。</summary>
    public IReadOnlyList<PostCandidate> SearchVideos(string keyword, long? onlyMid, Action<string>? log, CancellationToken ct)
        => SearchVideos(keyword, onlyMid, log, ct, MaxVideoPages);

    /// <summary>视频搜索（可指定翻页上限；按昵称兜底时只要前 2 页就够了，省请求）。</summary>
    public IReadOnlyList<PostCandidate> SearchVideos(string keyword, long? onlyMid, Action<string>? log, CancellationToken ct, int maxPages)
    {
        var list = new List<PostCandidate>();
        if (string.IsNullOrWhiteSpace(keyword)) return list;
        if (ShortCircuit("视频搜索", log)) return list;

        var pages = Math.Clamp(maxPages, 1, MaxVideoPages);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enc = Uri.EscapeDataString(keyword.Trim());

        for (var page = 1; page <= pages; page++)
        {
            var count = 0;
            var gotPage = false;
            // 实测：搜索接口连续请求后偶发「code=0 但 result 为空」的静默限流
            // （同一关键词单独跑能命中，长链里却回 0 条）。第 1 页空就退避重试，
            // 间隔要够长——实测 3 秒仍可能撞上，5/10 秒才稳定。
            for (var attempt = 0; attempt < 3 && !gotPage; attempt++)
            {
                // order=pubdate 很重要：默认综合排序下实测 20 页都翻不到目标视频，
                // 按发布时间倒序才能在同好活动（一次发布几十棒）里稳定命中最新的几棒。
                var url = $"{SearchTypeApi}?search_type=video&keyword={enc}&page={page}&order=pubdate";
                string json;
                try
                {
                    json = _http.GetApiJson(url, ct: ct);
                }
                catch (ChainFetchException ex)
                {
                    if (ex.Message.Contains("v_voucher", StringComparison.Ordinal)) _challenged = true;
                    log?.Invoke($"[搜索] video「{keyword}」第 {page} 页失败：{ex.Message}");
                    // 风控挑战给一次长冷却重试的机会（实测挑战会在几十秒后自行解除）。
                    // 只在「本轮第一次」挑战时冷却重试，避免反复白等把一次遍历拖成十分钟。
                    if (ex.Message.Contains("v_voucher", StringComparison.Ordinal) && attempt == 0 && !_challengeCooledDown)
                    {
                        _challengeCooledDown = true;
                        log?.Invoke("[搜索] 触发风控挑战，冷却 30 秒后重试一次");
                        Thread.Sleep(30000);
                        continue;
                    }
                    break;
                }

                using var doc = TryParseJson(json, $"video「{keyword}」第 {page} 页", log);
                if (doc is null || !TryData(doc, out var data)) break;

                foreach (var item in ResultItems(data))
                {
                    count++;
                    var bvid = GetStr(item, "bvid");
                    if (bvid.Length == 0 || !seen.Add(bvid)) continue;

                    var mid = GetLong(item, "mid");
                    if (onlyMid.HasValue && mid != onlyMid.Value) continue;

                    list.Add(new PostCandidate
                    {
                        Ref = PostRef.Video(bvid),
                        Title = CleanText(GetStr(item, "title")),
                        PubTs = ParseUnix(item, "pubdate", "pub_time", "senddate"),
                        AuthorUid = mid,
                        Method = FetchMethod.VideoSearch,
                        Why = $"视频搜索「{keyword.Trim()}」（按发布时间倒序）命中"
                            + (onlyMid.HasValue ? "，作者 mid 与目标一致" : ""),
                    });
                }

                gotPage = true;
                if (count == 0 && page == 1 && attempt < 2)
                {
                    var head = json.Length > 260 ? json[..260] : json;
                    log?.Invoke($"[搜索] 空响应样本（{json.Length} 字符）：{head.Replace('\n', ' ')}");
                    var waitMs = attempt == 0 ? 5000 : 10000;
                    log?.Invoke($"[搜索] FetchMethod.VideoSearch「{keyword}」第 1 页返回 0 条（疑似静默限流），{waitMs / 1000} 秒后重试");
                    Thread.Sleep(waitMs);
                    gotPage = false;
                }
            }

            log?.Invoke($"[搜索] FetchMethod.VideoSearch「{keyword}」第 {page}/{pages} 页：{count} 条，累计命中 {list.Count} 条");
            if (count == 0 || count < PageSize) break; // 没有下一页
        }

        return list;
    }

    // ------------------------------------------------------------------
    // JSON / 文本 / 时间解析（搜索接口的字段类型不稳定，统一在这里兜底）
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析 JSON；失败（例如风控返回 HTML）只记日志并返回 null，不抛。
    /// 之所以在这里兜底：搜索是"弱手段"，接口偶发返回网页不能把整条定位链打断。
    /// </summary>
    internal static JsonDocument? TryParseJson(string json, string what, Action<string>? log)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            log?.Invoke($"[解析] {what} 返回的不是合法 JSON：{ex.Message}");
            return null;
        }
    }

    private static bool TryData(JsonDocument doc, out JsonElement data)
    {
        data = default;
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> ResultItems(JsonElement data)
    {
        if (!data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in result.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                yield return item;
    }

    /// <summary>取字符串字段（数字字段也容忍，转成文本）。</summary>
    internal static string GetStr(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    /// <summary>取整数字段（字符串形式也容忍）。</summary>
    internal static long GetLong(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var n)) return n;
            return 0;
        }
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    /// <summary>清理搜索片段：去掉 &lt;em&gt; 等高亮标签、解码 HTML 实体、去掉零宽字符。</summary>
    internal static string CleanText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var text = HtmlTagRegex.Replace(raw, "");
        text = WebUtility.HtmlDecode(text);
        // b 站标题里常混入 U+200B/U+FEFF 零宽字符，会破坏昵称比对与子串匹配。
        text = text.Replace("\u200B", "").Replace("\uFEFF", "").Replace("\u2060", "");
        return text.Trim();
    }

    /// <summary>按字段名依次尝试解析为 unix 秒（接口里时间字段有的叫 pubdate 有的叫 pub_time）。</summary>
    internal static long ParseUnix(JsonElement item, params string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            if (!item.TryGetProperty(name, out var v)) continue;
            var ts = ParseTimeValue(v);
            if (ts > 0) return ts;
        }
        return 0;
    }

    /// <summary>
    /// 解析一个时间值：可能是 unix 秒/毫秒（数字或数字字符串），
    /// 也可能是 "2025-09-06"、"09-06 20:30" 这类本地时间字符串。
    /// </summary>
    internal static long ParseTimeValue(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number)
        {
            var raw = v.TryGetInt64(out var n) ? n : (long)v.GetDouble();
            return NormalizeUnix(raw);
        }
        if (v.ValueKind != JsonValueKind.String) return 0;

        var text = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(text)) return 0;

        // 纯数字（unix 秒或毫秒）
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var digits))
            return NormalizeUnix(digits);

        // 日期字符串：本地时间 → unix 秒（b 站接口返回的就是北京时间本地时间）
        foreach (var format in TimeFormats)
        {
            if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                continue;
            // "09-06 20:30" 这类缺年份的，用当前年份补（只用于候选排序/时间过滤，误差一年可接受；
            // 真实接口里带年份的格式占绝大多数）。
            if (format.StartsWith("MM-dd", StringComparison.Ordinal))
                dt = new DateTime(DateTime.Now.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);
            var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
            return new DateTimeOffset(dt, offset).ToUnixTimeSeconds();
        }

        // 兜底：交给宽松解析，只有解析成功且年份合理才采用。
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose)
            && loose.Year is >= 2009 and <= 2100)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(loose);
            return new DateTimeOffset(loose, offset).ToUnixTimeSeconds();
        }
        return 0;
    }

    /// <summary>毫秒时间戳除以 1000；顺便把明显不合理的值当 0（时间未知）。</summary>
    private static long NormalizeUnix(long value)
    {
        if (value <= 0) return 0;
        if (value > 100_000_000_000) value /= 1000;            // 毫秒
        if (value < 946_684_800 || value > 4_102_444_800) return 0; // 2000 年之前 / 2100 年之后视为无效
        return value;
    }
}
