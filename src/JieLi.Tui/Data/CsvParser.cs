#nullable enable
using System.Text;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>CSV 解析结果：行 + 实际编码信息。</summary>
public sealed class CsvParseResult
{
    public string EncodingName { get; init; } = "unknown";
    public bool HasBom { get; init; }
    public List<string> Header { get; init; } = new();
    public List<BarRecord> Rows { get; init; } = new();
    public List<string> Anomalies { get; init; } = new();
}

/// <summary>
/// 归档 CSV 解析器。
/// 实测语料：21 个 UTF-8-with-BOM + 1 个 GBK（2025_红龙组24h/data.csv），
/// 表头全部是 `棒数,时间,作者,地址,类型,地址`（含两个"地址"，属历史遗留）。
/// </summary>
public static class CsvParser
{
    /// <summary>规范表头（第二个"地址"语义上应为作品链接，但为兼容 csv_md.py 与 Excel 习惯不改动）。</summary>
    public static readonly string[] CanonicalHeader = { "棒数", "时间", "作者", "地址", "类型", "地址" };

    public static CsvParseResult ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encName, hasBom) = Decode(bytes);
        var result = ParseText(text);
        return new CsvParseResult
        {
            EncodingName = encName,
            HasBom = hasBom,
            Header = result.Header,
            Rows = result.Rows,
            Anomalies = result.Anomalies,
        };
    }

    /// <summary>编码嗅探：BOM → 严格 UTF-8 → GB18030 兜底。</summary>
    public static (string Text, string EncodingName, bool HasBom) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 (BOM)", true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE (BOM)", true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE (BOM)", true);

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "UTF-8 (no BOM)", false);
        }
        catch (DecoderFallbackException)
        {
            // 非 UTF-8：按中文 Windows 语料最可能的 GB18030 还原
            RegisterCodePages();
            try
            {
                var gb = Encoding.GetEncoding("GB18030");
                return (gb.GetString(bytes), "GB18030/GBK", false);
            }
            catch
            {
                return (Encoding.UTF8.GetString(bytes), "UTF-8 (lossy)", false);
            }
        }
    }

    private static bool _cpRegistered;

    private static void RegisterCodePages()
    {
        if (_cpRegistered) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _cpRegistered = true;
        }
        catch { /* 某些运行时不支持，则退化为 UTF-8 lossy */ }
    }

    public static CsvParseResult ParseText(string text)
    {
        var anomalies = new List<string>();
        var rows = new List<BarRecord>();
        var header = new List<string>();

        var lines = SplitRecords(text);
        if (lines.Count == 0)
        {
            anomalies.Add("空文件");
            return new CsvParseResult { Header = header, Rows = rows, Anomalies = anomalies };
        }

        header.AddRange(lines[0].Fields);
        if (header.Count != 6)
            anomalies.Add($"表头列数异常：{header.Count} 列（期望 6 列）");
        if (header.Count >= 6 && header[3] == header[5])
            anomalies.Add($"表头第 4/6 列同名（均为「{header[3]}」）——历史遗留，第 6 列语义上是作品链接");

        int lineNo = 1;
        foreach (var rec in lines.Skip(1))
        {
            lineNo++;
            var f = rec.Fields;
            if (f.Count == 0 || f.All(string.IsNullOrWhiteSpace)) continue;
            if (f.Count < 6)
            {
                anomalies.Add($"第 {lineNo} 行只有 {f.Count} 列，已跳过");
                continue;
            }
            if (string.IsNullOrWhiteSpace(f[0]))
            {
                anomalies.Add($"第 {lineNo} 行棒数为空，已跳过");
                continue;
            }
            rows.Add(new BarRecord
            {
                RawLine = rec.Raw,
                LineNumber = lineNo,
                Bar = f[0].Trim(),
                Time = f[1].Trim(),
                Author = f[2].Trim(),
                AuthorLink = f[3].Trim(),
                Type = f[4].Trim(),
                WorkLink = f[5].Trim(),
            });
        }

        // 语义检查
        var numeric = rows.Where(r => r.BarNumber.HasValue).Select(r => r.BarNumber!.Value).ToList();
        var dupBars = numeric.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(x => x).ToList();
        if (dupBars.Count > 0)
            anomalies.Add($"棒号重复：{string.Join(", ", dupBars.Select(x => "第" + x + "棒"))}");

        var nonNumeric = rows.Where(r => !r.BarNumber.HasValue).Select(r => r.Bar).Distinct().ToList();
        if (nonNumeric.Count > 0)
            anomalies.Add($"非数字棒号：{string.Join(", ", nonNumeric)}");

        foreach (var r in rows)
        {
            if (r.Type == "404" || r.WorkLink.Trim() == "404")
                anomalies.Add($"第 {r.LineNumber} 行出现 404 语义（棒号 {r.Bar}）");
        }

        return new CsvParseResult { Header = header, Rows = rows, Anomalies = anomalies };
    }

    private sealed record CsvRecord(List<string> Fields, string Raw);

    /// <summary>RFC4180 解析：支持引号包裹、字段内逗号、字段内换行、双引号转义。</summary>
    private static List<CsvRecord> SplitRecords(string text)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var raw = new StringBuilder();
        bool inQuotes = false;
        bool started = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            started = false;
        }

        void EndRecord()
        {
            EndField();
            var rawText = raw.ToString().TrimEnd('\r', '\n');
            raw.Clear();
            records.Add(new CsvRecord(new List<string>(fields), rawText));
            fields.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); raw.Append("\"\""); i++; }
                    else { inQuotes = false; raw.Append(c); }
                }
                else { field.Append(c); raw.Append(c); }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    started = true;
                    raw.Append(c);
                    break;
                case ',':
                    EndField();
                    raw.Append(c);
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                case '\uFEFF':
                    break; // 行内残留 BOM 忽略
                default:
                    field.Append(c);
                    raw.Append(c);
                    started = true;
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0 || started) EndRecord();
        // 去掉末尾因文件以换行结尾而产生的空记录
        while (records.Count > 0 && records[^1].Fields.Count == 1 && records[^1].Fields[0].Length == 0)
            records.RemoveAt(records.Count - 1);
        return records;
    }
}
