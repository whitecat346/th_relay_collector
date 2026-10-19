#nullable enable
using System.Text;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>渲染结果。</summary>
public sealed class RenderResult
{
    public string OutputDirectory { get; init; } = string.Empty;
    public string CsvPath { get; init; } = string.Empty;
    public string MdPath { get; init; } = string.Empty;
    public string ReportPath { get; init; } = string.Empty;
    public int BarCount { get; init; }
    public int ExCount { get; init; }
    public int UnnumberedCount { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>一个输出行（CSV 的一行 / md 的一节）。</summary>
public sealed class ArchiveRow
{
    public string BarLabel { get; set; } = string.Empty;      // "1" / "EX1" / "N/A"
    public int? BarNumber { get; set; }
    public bool IsEx { get; set; }
    public string Time { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string AuthorLink { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string WorkLink { get; set; } = string.Empty;
    /// <summary>同一棒被多条动态认领时，被合并掉的次要作品链接（进报告，不进 CSV）。</summary>
    public List<string> SecondaryLinks { get; } = new();
    public List<string> Sources { get; } = new();
}

/// <summary>
/// M5 渲染器：把人工确认后的 review.jsonl 渲染成归档产物。
///
/// 严格复刻 legacy/csv_md.py 的输出契约（这是用户既有 Excel 流程与阅读习惯的基准）：
///   CSV 列：棒数,时间,作者,地址,类型,地址     （第 4 列=作者空间链接，第 6 列=作品链接）
///            UTF-8 with BOM（Windows 中文区 Excel 直接双击不乱码）
///   MD 三段：# 宣传（主催 + 各宣）→ # 整点接力 → # ex棒
///            每节三行：### 第N棒 时段 / 创作者 [@名](链接) / 类型 [查看](链接)，以 --- 分隔
///
/// 两道闸门（设计阶段确定）：
///   ① 未确认的条目默认不产出最终归档（可用 allowUnconfirmed 显式跳过）；
///   ② 宣传信息只来自 meta.yml —— 机器抓取结果绝不直接进产物。
/// </summary>
public static class ArchiveRenderer
{
    /// <summary>同一棒多条动态时的作品形态优先级（设计阶段确定：opus &gt; 视频 &gt; read）。</summary>
    private static int LinkRank(string url)
    {
        if (url.Contains("/opus/", StringComparison.OrdinalIgnoreCase)) return 0;
        if (url.Contains("/video/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (url.Contains("/read/", StringComparison.OrdinalIgnoreCase)) return 2;
        if (url.Contains("t.bilibili.com", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    public static RenderResult Render(
        string workRoot,
        long topicId,
        ArchiveMeta meta,
        IReadOnlyList<ReviewItem> items,
        string archiveRoot,
        bool allowUnconfirmed,
        bool writeFiles = true,
        string? workSlug = null,
        string? reportFileName = null)
    {
        // workSlug 非空 = 链遍历模式（工作目录是 work/<slug>，没有 topic_id）；
        // reportFileName 可换名，避免覆盖链遍历自己写的 report.md（那里是缺口清单）。
        var workKey = string.IsNullOrWhiteSpace(workSlug) ? topicId.ToString() : workSlug!;
        var warnings = new List<string>();

        // ---------- 闸门①：人工确认 ----------
        var unconfirmed = items.Where(i => !i.Confirmed && !i.Ignored).ToList();
        if (unconfirmed.Count > 0 && !allowUnconfirmed)
        {
            throw new InvalidOperationException(
                $"还有 {unconfirmed.Count} 条未人工确认，拒绝产出最终归档。"
                + "请在 TUI 中确认（--review），或显式加 --allow-unconfirmed 跳过（不建议）。");
        }
        if (unconfirmed.Count > 0)
            warnings.Add($"有 {unconfirmed.Count} 条未确认，按 --allow-unconfirmed 强制产出（产物可能不完整）");

        // ---- 宣传信息齐全性（meta.yml 是唯一真值源，机器绝不猜） ----
        var promoEmpty = string.IsNullOrWhiteSpace(meta.MainHostName) && meta.Promotions.Count == 0;
        if (promoEmpty)
            warnings.Add("meta.yml 里的「主催」「宣传」还是空的：宣传段会留空。请先填写 meta.yml，再重新运行 --render。");

        // ---------- 组装行 ----------
        var active = items.Where(i => !i.Ignored && !i.IsSummary).ToList();
        var (rows, unnumbered) = BuildRows(active, meta, warnings);

        // ---------- 输出目录 ----------
        var slug = !string.IsNullOrWhiteSpace(meta.Slug) ? meta.Slug!
                 : !string.IsNullOrWhiteSpace(meta.Title) ? Sanitize(meta.Title!)
                 : topicId.ToString();
        var outDir = Path.Combine(archiveRoot, slug);
        var baseName = Path.GetFileName(outDir);
        var csvPath = Path.Combine(outDir, baseName + ".csv");
        var mdPath = Path.Combine(outDir, baseName + ".md");
        var reportPath = Path.Combine(workRoot, workKey, reportFileName ?? "report.md");

        if (writeFiles)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(csvPath, BuildCsv(rows, unnumbered), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(mdPath, BuildMarkdown(meta, rows, unnumbered), new UTF8Encoding(false));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, BuildReport(meta, workKey, rows, unnumbered, items, warnings), new UTF8Encoding(false));
        }

        return new RenderResult
        {
            OutputDirectory = outDir,
            CsvPath = csvPath,
            MdPath = mdPath,
            ReportPath = reportPath,
            BarCount = rows.Count,
            ExCount = rows.Count(r => r.IsEx),
            UnnumberedCount = unnumbered.Count,
            Warnings = warnings,
        };
    }

    // ---------------------------------------------------------------- 行组装

    private static (List<ArchiveRow> Numbered, List<ArchiveRow> Unnumbered) BuildRows(
        IReadOnlyList<ReviewItem> items, ArchiveMeta meta, List<string> warnings)
    {
        var numbered = new List<ArchiveRow>();
        var unnumbered = new List<ArchiveRow>();

        // 棒号重复：合并为同一行（作品形态优先级 opus > 视频 > read），次要链接进报告
        foreach (var group in items.Where(i => i.Bar.HasValue).GroupBy(i => i.Bar!.Value))
        {
            var sorted = group.OrderBy(i => LinkRank(i.OpusUrl)).ToList();
            var primary = sorted[0];
            var row = new ArchiveRow
            {
                BarNumber = group.Key,
                BarLabel = group.Key.ToString(),
                Time = ResolveTime(primary, group.Key, meta),
                Author = primary.ResolvedAuthor ?? primary.PosterName,
                AuthorLink = primary.ResolvedAuthorLink ?? string.Empty,
                Type = string.Join(";", group.Select(i => KindOf(i)).Where(x => x.Length > 0).Distinct()),
                WorkLink = primary.OpusUrl,
            };
            row.Sources.Add(primary.MatchedStyle ?? "—");
            for (int i = 1; i < sorted.Count; i++)
            {
                row.SecondaryLinks.Add(sorted[i].OpusUrl);
                if (row.Type.Length == 0) row.Type = KindOf(sorted[i]);
            }
            numbered.Add(row);
        }
        numbered.Sort((a, b) => (a.BarNumber ?? 0).CompareTo(b.BarNumber ?? 0));

        // EX 棒
        var exRows = new List<ArchiveRow>();
        int exIdx = 0;
        foreach (var it in items.Where(i => i.IsEx).OrderBy(i => TimeKey(i.DeclaredTime)))
        {
            exIdx++;
            exRows.Add(new ArchiveRow
            {
                IsEx = true,
                BarLabel = string.IsNullOrEmpty(it.BarRaw) ? "EX" : it.BarRaw!,
                Time = it.DeclaredTime ?? "N/A",
                Author = it.ResolvedAuthor ?? it.PosterName,
                AuthorLink = it.ResolvedAuthorLink ?? string.Empty,
                Type = KindOf(it),
                WorkLink = it.OpusUrl,
            });
            _ = exIdx;
        }

        // 棒数异常（含"棒号未知"）：按用户要求**按时间排序**处理，棒号列写 N/A
        foreach (var it in items.Where(i => !i.Bar.HasValue && !i.IsEx).OrderBy(i => TimeKey(i.DeclaredTime)))
        {
            unnumbered.Add(new ArchiveRow
            {
                BarLabel = "N/A",
                Time = it.DeclaredTime ?? "N/A",
                Author = it.ResolvedAuthor ?? it.PosterName,
                AuthorLink = it.ResolvedAuthorLink ?? string.Empty,
                Type = KindOf(it),
                WorkLink = it.OpusUrl,
            });
        }

        var all = new List<ArchiveRow>();
        all.AddRange(numbered);
        all.AddRange(exRows);
        // 棒数异常的行由调用方单独追加（BuildCsv/BuildMarkdown 的第二个参数）。
        // 这里绝对不能再把它们塞进 rows —— 否则 CSV 会写两遍、md 里会同时出现
        // 「### 第N/A棒」和「## 棒数异常」两处（实测踩过）。
        if (unnumbered.Count > 0)
            warnings.Add($"有 {unnumbered.Count} 条棒数异常（无法确定棒号），已按时间排序附加在 CSV/MD 末尾，棒号列写 N/A");
        return (all, unnumbered);
    }

    private static string ResolveTime(ReviewItem it, int bar, ArchiveMeta meta)
    {
        if (meta.BarTimes.TryGetValue(bar, out var t) && !string.IsNullOrWhiteSpace(t)) return t;
        if (!string.IsNullOrWhiteSpace(it.DeclaredTime)) return it.DeclaredTime!;
        // 兜底：棒位时间取整点（第1棒=0:00 起，每棒 1 小时）
        return $"{bar - 1}:00";
    }

    /// <summary>取作品的展示类型：优先人工确认过的中文标签，其次按卡片类型推断。</summary>
    private static string KindOf(ReviewItem i)
    {
        if (!string.IsNullOrWhiteSpace(i.TypeLabel)) return i.TypeLabel.Trim();
        return BarParser.InferWorkKind(i.Type, i.MergedText);
    }

    private static int TimeKey(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return int.MaxValue;
        var p = hhmm.Split(':');
        if (p.Length != 2) return int.MaxValue;
        return (int.TryParse(p[0], out var h) ? h : 0) * 60 + (int.TryParse(p[1], out var m) ? m : 0);
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c);
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- CSV

    /// <summary>严格复刻 legacy/csv_md.py 的 6 列契约：棒数,时间,作者,地址,类型,地址。</summary>
    private static string BuildCsv(List<ArchiveRow> rows, List<ArchiveRow> unnumbered)
    {
        var sb = new StringBuilder();
        sb.Append("棒数,时间,作者,地址,类型,地址\n");
        foreach (var r in rows) AppendCsvRow(sb, r);
        foreach (var r in unnumbered) AppendCsvRow(sb, r);
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendCsvRow(StringBuilder sb, ArchiveRow r)
    {
        sb.Append(Quote(r.BarLabel)).Append(',')
          .Append(Quote(r.Time)).Append(',')
          .Append(Quote(r.Author)).Append(',')
          .Append(Quote(string.IsNullOrWhiteSpace(r.AuthorLink) ? "N/A" : r.AuthorLink)).Append(',')
          .Append(Quote(string.IsNullOrWhiteSpace(r.Type) ? "N/A" : r.Type)).Append(',')
          .Append(Quote(string.IsNullOrWhiteSpace(r.WorkLink) ? "N/A" : r.WorkLink)).Append('\n');
    }

    private static string Quote(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    // ---------------------------------------------------------------- Markdown

    private static string BuildMarkdown(ArchiveMeta meta, List<ArchiveRow> rows, List<ArchiveRow> unnumbered)
    {
        var sb = new StringBuilder();

        // ---- 宣传 ----
        sb.Append("## 宣传\n\n");
        sb.Append($"主催 [{meta.MainHostName}]({meta.MainHostLink})\n\n");
        foreach (var (label, url) in meta.Promotions)
            sb.Append($"{label}：[查看]({url})\n\n");

        // ---- 整点接力 ----
        sb.Append("## 整点接力\n\n");
        foreach (var r in rows.Where(r => !r.IsEx))
        {
            sb.Append($"### 第{r.BarLabel}棒 {r.Time}\n\n");
            sb.Append($"创作者 [@{r.Author}]({Nvl(r.AuthorLink)})\n\n");
            sb.Append($"{Nvl(r.Type)} [查看]({Nvl(r.WorkLink)})\n\n");
            sb.Append("---\n\n");
        }

        // ---- ex 棒 ----
        var ex = rows.Where(r => r.IsEx).ToList();
        if (ex.Count > 0)
        {
            sb.Append("## ex棒\n\n");
            foreach (var r in ex)
            {
                var title = string.Equals(r.BarLabel, "EX", StringComparison.OrdinalIgnoreCase)
                    ? $"ex棒 {r.Time}"
                    : $"{r.BarLabel} {r.Time}";
                sb.Append($"### {title}\n\n");
                sb.Append($"创作者 [@{r.Author}]({Nvl(r.AuthorLink)})\n\n");
                sb.Append($"{Nvl(r.Type)} [查看]({Nvl(r.WorkLink)})\n\n");
                sb.Append("---\n\n");
            }
        }

        // ---- 棒数异常（按时间排序） ----
        if (unnumbered.Count > 0)
        {
            sb.Append("## 棒数异常（未确定棒号）\n\n");
            sb.Append("> 以下条目经多种方法仍无法确定棒号，按时间排序。宣传/棒位信息可能不完整，请人工核对。\n\n");
            foreach (var r in unnumbered)
            {
                sb.Append($"### 时段 {r.Time}\n\n");
                sb.Append($"创作者 [@{r.Author}]({Nvl(r.AuthorLink)})\n\n");
                sb.Append($"{Nvl(r.Type)} [查看]({Nvl(r.WorkLink)})\n\n");
                sb.Append("---\n\n");
            }
        }

        return sb.ToString();
    }

    private static string Nvl(string s) => string.IsNullOrWhiteSpace(s) ? "N/A" : s;

    // ---------------------------------------------------------------- 只读报告

    private static string BuildReport(
        ArchiveMeta meta, string workKey, List<ArchiveRow> rows, List<ArchiveRow> unnumbered,
        IReadOnlyList<ReviewItem> items, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- 本文件由 relay-tool 自动生成，是只读产物；任何修改都会被下次生成覆盖。 -->\n");
        sb.Append("<!-- 人工确认只能在 TUI 里做（jieli --review ").Append(workKey).Append("）。 -->\n\n");
        sb.Append($"# 审阅报告 · {meta.Title ?? workKey}\n\n");
        sb.Append($"- 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append($"- 工作目录：{workKey}\n");
        sb.Append($"- 条目总数：{items.Count}（已确认 {items.Count(i => i.Confirmed)}／已忽略 {items.Count(i => i.Ignored)}）\n");
        sb.Append($"- 输出棒位：{rows.Count(r => !r.IsEx)} 根整点棒 + {rows.Count(r => r.IsEx)} 根 ex 棒\n");
        sb.Append($"- 棒数异常：{unnumbered.Count} 条（已按时间排序附在 md 末尾）\n\n");

        if (warnings.Count > 0)
        {
            sb.Append("## 警告\n\n");
            foreach (var w in warnings) sb.Append($"- ⚠ {w}\n");
            sb.Append('\n');
        }

        var merged = rows.Where(r => r.SecondaryLinks.Count > 0).ToList();
        if (merged.Count > 0)
        {
            sb.Append("## 同一棒多条动态（已按 opus &gt; 视频 &gt; read 取主作品）\n\n");
            sb.Append("| 棒 | 采用 | 次要链接（未进 CSV） |\n|---|---|---|\n");
            foreach (var r in merged)
                sb.Append($"| 第{r.BarLabel}棒 | {r.WorkLink} | {string.Join("<br>", r.SecondaryLinks)} |\n");
            sb.Append('\n');
        }

        var suspicious = items.Where(i => !i.Ignored && i.DeviationMinutes is { } d
                                          && Math.Abs(d) > ArtifactValidator.DeviationWarnMinutes).ToList();
        if (suspicious.Count > 0)
        {
            sb.Append("## 发布时刻与声明时段偏差较大\n\n");
            sb.Append("| 条目标识 | 声明时段 | 偏差(分钟) | 说明 |\n|---|---|---|---|\n");
            foreach (var i in suspicious)
                sb.Append($"| {i.DeclaredTime ?? "-"} / {i.ResolvedAuthor ?? i.PosterName} | {i.DeclaredTime ?? "-"} | "
                          + $"{i.DeviationMinutes} | 可能是发布延迟，也可能是标注错误 |\n");
            sb.Append('\n');
        }

        var ignored = items.Where(i => i.Ignored).ToList();
        if (ignored.Count > 0)
        {
            sb.Append("## 被人工忽略的条目\n\n");
            foreach (var i in ignored)
                sb.Append($"- {i.OpusUrl}（{i.ResolvedAuthor ?? i.PosterName}，{i.MatchedStyle ?? "—"}）\n");
            sb.Append('\n');
        }

        sb.Append("## 数据来源\n\n");
        sb.Append("| 棒 | 时段 | 创作者 | 命中写法 |\n|---|---|---|---|\n");
        foreach (var r in rows)
            sb.Append($"| {r.BarLabel} | {r.Time} | {r.Author} | {string.Join("/", r.Sources)} |\n");
        sb.Append('\n');

        return sb.ToString();
    }
}
