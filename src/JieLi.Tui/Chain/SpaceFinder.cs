#nullable enable
using System.Text;
using System.Text.Json;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/SearchClient.cs / Chain/UserResolver.cs / Chain/UserCache.cs。
//
// 职责：给定 uid + 活动 + 期望槽位，找出该用户的候选帖子（不含最终判定，判定归 Agent「chain」）。
// 主端点（已实测匿名可用，20 条/页，has_more + offset 游标）：
//   x/polymer/web-dynamic/v1/opus/feed/space?host_mid=&page=&offset=&type=all
//   item 里 pub_time 恒为空串、content 是整段正文（首行≈标题），所以候选只能靠
//   「标题过滤 + 之后点开确认」；这里只负责**产出候选**，不要在这里做棒号判定。
//
// 为什么不用 x/space/wbi/arc/search（视频列表）与 x/space/acc/info：
//   匿名会 -352 风控；x/polymer/web-dynamic/v1/feed/space 是静默空（code=0 但 total=0），
//   也不要用。视频类候选走 ChainSearchClient.SearchVideos（搜索）而不是空间视频列表。
// ===========================================================================

/// <summary>空间帖子候选搜索。</summary>
public sealed class SpaceFinder
{
    private const string OpusFeedApi = "https://api.bilibili.com/x/polymer/web-dynamic/v1/opus/feed/space";

    /// <summary>翻页上限（硬约束：每次调用最多 5 页）。</summary>
    private const int MaxPages = 5;

    /// <summary>接口固定 20 条/页，用来判断最后一页。</summary>
    private const int PageSize = 20;

    /// <summary>候选标题只做展示/审计，截断避免把整段正文塞进报告。</summary>
    private const int MaxTitleLength = 120;

    private readonly ChainHttp _http;

    public SpaceFinder(ChainHttp http) => _http = http;

    /// <summary>取该用户在某活动时间窗附近的候选帖子（未验证）。</summary>
    public IReadOnlyList<PostCandidate> FindCandidates(long uid, string? relayName, RelayQuery query, Action<string>? log, CancellationToken ct)
    {
        var list = new List<PostCandidate>();
        if (uid <= 0)
        {
            log?.Invoke("[空间] uid 无效，跳过");
            return list;
        }

        // 活动名先规范化一次，后面每条正文都拿它比，省重复处理。
        var relay = string.IsNullOrWhiteSpace(relayName) ? null : Canon(relayName);
        if (relay is null)
            log?.Invoke("[空间] 未给活动名：只按时间窗收候选，不做标题过滤");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 标题过滤失败的条目：接口只给「正文首行」，接力帖首行常常只有「第9棒 15:00」，
        // 不含活动名 → 严格过滤会把真正的那一棒全丢掉（实测 uid=516454795 100 条 0 命中）。
        // 因此留一份宽松候选，严格池为空时按 opus id 邻近兜底，让 MatchVerifier 读正文后再判。
        var loose = new List<(string OpusId, string Title)>();
        string? offset = null;

        for (var page = 1; page <= MaxPages; page++)
        {
            var url = $"{OpusFeedApi}?host_mid={uid}&page={page}&type=all";
            if (!string.IsNullOrEmpty(offset))
                url += $"&offset={Uri.EscapeDataString(offset)}";

            string json;
            try
            {
                json = _http.GetApiJson(url, ct: ct);
            }
            catch (ChainFetchException ex)
            {
                // 拿不到就退到下一页/外层阶梯；不要在这里重试（ChainHttp 已做过退避）。
                log?.Invoke($"[空间] uid={uid} 第 {page} 页请求失败：{ex.Message}");
                break;
            }

            using var doc = ChainSearchClient.TryParseJson(json, $"空间动态 uid={uid} 第 {page} 页", log);
            if (doc is null) break;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                break;
            if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                break;

            var count = 0;
            var before = list.Count;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                count++;

                var opusId = ChainSearchClient.GetStr(item, "opus_id");
                if (opusId.Length == 0) opusId = ExtractOpusId(ChainSearchClient.GetStr(item, "jump_url"));
                if (opusId.Length == 0) continue;
                if (!seen.Add(opusId)) continue;

                var content = ChainSearchClient.CleanText(ChainSearchClient.GetStr(item, "content"));
                // pub_time 实测恒为空串（0 = 时间未知）。时间未知时 InWindow 返回 true：
                // 宁可多收候选，也不要因为缺字段漏掉目标（最终由 MatchVerifier 用发布时间复核）。
                var pubTs = ChainSearchClient.ParseUnix(item, "pub_time", "pubdate", "ptime");

                if (!InWindow(pubTs, query)) continue;

                string why;
                if (relay is null)
                {
                    why = pubTs > 0 ? "空间动态：未给活动名，仅按时间窗收候选" : "空间动态：未给活动名，收近期候选";
                }
                else if (!TryMatch(content, relay, out why))
                {
                    loose.Add((opusId, FirstLine(content)));
                    continue;
                }

                if (pubTs > 0) why += $"；发布时间 {DateTimeOffset.FromUnixTimeSeconds(pubTs).ToLocalTime():yyyy-MM-dd HH:mm}";

                list.Add(new PostCandidate
                {
                    Ref = PostRef.Opus(opusId),
                    Title = FirstLine(content),
                    PubTs = pubTs,
                    Method = FetchMethod.SpaceOpusFeed,
                    Why = why,
                });
            }

            log?.Invoke($"[空间] FetchMethod.SpaceOpusFeed uid={uid} 第 {page}/{MaxPages} 页：{count} 条，候选 +{list.Count - before}（累计 {list.Count}）");

            if (count == 0 || count < PageSize) break; // 没有下一页
            var hasMore = data.TryGetProperty("has_more", out var hm)
                && (hm.ValueKind == JsonValueKind.True || (hm.ValueKind == JsonValueKind.String && hm.GetString() == "true"));
            if (!hasMore) break;
            offset = ChainSearchClient.GetStr(data, "offset");
            if (offset.Length == 0) break;
        }

        // 严格池为空 → 宽松兜底：按 opus id 邻近取最可能的那几条。
        // opus id 全局随时间递增（雪花式），所以「离源帖 id 最近」≈「发布时间最接近」，
        // 这是不额外发请求就能做的时间近似排序。数量封顶，控制读取成本。
        if (list.Count == 0 && relay is not null && loose.Count > 0)
        {
            var anchor = query.AnchorPostId;
            IEnumerable<(string OpusId, string Title)> pick = loose;
            if (anchor > 0)
            {
                pick = loose
                    .OrderBy(t => Distance(long.TryParse(t.OpusId, out var oid) ? oid : long.MaxValue, anchor))
                    .Take(MaxLooseCandidates);
            }
            else
            {
                pick = loose.Take(MaxLooseCandidates);
            }

            foreach (var (opusId, title) in pick)
            {
                list.Add(new PostCandidate
                {
                    Ref = PostRef.Opus(opusId),
                    Title = title,
                    PubTs = 0,
                    Method = FetchMethod.SpaceOpusFeed,
                    Why = anchor > 0
                        ? $"标题未见活动名，按 opus id 邻近兜底（源帖 {anchor}）；需读正文验证"
                        : "标题未见活动名，按动态顺序兜底（无源帖锚点）；需读正文验证",
                });
            }

            log?.Invoke($"[空间] FetchMethod.SpaceOpusFeed uid={uid} 严格匹配 0 条 → 宽松兜底 {list.Count} 条（读取正文后再验收）");
        }

        if (relay is not null)
            log?.Invoke($"[空间] FetchMethod.SpaceOpusFeed uid={uid}「{relayName}」→ {list.Count} 条候选");
        return list;
    }

    /// <summary>宽松兜底的候选上限：再多读取成本就压不住了。</summary>
    internal const int MaxLooseCandidates = 8;

    private static long Distance(long a, long b) => a > b ? a - b : b - a;

    // ------------------------------------------------------------------
    // 匹配与时间窗（ChainUserLocator 也复用这两个静态方法）
    // ------------------------------------------------------------------

    /// <summary>
    /// 规范化用于「包含/公共子串」比较的文本：去空白与标点、全角转半角、ASCII 转小写。
    /// 保留中日韩文字与字母数字，这样「【8.9结界日32h】」与「结界日」能直接 contains。
    /// </summary>
    internal static string Canon(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = raw;
            if (c == '\u3000' || char.IsWhiteSpace(c)) continue;
            if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0); // 全角 ASCII → 半角
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 有活动名时判断正文是否值得收：完整包含优先；否则看最长公共子串够不够长（弱匹配）。
    /// 中文活动名常被拆开/加装饰（「【8.9结界日32h】」），所以不能只做全等。
    /// </summary>
    internal static bool TryMatch(string content, string relayCanon, out string why)
    {
        var text = Canon(content);
        if (relayCanon.Length == 0) { why = ""; return true; }

        if (text.Contains(relayCanon, StringComparison.Ordinal))
        {
            why = "标题/正文含活动名";
            return true;
        }

        var lcs = LongestCommonSubstring(text, relayCanon);
        // 两字活动名必须完整出现；更长的允许一半以上的公共子串（错字/断行/装饰容忍）。
        var need = relayCanon.Length <= 2 ? relayCanon.Length : Math.Max(2, (relayCanon.Length + 1) / 2);
        if (lcs >= need)
        {
            why = $"标题/正文与活动名有 {lcs} 字连续公共子串（弱匹配）";
            return true;
        }

        why = "";
        return false;
    }

    /// <summary>最长公共子串长度（正文可能几百字、活动名很短，直接 O(n*m) 可接受）。</summary>
    internal static int LongestCommonSubstring(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        var best = 0;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    cur[j] = prev[j - 1] + 1;
                    if (cur[j] > best) best = cur[j];
                }
                else cur[j] = 0;
            }
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return best;
    }

    /// <summary>
    /// 候选是否落在搜索时间窗内。
    /// 约定：DayStart == MinValue 表示「调用方还没定日期」→ 不过滤；
    /// Year==1 但月日有效表示只给了月-日，年份用候选自身年份补齐（跨年回退一年）。
    /// pubTs == 0 表示接口没给时间 → 保留候选（缺字段不能当作"不在窗口"）。
    /// </summary>
    internal static bool InWindow(long pubTs, RelayQuery query)
    {
        if (query.DayStart == DateTime.MinValue) return true;
        if (pubTs <= 0) return true;

        var local = DateTimeOffset.FromUnixTimeSeconds(pubTs).ToLocalTime().DateTime;

        // 活动时长：DayEnd 有效时用它；否则按 24h 估算（多数是 24h 接力）。
        var spanHours = 24.0;
        if (query.DayEnd != DateTime.MinValue && query.DayEnd > query.DayStart)
            spanHours = (query.DayEnd - query.DayStart).TotalHours;

        DateTime start;
        if (query.DayStart.Year > 1)
        {
            start = query.DayStart.AddHours(-2); // == query.SearchStart
        }
        else
        {
            // 只给了月-日：用候选自己的年份补；若补出来的日期比候选晚很多，说明跨年，回退一年。
            var guess = new DateTime(local.Year, query.DayStart.Month, query.DayStart.Day);
            if (guess > local.AddDays(40)) guess = guess.AddYears(-1);
            start = guess.AddHours(-2);
        }

        // end = SearchEnd = DayEnd + 6h = start + 2h + span + 6h
        var end = start.AddHours(spanHours + 8);
        return local >= start && local <= end;
    }

    private static string ExtractOpusId(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var slash = url.LastIndexOf('/');
        return slash >= 0 && slash + 1 < url.Length ? url[(slash + 1)..] : "";
    }

    /// <summary>取正文第一行非空内容当标题（content 是整段正文，首行≈标题）。</summary>
    internal static string FirstLine(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.Length <= MaxTitleLength ? line : line[..MaxTitleLength] + "…";
        }
        return "";
    }
}
