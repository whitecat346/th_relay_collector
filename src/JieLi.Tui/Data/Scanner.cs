#nullable enable
using System.Text.RegularExpressions;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>
/// 归档目录扫描器（只读，绝不写入）。
/// 每个活动目录的约定：`&lt;目录名&gt;.csv` + `&lt;目录名&gt;.md`；
/// 实测存在偏离（2025_红龙组24h 用 data.csv、且缺 .md），因此这里做宽松匹配并记录异常。
/// </summary>
public sealed class Scanner
{
    private readonly string _root;

    public Scanner(string root) => _root = root;

    public List<Activity> ScanAll()
    {
        var list = new List<Activity>();
        if (!Directory.Exists(_root)) return list;

        foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            // 跳过工具目录自身与隐藏目录
            if (name.StartsWith('.')) continue;
            if (string.Equals(name, "relay-tool", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsActivityFolder(dir)) continue;
            list.Add(ScanOne(dir));
        }
        return list;
    }

    /// <summary>活动目录判定：含至少一个 .csv 文件（或 .md），且不含 .csproj 之类的工程文件。</summary>
    private static bool IsActivityFolder(string dir)
    {
        if (File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"))) return false;
        return Directory.EnumerateFiles(dir, "*.csv").Any()
            || Directory.EnumerateFiles(dir, "*.md").Any();
    }

    public Activity ScanOne(string dir)
    {
        var dirName = Path.GetFileName(dir);
        var act = new Activity { DirectoryPath = dir, DirectoryName = dirName };

        // --- 定位 CSV ---
        var csvs = Directory.EnumerateFiles(dir, "*.csv").OrderBy(f => f, StringComparer.Ordinal).ToList();
        string? csv = csvs.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), dirName, StringComparison.Ordinal))
                      ?? csvs.FirstOrDefault();
        if (csv is null)
        {
            act.Anomalies.Add("缺少 .csv 文件");
        }
        else
        {
            act.CsvPath = csv;
            if (csvs.Count > 1) act.Anomalies.Add($"目录内有 {csvs.Count} 个 csv 文件，已选用 {Path.GetFileName(csv)}");
            if (!string.Equals(Path.GetFileNameWithoutExtension(csv), dirName, StringComparison.Ordinal))
                act.Anomalies.Add($"csv 文件名与目录名不一致（{Path.GetFileName(csv)}）");

            try
            {
                var parsed = CsvParser.ParseFile(csv);
                act.SourceEncoding = parsed.EncodingName;
                act.HasBom = parsed.HasBom;
                act.Header.AddRange(parsed.Header);
                act.Bars.AddRange(parsed.Rows);
                act.Anomalies.AddRange(parsed.Anomalies);
                if (!parsed.HasBom)
                    act.Anomalies.Add($"编码为 {parsed.EncodingName}（非 UTF-8+BOM 规范）");
            }
            catch (Exception ex)
            {
                act.Anomalies.Add("CSV 解析失败：" + ex.Message);
            }
        }

        // --- 定位 md ---
        var mds = Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal).ToList();
        string? md = mds.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), dirName, StringComparison.Ordinal))
                     ?? mds.FirstOrDefault();
        if (md is null)
        {
            act.Anomalies.Add("缺少 .md 文件");
        }
        else
        {
            act.MdPath = md;
            try
            {
                var (text, encName, hasBom) = CsvParser.Decode(File.ReadAllBytes(md));
                act.Promotions.AddRange(ParsePromotions(text));
                // 段数一致性：md 的 `### 第N棒` 计数 vs csv 行数
                var mdBars = Regex.Matches(text, @"^###\s", RegexOptions.Multiline).Count;
                if (act.Bars.Count > 0 && mdBars != act.Bars.Count)
                    act.Anomalies.Add($"md 段数 {mdBars} 与 csv 行数 {act.Bars.Count} 不一致");
                if (!hasBom && encName.Contains("GB", StringComparison.OrdinalIgnoreCase))
                    act.Anomalies.Add($"md 编码为 {encName}");
            }
            catch (Exception ex)
            {
                act.Anomalies.Add("md 解析失败：" + ex.Message);
            }
        }

        if (!act.IsCompliantName)
            act.Anomalies.Add($"目录名不符合规范 {{年份}}_{{活动名}}_{{月-日}}");

        // 缺棒检测（仅在数字棒上；EX 棒不计入区间）
        var nums = act.Bars.Where(b => b.BarNumber.HasValue).Select(b => b.BarNumber!.Value).ToList();
        if (nums.Count > 0)
        {
            var max = nums.Max();
            var missing = Enumerable.Range(1, max).Where(i => !nums.Contains(i)).ToList();
            if (missing.Count > 0)
                act.Anomalies.Add($"缺棒号：{string.Join(", ", missing.Select(x => "第" + x + "棒"))}");
        }

        // 宣传项缺失
        var emptyPromo = act.Promotions.Count(p => p.IsEmpty);
        if (act.MdPath is not null && emptyPromo > 0)
            act.Anomalies.Add($"宣传项 {emptyPromo} 处为空链接");

        return act;
    }

    private static readonly string[] PromoLabels =
        { "主催", "一宣", "二宣", "三宣", "终宣", "总结", "宣", "宣传", "往期", "往期回顾" };

    /// <summary>解析 md 的 `## 宣传` 段：把每个标签行拆成 (标签, 文本, URL)。</summary>
    public static List<PromoItem> ParsePromotions(string markdown)
    {
        var items = new List<PromoItem>();
        var inSection = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                var heading = line.TrimStart('#').Trim();
                inSection = heading == "宣传";
                continue;
            }
            if (!inSection || line.Length == 0) continue;

            foreach (var label in PromoLabels)
            {
                if (!line.StartsWith(label, StringComparison.Ordinal)) continue;

                var link = Regex.Match(line, @"\[(?<t>[^\]]*)\]\((?<u>[^)]*)\)");
                if (link.Success)
                {
                    items.Add(new PromoItem
                    {
                        Label = label,
                        Text = link.Groups["t"].Value.Trim(),
                        Url = link.Groups["u"].Value.Trim(),
                    });
                }
                else
                {
                    var rest = line[label.Length..].TrimStart('：', ':', ' ', '　');
                    items.Add(new PromoItem { Label = label, Text = rest, Url = string.Empty });
                }
                break;
            }
        }
        return items;
    }
}
