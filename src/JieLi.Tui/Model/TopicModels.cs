#nullable enable
using System.Text.Json.Serialization;

namespace JieLi.Tui.Model;

// ---------------------------------------------------------------------------
// B站话题接口的响应模型。
// 实测端点（免登录可用）：
//   GET x/topic/web/details/top?topic_id=          → 活动身份 + 总动态数
//   GET x/topic/web/details/cards?topic_id=&sort_by=3&page_size=20&offset=
// 注意：page_size 被服务端强制为 20；排序 2=热门（默认）、3=最新（时间序）。
// ---------------------------------------------------------------------------

public sealed class TopicTopResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public TopicTopData? Data { get; set; }
}

public sealed class TopicTopData
{
    [JsonPropertyName("top_details")] public TopicTopDetails? TopDetails { get; set; }
}

public sealed class TopicTopDetails
{
    [JsonPropertyName("topic_item")] public TopicItem? TopicItem { get; set; }
}

public sealed class TopicItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>话题下的动态总数（实测 34）——用于"卡片数 == dynamics"的硬断言。</summary>
    [JsonPropertyName("dynamics")] public int Dynamics { get; set; }
    [JsonPropertyName("discuss")] public int Discuss { get; set; }
    [JsonPropertyName("view")] public long View { get; set; }
    [JsonPropertyName("jump_url")] public string? JumpUrl { get; set; }
}

public sealed class TopicCardsResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public TopicCardsData? Data { get; set; }
}

public sealed class TopicCardsData
{
    [JsonPropertyName("topic_card_list")] public TopicCardList? TopicCardList { get; set; }
}

public sealed class TopicCardList
{
    [JsonPropertyName("items")] public List<TopicCardItem>? Items { get; set; }
    /// <summary>下一页游标（字符串形式，如 "579005257824480"）。</summary>
    [JsonPropertyName("offset")] public string? Offset { get; set; }
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
}

public sealed class TopicCardItem
{
    [JsonPropertyName("topic_type")] public string? TopicType { get; set; }
    /// <summary>运行时判定：键名以 _card_item 结尾（dynamic_card_item / video_card_item / …）。</summary>
    [JsonIgnore] public string? CardItemKey { get; set; }
    [JsonPropertyName("dynamic_card_item")] public CardItem? DynamicCardItem { get; set; }
}

public sealed class CardItem
{
    /// <summary>动态 id，拼成 https://www.bilibili.com/opus/{id_str}。</summary>
    [JsonPropertyName("id_str")] public string? IdStr { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("topic_id")] public long TopicId { get; set; }
    [JsonPropertyName("modules")] public CardModules? Modules { get; set; }
}

public sealed class CardModules
{
    [JsonPropertyName("module_author")] public ModuleAuthor? Author { get; set; }
    [JsonPropertyName("module_dynamic")] public ModuleDynamic? Dynamic { get; set; }
}

public sealed class ModuleAuthor
{
    [JsonPropertyName("mid")] public long Mid { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("pub_ts")] public long PubTs { get; set; }
    /// <summary>粗粒度时间标签，如 "10月1日"（不可用于排序）。</summary>
    [JsonPropertyName("pub_time")] public string? PubTime { get; set; }
}

public sealed class ModuleDynamic
{
    [JsonPropertyName("desc")] public DynDesc? Desc { get; set; }
    /// <summary>保留原始 JSON：正文在不同卡片类型里藏在不同字段（draw/archive/article/video…）。</summary>
    [JsonPropertyName("major")] public System.Text.Json.JsonElement? Major { get; set; }
}

public sealed class DynDesc
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>归一化后的卡片视图 —— 采集层与解析层之间的稳定边界。</summary>
public sealed class TopicCard
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public long PubTs { get; set; }
    public string? PubTimeLabel { get; set; }

    /// <summary>来源字段名 → 该字段的文本（如 "desc.text"、"major.archive.desc"）。</summary>
    public Dictionary<string, string> TextSources { get; set; } = new();

    /// <summary>全部来源按优先级顺序拼接后的正文（供解析层做单次正则）。</summary>
    public string MergedText { get; set; } = string.Empty;

    public string OpusUrl => string.IsNullOrEmpty(Id) ? string.Empty : $"https://www.bilibili.com/opus/{Id}";
    public string SpaceUrl => AuthorMid > 0 ? $"https://space.bilibili.com/{AuthorMid}" : string.Empty;
}
