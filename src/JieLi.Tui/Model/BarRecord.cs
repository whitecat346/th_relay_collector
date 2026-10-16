#nullable enable
using System.Text;

namespace JieLi.Tui.Model;

/// <summary>CSV 里一行原始的棒记录（6 列：棒数,时间,作者,作者链接,类型,作品链接）。</summary>
public sealed class BarRecord
{
    public string RawLine { get; init; } = string.Empty;
    public int LineNumber { get; init; }

    public string Bar { get; init; } = string.Empty;        // 棒数（可能是 "EX"/"EX1"/"N/A"）
    public string Time { get; init; } = string.Empty;       // 棒位时间（如 20:30）
    public string Author { get; init; } = string.Empty;     // 作者
    public string AuthorLink { get; init; } = string.Empty; // 作者空间链接
    public string Type { get; init; } = string.Empty;       // 类型（自由词表）
    public string WorkLink { get; init; } = string.Empty;   // 作品链接

    private static readonly string[] NaValues = { "N/A", "n/a", "NA", "404", "—", "-", "" };

    public static bool IsNa(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return true;
        var t = v.Trim();
        return t.Length == 0 || t.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || t == "404" || t == "—" || t == "-";
    }

    public bool AuthorIsNa => IsNa(Author);
    public bool AuthorLinkIsNa => IsNa(AuthorLink);
    public bool WorkLinkIsNa => IsNa(WorkLink);
    public bool TypeIsNa => IsNa(Type);

    /// <summary>棒号解析：数字棒返回 int，EX/ex 类返回 null 并置 IsEx。</summary>
    public int? BarNumber
    {
        get
        {
            var t = Bar.Trim();
            if (int.TryParse(t, out var n)) return n;
            return null;
        }
    }

    public bool IsEx => Bar.Trim().Contains("ex", StringComparison.OrdinalIgnoreCase);

    /// <summary>该行是否含值得人工确认的问题。</summary>
    public bool HasGap => AuthorIsNa || AuthorLinkIsNa || WorkLinkIsNa || TypeIsNa;

    public string GapSummary()
    {
        var parts = new List<string>(4);
        if (AuthorIsNa) parts.Add("作者");
        if (AuthorLinkIsNa) parts.Add("作者链接");
        if (WorkLinkIsNa) parts.Add("作品链接");
        if (TypeIsNa) parts.Add("类型");
        return parts.Count == 0 ? string.Empty : "缺: " + string.Join("/", parts);
    }
}

/// <summary>一个活动归档目录的分析结果。</summary>
public sealed class Activity
{
    public string DirectoryPath { get; init; } = string.Empty;
    public string DirectoryName { get; init; } = string.Empty;
    public string? CsvPath { get; set; }
    public string? MdPath { get; set; }

    public string SourceEncoding { get; set; } = "unknown";
    public bool HasBom { get; set; }
    public List<string> Header { get; init; } = new();
    public List<BarRecord> Bars { get; init; } = new();
    public List<string> Anomalies { get; init; } = new();

    // 从 md 的 ## 宣传 段解析出的宣传项
    public List<PromoItem> Promotions { get; init; } = new();

    public int NaCount => Bars.Count(b => b.HasGap);
    public int ExCount => Bars.Count(b => b.IsEx);
    public int? MaxBar => Bars.Where(b => b.BarNumber.HasValue).Select(b => b.BarNumber!.Value).DefaultIfEmpty().Max() is var m && Bars.Any(b => b.BarNumber.HasValue) ? m : null;
    public bool IsCompliantName => System.Text.RegularExpressions.Regex.IsMatch(DirectoryName, @"^\d{4}_.+_\d{1,2}-\d{1,2}$");
}

public sealed class PromoItem
{
    public string Label { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsEmpty => Text.Length == 0 && Url.Length == 0;
}

/// <summary>一条话题卡片解析出的"接力学"信息。</summary>
public sealed class ParsedBar
{
    public string CardId { get; set; } = string.Empty;
    public long AuthorMid { get; set; }
    /// <summary>发布者（该卡片的所有者），不一定等于创作者。</summary>
    public string PosterName { get; set; } = string.Empty;
    public long PubTs { get; set; }
    public string OpusUrl { get; set; } = string.Empty;

    /// <summary>棒号原始文本（如 "06"、"第六"）；解析不到为 null。</summary>
    public string? BarRaw { get; set; }
    public int? BarNumber { get; set; }
    public bool IsEx { get; set; }
    /// <summary>EX 棒的序号（EX1/EX2…）。</summary>
    public int? ExIndex { get; set; }

    /// <summary>正文里声明的棒位时段（如 "20:30"），解析不到为空。</summary>
    public string? DeclaredTime { get; set; }

    /// <summary>正文里紧跟棒号的 @ 创作者。</summary>
    public string? CreatorInText { get; set; }
    public string? PrevName { get; set; }
    public string? NextName { get; set; }

    /// <summary>本条是否为代发：正文标注的创作者 ≠ 发布者。</summary>
    public bool IsProxy { get; set; }
    /// <summary>本条代发时的代发者（= 发布者）。</summary>
    public string? ProxyName { get; set; }
    /// <summary>上一棒的原作者（"上一棒：X（@Y 代发）" 里的 X），与本条无关。</summary>
    public string? PrevOriginalAuthor { get; set; }
    /// <summary>上一棒的代发者（上一棒那一句里标注的 @Y）。</summary>
    public string? PrevProxyName { get; set; }

    public string? ResolvedAuthor { get; set; }
    public string? ResolvedAuthorLink { get; set; }

    /// <summary>命中的棒号/时段写法（A–H），用于验证解析器的覆盖面。</summary>
    public string? MatchedStyle { get; set; }

    /// <summary>该卡片是否带本活动的话题标签（归属判定的最强信号）。</summary>
    public bool HasTopicTag { get; set; }

    /// <summary>该卡片是否为活动的总结/名册动态（不是一根棒）。</summary>
    public bool IsSummary { get; set; }

    public List<string> Conflicts { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>本条卡片的"发布时刻"与"声明时段"的偏差（分钟）；任一缺失为 null。</summary>
    public int? TimeDeviationMinutes { get; set; }

    public bool IsUnattributable => BarNumber is null && !IsEx;
}
