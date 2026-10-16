#nullable enable
using System.Text;
using System.Text.RegularExpressions;

namespace JieLi.Tui.Data;

/// <summary>一条待修复项。</summary>
public sealed class BackfillIssue
{
    public string Kind { get; init; } = string.Empty;      // 编码 / 表头 / 类型 / 文件名
    public string Detail { get; init; } = string.Empty;
    public int Affected { get; init; } = 1;
}

/// <summary>一个文件的修复报告。</summary>
public sealed class BackfillReport
{
    public string CsvPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string SourceEncoding { get; set; } = string.Empty;
    public bool Fixed { get; set; }
    public string? BackupPath { get; set; }
    public string? Error { get; set; }
    public List<BackfillIssue> Issues { get; init; } = new();
}

/// <summary>
/// M6：旧归档的**零风险回填**。
/// 只做"纯本地可判定、不改变语义"的修复：
///   ① 编码统一为 UTF-8 with BOM（实测 1 个文件是 GB18030/GBK）
///   ② 重复表头修复（22/22 个文件的第 4、6 列都叫"地址"）
///   ③ 类型词表归一化（插图/图片/绘画 → 插画，实测 69 行）
/// 绝不触碰需要联网核对的内容（代发关系、N/A 项、404 类型）——那些进清单由人工处理。
/// 默认 dry-run；应用前对每个文件生成 .bak。
/// </summary>
public static class ArchiveBackfill
{
    /// <summary>类型归一化表（既有 683 行的多数用法：插画 436 行为主）。</summary>
    private static readonly Dictionary<string, string> TypeNorm = new(StringComparer.Ordinal)
    {
        ["插图"] = "插画",
        ["图片"] = "插画",
        ["绘画"] = "插画",
    };

    public static (string Encoding, bool HasBom) Sniff(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return ("UTF-8 (BOM)", true);
        try
        {
            new UTF8Encoding(false, true).GetString(raw);
            return ("UTF-8 (no BOM)", false);
        }
        catch (DecoderFallbackException)
        {
            return ("GB18030/GBK", false);
        }
    }

    /// <summary>分析单个 CSV 的待修复项（不写入）。</summary>
    public static BackfillReport Analyze(string root, string csvPath)
    {
        var report = new BackfillReport
        {
            CsvPath = csvPath,
            RelativePath = Path.GetRelativePath(root, csvPath),
        };

        byte[] raw;
        try { raw = File.ReadAllBytes(csvPath); }
        catch (Exception ex) { report.Error = ex.Message; return report; }

        var (encName, hasBom) = Sniff(raw);
        report.SourceEncoding = encName;

        if (!hasBom)
            report.Issues.Add(new BackfillIssue { Kind = "编码", Detail = $"{encName} → UTF-8 (BOM)" });

        // 解码用于分析（写回时按 UTF-8 重编码）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try
        {
            text = hasBom
                ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                : encName.StartsWith("UTF-8", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetString(raw)
                    : Encoding.GetEncoding("GB18030").GetString(raw);
        }
        catch (Exception ex) { report.Error = "解码失败：" + ex.Message; return report; }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0)
        {
            var header = lines[0].TrimEnd('\r');
            if (header == "棒数,时间,作者,地址,类型,地址")
                report.Issues.Add(new BackfillIssue
                {
                    Kind = "表头",
                    Detail = "第 6 列与第 4 列同名（均为「地址」）—— 历史遗留",
                });
        }

        int typeHits = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var fields = SplitCsvLine(line);
            if (fields.Count < 6) continue;
            var t = fields[4].Trim();
            if (TypeNorm.ContainsKey(t)) typeHits++;
        }
        if (typeHits > 0)
            report.Issues.Add(new BackfillIssue
            {
                Kind = "类型",
                Detail = "插图/图片/绘画 → 插画（与既有 436 行的多数用法一致）",
                Affected = typeHits,
            });

        return report;
    }

    public static List<BackfillReport> AnalyzeAll(string root)
    {
        var list = new List<BackfillReport>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || string.Equals(name, "relay-tool", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var csv in Directory.EnumerateFiles(dir, "*.csv").OrderBy(f => f, StringComparer.Ordinal))
                list.Add(Analyze(root, csv));
        }
        return list;
    }

    /// <summary>应用修复。fixHeader 决定是否改写重复表头（改名会与 legacy 脚本的列名预期不同，故可关）。</summary>
    public static void Apply(
        BackfillReport report,
        bool fixHeader,
        bool fixEncoding = true,
        bool fixTypes = true)
    {
        if (report.Error is not null || report.Issues.Count == 0) return;

        var raw = File.ReadAllBytes(report.CsvPath);
        var (encName, hasBom) = Sniff(raw);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = hasBom
            ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
            : encName.StartsWith("UTF-8", StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(raw)
                : Encoding.GetEncoding("GB18030").GetString(raw);

        // 备份
        var backup = report.CsvPath + ".bak";
        if (!File.Exists(backup)) File.Copy(report.CsvPath, backup);
        report.BackupPath = backup;

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && fixHeader)
            lines[0] = lines[0].TrimEnd('\r') switch
            {
                "棒数,时间,作者,地址,类型,地址" => "棒数,时间,作者,作者链接,类型,作品链接",
                var other => other,
            };

        if (fixTypes)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                var fields = SplitCsvLine(line);
                if (fields.Count < 6) continue;
                var t = fields[4].Trim();
                if (!TypeNorm.TryGetValue(t, out var norm)) continue;
                fields[4] = norm;
                lines[i] = JoinCsvLine(fields);
            }
        }

        var outText = string.Join("\n", lines).TrimEnd('\n') + "\n";
        File.WriteAllText(report.CsvPath, outText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: fixEncoding));
        report.Fixed = true;
    }

    // ---- 极简 RFC4180 单行切分（与 CsvParser 保持同样的语义） ----
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static string JoinCsvLine(List<string> fields)
        => string.Join(",", fields.Select(f =>
            f.Contains(',') || f.Contains('"') ? "\"" + f.Replace("\"", "\"\"") + "\"" : f));
}
