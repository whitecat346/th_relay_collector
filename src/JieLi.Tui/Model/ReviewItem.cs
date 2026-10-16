#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JieLi.Tui.Data;

namespace JieLi.Tui.Model;

/// <summary>
/// M4 人工确认的单元 —— review.jsonl 的一行。
/// 采集与自动判定产出的"候选记录"；人工在 TUI 里改的就是它，改完写回、重跑断言。
/// </summary>
public sealed class ReviewItem
{
    [JsonPropertyName("card_id")] public string CardId { get; set; } = string.Empty;
    [JsonPropertyName("bar")] public int? Bar { get; set; }
    [JsonPropertyName("bar_raw")] public string? BarRaw { get; set; }
    [JsonPropertyName("is_ex")] public bool IsEx { get; set; }
    [JsonPropertyName("declared_time")] public string? DeclaredTime { get; set; }
    [JsonPropertyName("pub_ts")] public long PubTs { get; set; }
    [JsonPropertyName("pub_time")] public string PubTimeText { get; set; } = string.Empty;
    [JsonPropertyName("deviation_minutes")] public int? DeviationMinutes { get; set; }

    [JsonPropertyName("poster_name")] public string PosterName { get; set; } = string.Empty;
    [JsonPropertyName("poster_mid")] public long PosterMid { get; set; }
    [JsonPropertyName("creator_in_text")] public string? CreatorInText { get; set; }
    [JsonPropertyName("resolved_author")] public string? ResolvedAuthor { get; set; }
    [JsonPropertyName("resolved_author_link")] public string? ResolvedAuthorLink { get; set; }
    [JsonPropertyName("prev_name")] public string? PrevName { get; set; }
    [JsonPropertyName("next_name")] public string? NextName { get; set; }
    [JsonPropertyName("looks_like_proxy")] public bool IsProxy { get; set; }
    [JsonPropertyName("proxy_name")] public string? ProxyName { get; set; }
    [JsonPropertyName("prev_original_author")] public string? PrevOriginalAuthor { get; set; }

    [JsonPropertyName("opus_url")] public string OpusUrl { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("conflicts")] public List<string> Conflicts { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    [JsonPropertyName("unattributable")] public bool Unattributable { get; set; }
    [JsonPropertyName("is_summary")] public bool IsSummary { get; set; }
    [JsonPropertyName("matched_style")] public string? MatchedStyle { get; set; }
    [JsonPropertyName("merged_text")] public string MergedText { get; set; } = string.Empty;

    // ---- 人工确认状态（M4） ----
    /// <summary>人工确认后置 true；未确认的行不允许进入最终产物。</summary>
    [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
    /// <summary>人工判定为"这条不是一根棒"（陪跑/无关投稿）。</summary>
    [JsonPropertyName("ignored")] public bool Ignored { get; set; }
    [JsonPropertyName("manual_note")] public string? ManualNote { get; set; }

    [JsonIgnore]
    public bool NeedsAttention =>
        Conflicts.Count > 0
        || Unattributable
        || Bar is null && !IsEx
        || (DeviationMinutes is { } d && Math.Abs(d) > ArtifactValidator.DeviationWarnMinutes)
        || IsProxy;

    [JsonIgnore]
    public string StatusLabel
    {
        get
        {
            if (Ignored) return "已忽略";
            if (Confirmed) return "已确认";
            if (Unattributable) return "无法归属";
            if (Conflicts.Count > 0) return "冲突";
            if (IsProxy) return "代发";
            if (Bar is null && !IsEx) return "无棒号";
            if (DeviationMinutes is { } d && Math.Abs(d) > ArtifactValidator.DeviationWarnMinutes) return "时段可疑";
            return "ok";
        }
    }
}

/// <summary>review.jsonl 的读写。人工确认结果写回同一文件（保留原始证据字段）。</summary>
public static class ReviewStore
{
    private static readonly JsonSerializerOptions Out = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string PathFor(string workRoot, long topicId)
        => Path.Combine(workRoot, topicId.ToString(), "review.jsonl");

    public static bool Exists(string workRoot, long topicId) => File.Exists(PathFor(workRoot, topicId));

    /// <summary>
    /// 读取 review.jsonl。
    /// 兼容两种键名：① 写出的 snake_case（JsonPropertyName）；② 早期版本写出的 PascalCase。
    /// 早期版本 Load 只认 snake_case 而 Save 用默认 PascalCase，导致人工确认"存了但读不回"——
    /// 表现为自检里 已确认 3 条、重新加载却是 0 条。这里做大小写无关查找，彻底消除该隐患。
    /// </summary>
    public static List<ReviewItem> Load(string workRoot, long topicId)
    {
        var path = PathFor(workRoot, topicId);
        if (!File.Exists(path)) return new List<ReviewItem>();

        var list = new List<ReviewItem>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                list.Add(new ReviewItem
                {
                    CardId = Str(r, "card_id"),
                    Bar = IntOrNull(r, "bar"),
                    BarRaw = StrOrNull(r, "bar_raw"),
                    IsEx = Bool(r, "is_ex"),
                    DeclaredTime = StrOrNull(r, "declared_time"),
                    PubTs = Long(r, "pub_ts"),
                    PubTimeText = Str(r, "pub_time"),
                    DeviationMinutes = IntOrNull(r, "deviation_minutes"),
                    PosterName = Str(r, "poster_name"),
                    PosterMid = Long(r, "poster_mid"),
                    CreatorInText = StrOrNull(r, "creator_in_text"),
                    ResolvedAuthor = StrOrNull(r, "resolved_author"),
                    ResolvedAuthorLink = StrOrNull(r, "resolved_author_link"),
                    PrevName = StrOrNull(r, "prev_name"),
                    NextName = StrOrNull(r, "next_name"),
                    IsProxy = Bool(r, "looks_like_proxy"),
                    ProxyName = StrOrNull(r, "proxy_name"),
                    PrevOriginalAuthor = StrOrNull(r, "prev_original_author"),
                    OpusUrl = Str(r, "opus_url"),
                    Type = Str(r, "type"),
                    Unattributable = Bool(r, "unattributable"),
                    IsSummary = Bool(r, "is_summary"),
                    MatchedStyle = StrOrNull(r, "matched_style"),
                    MergedText = Str(r, "merged_text"),
                    Confirmed = Bool(r, "confirmed"),
                    Ignored = Bool(r, "ignored"),
                    ManualNote = StrOrNull(r, "manual_note"),
                    Conflicts = StrList(r, "conflicts"),
                    Notes = StrList(r, "notes"),
                });
            }
            catch { /* 单行损坏不影响整体 */ }
        }
        return list;
    }

    public static void Save(string workRoot, long topicId, IEnumerable<ReviewItem> items)
    {
        var path = PathFor(workRoot, topicId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var it in items) w.WriteLine(JsonSerializer.Serialize(it, Out));
    }

    // ---- 大小写无关的取值 ----
    private static bool TryGet(JsonElement e, string name, out JsonElement v)
    {
        if (e.TryGetProperty(name, out v)) return true;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        v = default;
        return false;
    }

    private static string Str(JsonElement e, string n)
        => TryGet(e, n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? StrOrNull(JsonElement e, string n)
        => TryGet(e, n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string n)
        => TryGet(e, n, out var v) && v.ValueKind == JsonValueKind.True;

    private static long Long(JsonElement e, string n)
        => TryGet(e, n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private static int? IntOrNull(JsonElement e, string n)
        => TryGet(e, n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static List<string> StrList(JsonElement e, string n)
        => TryGet(e, n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
}
