#nullable enable
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JieLi.Tui.Chain;

// ============================================================================
// TopicIndex —— 「一次拉话题卡片，换一张名字→uid→帖子」的本地索引
//
// 背景（2026-10-19 实测）：
//   用户反馈 `--link https://www.bilibili.com/opus/1256711085842497553` 会触发风控
//   （浏览器访问同一条却正常）。复现后确认卡点在**搜索接口**：
//   `x/web-interface/wbi/search/type` 会在链路遍历中途开始回
//   `{"code":0,"data":{"v_voucher":"voucher_..."}}`，于是所有「按昵称找 uid、
//   按 uid 找帖子」的步骤全废，第 5 棒（视频）与第 15 棒直接变成缺口。
//
//   而该接力本身有话题：topic_id=1349127。
//   `x/topic/web/details/cards?topic_id=…&sort_by=3&page_size=20` 匿名可用、
//   不带 wbi 也不挑战、一次 20 张翻页，每张卡直接给：
//     id_str（帖子 id）/ module_author{mid,name,pub_ts} / module_dynamic.desc.text
//     / module_dynamic.major{type,archive.bvid,article.id}
//   46 张卡恰好就等于整场接力的成员表 —— **完全不需要搜索接口**。
//
// 于是这套索引是「搜索被风控」这一现实下的第一条腿，而不是可选优化：
//   Resolve(name) → 先在索引里按昵称找 uid（含简繁/异体折叠）
//   FindPosts(uid) → 直接从索引按 uid 取帖子（含视频 bvid）
// ============================================================================

/// <summary>话题里的一张卡片（一条帖子）的轻量投影。</summary>
public sealed class TopicCardEntry
{
    /// <summary>帖子 id：动态/opus 是 19 位数字；视频是 bvid（Kind=Video 时）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>帖子类型。</summary>
    public PostKind Kind { get; set; } = PostKind.Opus;

    /// <summary>原帖地址（给报告/日志看）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>作者 uid。</summary>
    public long AuthorUid { get; set; }

    /// <summary>作者昵称（卡面上的原文）。</summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>发布时间（unix 秒）。</summary>
    public long PubTs { get; set; }

    /// <summary>卡面正文片段（可能为空：文章/视频卡常常不带 desc）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>标题（视频/文章卡有，动态卡通常为空）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>卡片原始类型，如 DYNAMIC_TYPE_DRAW / DYNAMIC_TYPE_ARTICLE / DYNAMIC_TYPE_AV。</summary>
    public string CardType { get; set; } = string.Empty;
}

/// <summary>
/// 一个话题的卡片索引。构造后只读；拉取失败时 <see cref="Cards"/> 为空，
/// 调用方应当自动退回到搜索路径（索引只是「更稳的一条腿」，不是硬依赖）。
/// </summary>
public sealed class TopicIndex
{
    private const int PageSize = 20;
    private const int MaxPages = 10;

    private TopicIndex(long topicId)
    {
        TopicId = topicId;
    }

    public long TopicId { get; }

    /// <summary>话题名（接口给的原名；拉不到就是 null）。</summary>
    public string? Name { get; private set; }

    public List<TopicCardEntry> Cards { get; } = new();

    /// <summary>索引是否真的建立起来了（有卡片 or 明确拉到话题信息）。</summary>
    public bool IsBuilt => Cards.Count > 0;

    /// <summary>拉取失败原因（进日志/报告，别静默吞掉）。</summary>
    public string? Failure { get; private set; }

    /// <summary>
    /// 拉取话题卡片并建立索引。任何异常都只记 <see cref="Failure"/> 而返回一个空索引，
    /// 让调用方自然退回到搜索路径。（话题接口不需要登录，实测匿名可用。）
    /// </summary>
    public static TopicIndex Load(ChainHttp http, long topicId, Action<string>? log, CancellationToken ct)
    {
        var index = new TopicIndex(topicId);
        try
        {
            // ① 话题标题（顺带确认话题存在）
            var topUrl = $"https://api.bilibili.com/x/topic/web/details/top?topic_id={topicId}";
            try
            {
                var topJson = http.GetApiJson(topUrl, "https://www.bilibili.com/", ct);
                using var topDoc = JsonDocument.Parse(topJson);
                if (topDoc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("top_details", out var top)
                    && top.ValueKind == JsonValueKind.Object
                    && top.TryGetProperty("topic_item", out var item)
                    && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var nameEl))
                {
                    index.Name = nameEl.GetString();
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[话题索引] 取话题标题失败（不影响卡片）：{ex.Message}");
            }

            // ② 翻页拉卡片
            string? offset = null;
            for (int page = 1; page <= MaxPages; page++)
            {
                var url = $"https://api.bilibili.com/x/topic/web/details/cards?topic_id={topicId}" +
                          $"&sort_by=3&page_size={PageSize}";
                if (!string.IsNullOrEmpty(offset)) url += $"&offset={Uri.EscapeDataString(offset)}";

                var json = http.GetApiJson(url, "https://www.bilibili.com/", ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("topic_card_list", out var list)
                    || list.ValueKind != JsonValueKind.Object) break;

                int before = index.Cards.Count;
                if (list.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                        index.AddCard(item);
                }

                var hasMore = list.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                string? nextOffset = null;
                if (list.TryGetProperty("offset", out var offEl))
                {
                    nextOffset = offEl.ValueKind switch
                    {
                        JsonValueKind.String => offEl.GetString(),
                        JsonValueKind.Number => offEl.ToString(),
                        _ => null,
                    };
                }

                if (!hasMore || index.Cards.Count == before) break;
                if (string.IsNullOrEmpty(nextOffset) || nextOffset == offset) break;
                offset = nextOffset;
            }

            log?.Invoke($"[话题索引] topic_id={topicId}「{index.Name}」拉到 {index.Cards.Count} 张卡片" +
                        $"（{index.Cards.Select(c => c.AuthorUid).Distinct().Count()} 个作者）");
        }
        catch (Exception ex)
        {
            index.Failure = ex.Message;
            log?.Invoke($"[话题索引] 建立失败（退回搜索路径）：{ex.Message}");
        }
        return index;
    }

    private void AddCard(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return;

        // 卡片键名是 `<kind>_card_item`，实测话题里全是 dynamic_card_item
        JsonElement card = default;
        bool found = false;
        foreach (var prop in item.EnumerateObject())
        {
            if (!prop.Name.EndsWith("_card_item", StringComparison.Ordinal)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            card = prop.Value;
            found = true;
            break;
        }
        if (!found) return;

        var entry = new TopicCardEntry();
        entry.CardType = Str(card, "type") ?? "";
        var idStr = Str(card, "id_str") ?? Str(card, "id") ?? "";

        if (card.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Object)
        {
            if (modules.TryGetProperty("module_author", out var author) && author.ValueKind == JsonValueKind.Object)
            {
                entry.AuthorUid = Long(author, "mid");
                entry.AuthorName = Str(author, "name") ?? "";
                entry.PubTs = Long(author, "pub_ts");
            }

            if (modules.TryGetProperty("module_dynamic", out var dyn) && dyn.ValueKind == JsonValueKind.Object)
            {
                if (dyn.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Object)
                    entry.Text = Str(desc, "text") ?? "";

                if (dyn.TryGetProperty("major", out var major) && major.ValueKind == JsonValueKind.Object)
                {
                    // 视频卡：major.archive.bvid —— 这类卡片就是「第 5 棒那种只在话题里出现的视频」
                    if (major.TryGetProperty("archive", out var archive) && archive.ValueKind == JsonValueKind.Object)
                    {
                        var bvid = Str(archive, "bvid");
                        if (!string.IsNullOrWhiteSpace(bvid))
                        {
                            entry.Kind = PostKind.Video;
                            entry.Id = bvid!;
                            entry.Url = $"https://www.bilibili.com/video/{bvid}/";
                            entry.Title = Str(archive, "title") ?? "";
                        }
                    }
                    // 文章卡：major.article.id 是 cv 号
                    if (entry.Kind == PostKind.Opus
                        && major.TryGetProperty("article", out var article) && article.ValueKind == JsonValueKind.Object)
                    {
                        var articleId = Long(article, "id");
                        if (articleId > 0)
                        {
                            entry.Url = $"https://www.bilibili.com/read/cv{articleId}";
                            entry.Title = Str(article, "title") ?? entry.Title;
                        }
                    }
                }
            }
        }

        // 动态/opus 卡：Id 就是帖子 id，地址可按 opus 构造
        if (entry.Kind == PostKind.Opus)
        {
            if (string.IsNullOrWhiteSpace(idStr)) return;
            entry.Id = idStr!;
            if (string.IsNullOrWhiteSpace(entry.Url))
                entry.Url = $"https://www.bilibili.com/opus/{idStr}";
        }

        if (entry.AuthorUid <= 0 || string.IsNullOrWhiteSpace(entry.Id)) return;
        if (Cards.Any(c => c.Id == entry.Id)) return;
        Cards.Add(entry);
    }

    /// <summary>
    /// 昵称 → uid。先精确，再折叠简繁/异体，最后取「折叠后相等且唯一」的候选。
    /// 命中多个不同 uid 时返回 null（宁可交人工，也不瞎猜）。
    /// </summary>
    public UserRef? ResolveByName(string name, Action<string>? log = null)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return null;

        var exact = Cards.Where(c => string.Equals(c.AuthorName.Trim(), wanted, StringComparison.Ordinal))
                         .Select(c => (c.AuthorUid, c.AuthorName))
                         .Distinct().ToList();
        if (exact.Count == 1)
        {
            log?.Invoke($"[话题索引] 昵称「{wanted}」精确命中 uid={exact[0].AuthorUid}「{exact[0].AuthorName}」");
            return new UserRef { Uid = exact[0].AuthorUid, Name = exact[0].AuthorName, Method = FetchMethod.TopicAuthorTable, Exact = true };
        }

        var wantKey = NameFold.Fold(wanted);
        var folded = Cards.Where(c => NameFold.Fold(c.AuthorName) == wantKey)
                          .Select(c => (c.AuthorUid, c.AuthorName))
                          .Distinct().ToList();
        if (folded.Count == 1)
        {
            log?.Invoke($"[话题索引] 昵称「{wanted}」折叠后命中 uid={folded[0].AuthorUid}「{folded[0].AuthorName}」（简繁/异体差异）");
            return new UserRef { Uid = folded[0].AuthorUid, Name = folded[0].AuthorName, Method = FetchMethod.TopicAuthorTable, Exact = false };
        }
        if (folded.Count > 1)
            log?.Invoke($"[话题索引] 昵称「{wanted}」折叠后有 {folded.Count} 个不同 uid，放弃猜测");
        return null;
    }

    /// <summary>该 uid 在话题里的全部卡片（按发布时间升序）。</summary>
    public IReadOnlyList<TopicCardEntry> ByUid(long uid) =>
        Cards.Where(c => c.AuthorUid == uid).OrderBy(c => c.PubTs).ToList();

    /// <summary>
    /// 按「卡面正文提到这个名字」找卡片。用途：**代发棒**。
    /// 实测这一场 17:00 那棒就是「龙胆紫苑 代发 @茶猫祈祷中」：
    /// 作者是代发账号，正文里写着 `17:00 @茶猫祈祷中`，所以按作者名永远搜不到。
    /// </summary>
    public IReadOnlyList<TopicCardEntry> FindByMention(string name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return Array.Empty<TopicCardEntry>();
        var withAt = "@" + wanted;
        return Cards
            .Where(c => c.Text.Contains(withAt, StringComparison.Ordinal) || c.Text.Contains(wanted, StringComparison.Ordinal))
            .OrderBy(c => c.PubTs)
            .ToList();
    }

    // ------------------------------------------------------------------ JSON 小工具

    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
    }

    private static long Long(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var v) ? v : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0,
        };
    }
}

/// <summary>昵称折叠（简繁/异体/常见混写）。与 ChainWalker 的 FoldVariant 保持同一张表。</summary>
internal static class NameFold
{
    private static readonly Dictionary<char, char> Map = new()
    {
        ['霊'] = '灵', ['靈'] = '灵', ['夢'] = '梦', ['龍'] = '龙', ['櫻'] = '樱', ['繪'] = '绘',
        ['輝'] = '辉', ['館'] = '馆', ['華'] = '华', ['姫'] = '姬', ['霧'] = '雾', ['霚'] = '雾',
        ['園'] = '园', ['學'] = '学', ['園'] = '园', ['畫'] = '画', ['髮'] = '发', ['髮'] = '发',
        ['麗'] = '丽', ['詩'] = '诗', ['終'] = '终', ['緒'] = '绪', ['綠'] = '绿', ['紅'] = '红',
        ['黒'] = '黑', ['點'] = '点', ['爾'] = '尔', ['読'] = '读', ['読'] = '读', ['體'] = '体',
        ['聲'] = '声', ['歡'] = '欢', ['樂'] = '乐', ['蘭'] = '兰', ['貓'] = '猫', ['隱'] = '隐',
    };

    public static string Fold(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(Map.TryGetValue(ch, out var mapped) ? mapped : ch);
        }
        return sb.ToString();
    }
}
