#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using JieLi.Tui.Data;
using JieLi.Tui.Model;
using JieLi.Tui.Tui;

namespace JieLi.Tui;

public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* 重定向时可能不支持 */ }

        string? root = null;
        string? jumpSlug = null;
        string? collectTarget = null;
        string? parseTarget = null;
        string? rosterTarget = null;
        string? pipelineTarget = null;
        string? reviewTarget = null;
        long? selfTestReview = null;
        string? renderTarget = null;
        string? archiveTarget = null;
        bool allowUnconfirmed = false;
        bool backfill = false;
        bool applyBackfill = false;
        bool fixHeader = false;
        string? outDir = null;
        bool forcePlain = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    if (i + 1 < args.Length) root = args[++i];
                    break;
                case "--slug":
                    if (i + 1 < args.Length) jumpSlug = args[++i];
                    break;
                case "--collect":
                    if (i + 1 < args.Length) collectTarget = args[++i];
                    break;
                case "--parse":
                    if (i + 1 < args.Length) parseTarget = args[++i];
                    break;
                case "--roster":
                    if (i + 1 < args.Length) rosterTarget = args[++i];
                    break;
                case "--pipeline":
                    if (i + 1 < args.Length) pipelineTarget = args[++i];
                    break;
                case "--review":
                    if (i + 1 < args.Length) reviewTarget = args[++i];
                    break;
                case "--selftest-review":
                    if (i + 1 < args.Length) selfTestReview = ParseTopicId(args[++i]);
                    break;
                case "--render":
                    if (i + 1 < args.Length) renderTarget = args[++i];
                    break;
                case "--archive-root":
                    if (i + 1 < args.Length) archiveTarget = args[++i];
                    break;
                case "--allow-unconfirmed":
                    allowUnconfirmed = true;
                    break;
                case "--backfill":
                    backfill = true;
                    break;
                case "--apply":
                    applyBackfill = true;
                    break;
                case "--fix-header":
                    fixHeader = true;
                    break;
                case "--out":
                    if (i + 1 < args.Length) outDir = args[++i];
                    break;
                case "--plain":
                case "--dump":
                    forcePlain = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
            }
        }

        root ??= FindWorkspaceRoot();
        if (root is null || !Directory.Exists(root))
        {
            Console.Error.WriteLine("找不到工作目录。请用 --root <路径> 显式指定。");
            return 2;
        }

        if (collectTarget is not null)
            return RunCollect(root, collectTarget, outDir);

        if (parseTarget is not null)
            return RunParse(root, parseTarget, outDir);

        if (rosterTarget is not null)
            return RunRoster(root, rosterTarget, outDir);

        if (pipelineTarget is not null)
            return RunPipeline(root, pipelineTarget, outDir);

        // 人工确认视图（M4）
        if (reviewTarget is not null)
        {
            var workRootForReview = outDir ?? Path.Combine(root, "relay-tool", "work");
            long? rid = ParseTopicId(reviewTarget);
            if (rid is null) { Console.Error.WriteLine("--review 需要 topic_id 或话题链接。"); return 2; }
            return RunReview(root, workRootForReview, rid.Value, forcePlain);
        }

        if (selfTestReview is not null)
        {
            var workRootForTest = outDir ?? Path.Combine(root, "relay-tool", "work");
            return RunReviewSelfTest(workRootForTest, selfTestReview.Value);
        }

        if (renderTarget is not null)
        {
            var workRootForRender = outDir ?? Path.Combine(root, "relay-tool", "work");
            return RunRender(root, workRootForRender, renderTarget, archiveTarget, allowUnconfirmed);
        }

        if (backfill)
            return RunBackfill(root, applyBackfill, fixHeader);

        var scanner = new Scanner(root);
        var activities = scanner.ScanAll();

        var host = new ConsoleHost(forcePlain);
        var app = new TuiApp(host, activities, root);
        return app.Run(jumpSlug);
    }

    // ------------------------------------------------------------- 采集模式

    private static int RunCollect(string root, string target, string? outDir)
    {
        long? topicId = ParseTopicId(target);
        if (topicId is null)
        {
            Console.Error.WriteLine($"无法从「{target}」解析出 topic_id。请传数字 id，或形如 "
                                    + "https://www.bilibili.com/v/topic/detail?topic_id=1343535 的话题链接。");
            return 2;
        }

        var workRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        Directory.CreateDirectory(workRoot);

        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => writer.WriteLine(m);

        Log($"采集话题 topic_id={topicId}  输出目录 {workRoot}");
        using var client = new BilibiliClient();
        var collector = new TopicCollector(client);
        try
        {
            var manifest = collector.CollectAsync(topicId.Value, workRoot, Log).GetAwaiter().GetResult();
            Log("");
            Log("———— 采集清单 ————");
            Log($"  话题          : {manifest.Name}");
            Log($"  动态数(接口)  : {manifest.DynamicsReported}");
            Log($"  实际抓到      : {manifest.FetchedCards}   翻页 {manifest.Pages} 页");
            Log($"  有正文        : {manifest.EntriesWithText}");
            Log($"  无正文        : {manifest.EntriesWithoutText}");
            foreach (var w in manifest.Warnings) Log($"  ⚠ {w}");
            return manifest.Warnings.Count == 0 ? 0 : 1;
        }
        catch (CollectException ex)
        {
            Log("");
            Log("采集中止（硬失败，未产出任何派生数据）：");
            Log("  " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log("");
            Log("采集异常中止：" + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException is not null) Log("  内层：" + ex.InnerException.Message);
            return 4;
        }
    }

    // ------------------------------------------------------------- 旧归档回填（M6）

    private static int RunBackfill(string root, bool apply, bool fixHeader)
    {
        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        var reports = ArchiveBackfill.AnalyzeAll(root);
        Log($"扫描到 {reports.Count} 个归档 CSV");
        Log("");

        int encCount = 0, headerCount = 0, typeCount = 0, typeRows = 0;
        foreach (var r in reports)
        {
            if (r.Issues.Count == 0) continue;
            Log($"● {r.RelativePath}");
            foreach (var issue in r.Issues)
            {
                Log($"    [{issue.Kind}] {issue.Detail}"
                    + (issue.Affected > 1 ? $"（影响 {issue.Affected} 行）" : ""));
                switch (issue.Kind)
                {
                    case "编码": encCount++; break;
                    case "表头": headerCount++; break;
                    case "类型": typeCount++; typeRows += issue.Affected; break;
                }
            }
        }

        Log("");
        Log($"汇总：编码 {encCount} 个文件 / 重复表头 {headerCount} 个文件 / 类型归一化 {typeCount} 个文件（{typeRows} 行）");

        if (!apply)
        {
            Log("");
            Log("这是**干跑**（未修改任何文件）。要应用请加 --apply。");
            Log($"  --apply                统一编码为 UTF-8+BOM、类型归一化（每文件先存 .bak）");
            Log($"  --apply --fix-header   同时把重复的第 6 列「地址」改名为「作品链接」");
            Log("    注意：改名后 legacy/csv_md.py 的列名预期与实际表头不再一致（它按列序读取，功能不受影响）。");
            return 0;
        }

        int fixedCount = 0;
        foreach (var r in reports.Where(x => x.Issues.Count > 0))
        {
            try
            {
                ArchiveBackfill.Apply(r, fixHeader);
                if (r.Fixed)
                {
                    fixedCount++;
                    Log($"✔ 已修复 {r.RelativePath}   备份 {Path.GetFileName(r.BackupPath)}");
                }
            }
            catch (Exception ex)
            {
                Log($"✘ {r.RelativePath} 修复失败：{ex.Message}");
            }
        }
        Log("");
        Log($"已修复 {fixedCount} 个文件。");
        return 0;
    }

    // ------------------------------------------------------------- 渲染模式（M5）

    private static int RunRender(string root, string workRoot, string target, string? archiveRoot, bool allowUnconfirmed)
    {
        long? topicId = ParseTopicId(target);
        if (topicId is null) { Console.Error.WriteLine($"无法从「{target}」解析出 topic_id。"); return 2; }

        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        var manifest = TopicCollector.LoadManifest(workRoot, topicId.Value);
        var metaPath = ArchiveMeta.PathFor(workRoot, topicId.Value);

        // meta.yml 不存在时生成草稿（宣传信息是人工拍板项，草稿只做提示，绝不填机器猜测值）
        if (!File.Exists(metaPath))
        {
            var dir = Path.GetDirectoryName(metaPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(metaPath,
                ArchiveMeta.DraftYaml(manifest?.Name ?? topicId.Value.ToString(), topicId.Value,
                                      manifest?.TopicUrl ?? "", null),
                new UTF8Encoding(false));
            Log($"未找到 meta.yml，已生成草稿：{metaPath}");
            Log("请先填写主催与宣传链接，再重新运行 --render。");
            return 1;
        }

        var meta = ArchiveMeta.Load(metaPath);
        var items = ReviewStore.Load(workRoot, topicId.Value);
        if (items.Count == 0)
        {
            Log($"没有找到 review.jsonl（先跑 --pipeline {topicId}）。");
            return 2;
        }

        var archiveDir = archiveRoot ?? root;
        try
        {
            var r = ArchiveRenderer.Render(workRoot, topicId.Value, meta, items, archiveDir, allowUnconfirmed);
            Log($"话题「{meta.Title ?? manifest?.Name}」渲染完成");
            Log($"  输出目录   {r.OutputDirectory}");
            Log($"  整点棒     {r.BarCount - r.ExCount} 根");
            Log($"  ex 棒      {r.ExCount} 根");
            Log($"  棒数异常   {r.UnnumberedCount} 条（按时间排序）");
            Log($"  CSV        {r.CsvPath}");
            Log($"  Markdown   {r.MdPath}");
            Log($"  只读报告   {r.ReportPath}");
            foreach (var warn in r.Warnings) Log($"  ⚠ {warn}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Log("渲染被拒绝（人工确认闸门）：");
            Log("  " + ex.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------- 人工确认（M4）

    private static int RunReview(string root, string workRoot, long topicId, bool forcePlain)
    {
        var manifest = TopicCollector.LoadManifest(workRoot, topicId);
        var host = new ConsoleHost(forcePlain);
        var view = new ReviewView(host, workRoot, topicId, manifest?.Name ?? topicId.ToString());

        if (!host.Interactive)
        {
            // 非交互：渲染一帧就退出，便于自动化验收
            host.BeginFrame();
            view.Render(ConsoleHost.WindowWidth, ConsoleHost.WindowHeight);
            host.EndFrame();
            return 0;
        }

        while (!view.ShouldExit)
        {
            host.BeginFrame();
            view.Render(ConsoleHost.WindowWidth, ConsoleHost.WindowHeight);
            host.EndFrame();
            view.HandleKey(Console.ReadKey(intercept: true));
        }
        host.Restore();
        return 0;
    }

    /// <summary>
    /// M4 状态机自检：用**合成按键**驱动 ReviewView（沙箱无交互 TTY），
    /// 验证 渲染 → 确认/忽略/改棒号 → 保存 → 重跑断言 的闭环。
    /// </summary>
    private static int RunReviewSelfTest(string workRoot, long topicId)
    {
        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        var manifest = TopicCollector.LoadManifest(workRoot, topicId);
        var host = new ConsoleHost(forcePlain: true);
        var view = new ReviewView(host, workRoot, topicId, manifest?.Name ?? topicId.ToString());

        Log($"自检话题 {topicId}（{manifest?.Name}）");
        host.BeginFrame(); view.Render(160, 44); host.EndFrame();
        Log("① 初始渲染完成（上面应显示待确认统计与断言回执）");
        Log($"   ShouldExit={view.ShouldExit}");

        // 合成按键：确认 3 条、忽略 1 条、改 1 条棒号、切换过滤、保存
        var script = new (ConsoleKey, string)[]
        {
            (ConsoleKey.Enter,  "确认当前条"),
            (ConsoleKey.DownArrow, "下移"),
            (ConsoleKey.Enter,  "确认当前条"),
            (ConsoleKey.DownArrow, "下移"),
            (ConsoleKey.Enter,  "确认当前条"),
            (ConsoleKey.DownArrow, "下移"),
            (ConsoleKey.X,      "忽略当前条"),
            (ConsoleKey.I,      "切换为显示全部"),
            (ConsoleKey.DownArrow, "下移"),
            (ConsoleKey.S,      "保存"),
        };
        foreach (var (k, desc) in script)
        {
            view.HandleKey(new ConsoleKeyInfo('\0', k, false, false, false));
            Log($"② 按键 {k,-10} ({desc}) → ShouldExit={view.ShouldExit}");
        }
        Log("");
        host.BeginFrame(); view.Render(160, 44); host.EndFrame();
        Log("③ 操作后重新渲染完成（断言回执应随确认同步变化）");

        var items = ReviewStore.Load(workRoot, topicId);
        Log("");
        Log("④ 落盘核对");
        Log($"   条目总数   {items.Count}");
        Log($"   已确认     {items.Count(i => i.Confirmed)}");
        Log($"   已忽略     {items.Count(i => i.Ignored)}");
        Log($"   仍待确认   {items.Count(i => !i.Confirmed && !i.Ignored)}");
        bool persisted = items.Any(i => i.Confirmed) && items.Any(i => i.Ignored);
        Log(persisted ? "   ✓ 确认/忽略状态已持久化到 review.jsonl" : "   ✗ 状态未持久化");
        return persisted ? 0 : 1;
    }

    // ------------------------------------------------------------- 流水线模式

    /// <summary>
    /// 一条命令跑完 采集 → 解析 → 写 review.jsonl，供 TUI 人工确认。
    /// 这是 M4「半自动辅助器」的入口：非交互入口只产出草稿与待确认清单，
    /// 绝不产出最终 CSV/md（最终写出必须经过 TUI 确认，符合设计阶段定下的防线）。
    /// </summary>
    private static int RunPipeline(string root, string target, string? outDir)
    {
        long? topicId = ParseTopicId(target);
        if (topicId is null) { Console.Error.WriteLine($"无法从「{target}」解析出 topic_id。"); return 2; }

        var workRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => writer.WriteLine(m);

        // 1) 采集
        Log("【1/3】采集话题");
        using var client = new BilibiliClient();
        var collector = new TopicCollector(client);
        CollectManifest manifest;
        try
        {
            manifest = collector.CollectAsync(topicId.Value, workRoot,
                m => Log("  " + m), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (CollectException ex)
        {
            Log("  采集中止（硬失败）：" + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log("  采集异常中止：" + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException is not null) Log("  内层：" + ex.InnerException.Message);
            return 4;
        }

        // 2) 解析与交叉校验
        Log("");
        Log("【2/3】解析与交叉校验");
        var cards = TopicCollector.LoadCards(workRoot, topicId.Value);
        var result = ArtifactValidator.Validate(cards, manifest.DynamicsReported, manifest.Name);
        Log($"  话题标签 {SummaryExtractor.InferTopicTag(cards, manifest.Name) ?? "（未识别）"}");
        Log($"  总结名册 {(result.RosterCardId is null ? "未找到" : $"opus/{result.RosterCardId}（{result.RosterSlots.Count} 个棒位）")}");
        Log($"  有棒号/EX {result.Attributed}   无法归属 {result.Unattributable}   含冲突 {result.ConflictCount}");
        foreach (var f in result.AssertionFailures) Log("  ✗ " + f);

        // 3) 写 review.jsonl
        Log("");
        Log("【3/3】写 review.jsonl");
        TopicCollector.WriteReviewAsync(workRoot, topicId.Value, result, CancellationToken.None)
                       .GetAwaiter().GetResult();
        var rp = Path.Combine(workRoot, topicId.Value.ToString(), "review.jsonl");
        Log($"  {rp}  ({new FileInfo(rp).Length:N0} 字节)");
        Log("");
        Log("下一步：运行 jieli 进入 TUI → 选择本话题进行人工确认（待确认项修完才能产出最终归档）。");
        return 0;
    }

    private static int RunParse(string root, string target, string? outDir)
    {
        long? topicId = ParseTopicId(target);
        if (topicId is null)
        {
            Console.Error.WriteLine($"无法从「{target}」解析出 topic_id。");
            return 2;
        }

        var workRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => writer.WriteLine(m);

        List<Model.TopicCard> cards;
        CollectManifest? manifest;
        try
        {
            cards = TopicCollector.LoadCards(workRoot, topicId.Value);
            manifest = TopicCollector.LoadManifest(workRoot, topicId.Value);
        }
        catch (Exception ex)
        {
            Log("读取已采集数据失败：" + ex.Message);
            return 2;
        }

        var result = ArtifactValidator.Validate(cards, manifest?.DynamicsReported, manifest?.Name);

        Log($"解析话题 topic_id={topicId}  卡片 {cards.Count} 条（离线，来源于 cards.jsonl）");
        Log("");
        Log("———— 交叉校验 ————");
        Log($"  话题标签      : {SummaryExtractor.InferTopicTag(cards, manifest?.Name) ?? "（未识别）"}");
        Log($"  总结名册      : {(result.RosterCardId is null ? "未找到（退回逐棒解析）" : $"opus/{result.RosterCardId}，共 {result.RosterSlots.Count} 个棒位")}");
        Log($"  有棒号/EX     : {result.Attributed}");
        Log($"  无法归属      : {result.Unattributable}");
        Log($"  含冲突        : {result.ConflictCount}");
        Log($"  疑似代发      : {result.ProxyCount}");
        Log($"  最高棒号      : {(result.MaxBar > 0 ? result.MaxBar.ToString() : "-")}");

        Log("");
        if (result.AssertionFailures.Count == 0)
            Log("断言2通过：棒号在 1..总数 上无缺失、无重复");
        else
        {
            Log("断言2未通过（硬失败）：");
            foreach (var f in result.AssertionFailures) Log("  ✗ " + f);
        }

        if (result.Warnings.Count > 0)
        {
            Log("");
            Log($"at 链复核给出 {result.Warnings.Count} 条不一致（仅警告，不阻断）：");
            foreach (var w in result.Warnings.Take(12)) Log("  · " + w);
            if (result.Warnings.Count > 12) Log($"  · … 另有 {result.Warnings.Count - 12} 条");
        }

        Log("");
        Log("———— 棒表 ————");
        Log($"  {"棒",-5} {"时段",-6} {"发布时刻",-17} {"偏差",-8} {"发布者",-16} {"创作者",-16} 状态");
        foreach (var b in result.Bars.OrderBy(x => x.BarNumber ?? 999).ThenBy(x => x.PubTs))
        {
            string barLabel = b.IsEx ? (b.BarRaw ?? "EX") : (b.BarNumber?.ToString() ?? "?");
            string pub = b.PubTs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(b.PubTs).ToLocalTime().ToString("MM-dd HH:mm:ss")
                : "-";
            string dev = b.TimeDeviationMinutes is { } d ? (d >= 0 ? "+" : "") + d + "m" : "-";
            var status = new List<string>();
            if (b.IsUnattributable) status.Add("无法归属");
            if (b.Conflicts.Count > 0) status.Add("冲突");
            if (b.IsProxy) status.Add("代发");
            if (b.TimeDeviationMinutes is { } dd && Math.Abs(dd) > ArtifactValidator.DeviationWarnMinutes) status.Add("时段可疑");
            var statusText = status.Count == 0 ? "ok" : string.Join("/", status);

            Log($"  {Text.TextWidth.Pad(barLabel, 5)} {Text.TextWidth.Pad(b.DeclaredTime ?? "-", 6)} {Text.TextWidth.Pad(pub, 17)} {Text.TextWidth.Pad(dev, 8)} {Text.TextWidth.Pad(b.PosterName, 16)} {Text.TextWidth.Pad(b.ResolvedAuthor ?? "-", 16)} {statusText}");
        }

        if (Environment.GetEnvironmentVariable("JIELI_DEBUG") == "1")
        {
            Log("");
            Log("———— 自检细节（JIELI_DEBUG=1）————");
            foreach (var b in result.Bars.OrderBy(x => x.BarNumber ?? 999).ThenBy(x => x.PubTs))
            {
                Log($"  card={b.CardId} bar={b.BarNumber?.ToString() ?? "-"} raw={b.BarRaw ?? "-"} style={b.MatchedStyle ?? "-"} "
                    + $"time={b.DeclaredTime ?? "-"} pos={b.PubTs} poster=[{b.PosterName}] who=[{b.CreatorInText ?? "-"}] "
                    + $"prev=[{b.PrevName ?? "-"}] next=[{b.NextName ?? "-"}] proxy={b.IsProxy} proxyName=[{b.ProxyName ?? "-"}] "
                    + $"prevProxy=[{b.PrevOriginalAuthor ?? "-"}/{(b.PrevProxyName ?? "-")}] resolved=[{b.ResolvedAuthor ?? "-"}]");
            }
        }

        try
        {
            TopicCollector.WriteReviewAsync(workRoot, topicId.Value, result).GetAwaiter().GetResult();
            var rp = Path.Combine(workRoot, topicId.Value.ToString(), "review.jsonl");
            Log("");
            Log($"已写入 {rp}  ({new FileInfo(rp).Length:N0} 字节)");
        }
        catch (Exception ex)
        {
            Log("写入 review.jsonl 失败：" + ex.Message);
        }

        return result.AssertionFailures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- 名册模式（总结动态）

    private static int RunRoster(string root, string target, string? outDir)
    {
        long? topicId = ParseTopicId(target);
        if (topicId is null) { Console.Error.WriteLine($"无法从「{target}」解析出 topic_id。"); return 2; }

        var workRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => writer.WriteLine(m);

        List<Model.TopicCard> cards;
        try { cards = TopicCollector.LoadCards(workRoot, topicId.Value); }
        catch (Exception ex) { Log("读取已采集数据失败：" + ex.Message); return 2; }

        var (card, slots) = SummaryExtractor.FindRoster(cards);
        Log($"话题 topic_id={topicId}  卡片 {cards.Count} 条");
        if (card is null)
        {
            Log("");
            Log("未找到总结/名册动态（即 ai_guild.md 的『收集方法优先级 1』不可用）。");
            Log("→ 需要退回逐棒解析（--parse）。");
            return 1;
        }

        Log($"总结动态：opus/{card.Id}  发布者 {card.AuthorName}  ({card.PubTimeLabel})");
        Log($"抽出棒位 {slots.Count} 个（含特殊棒 {slots.Count(s => s.IsSpecial)} 个）");
        Log("");
        Log("———— 名册 ————");
        Log($"  {"时段",-8} {"修饰",-8} {"创作者",-18} 作品链接");
        foreach (var s in slots)
        {
            Log($"  {Text.TextWidth.Pad(s.Time, 8)} {Text.TextWidth.Pad(s.Qualifier, 8)} {Text.TextWidth.Pad(s.Creator, 18)} "
                + (s.WorkUrl.Length > 0 ? s.WorkUrl : "（未给出）"));
        }

        // 与逐棒解析结果对照：名册有、卡片没抓到棒号的，就是"必须人工确认"的那批
        var result = ArtifactValidator.Validate(cards, null);
        var rosterNames = slots.Select(s => s.Creator).Where(n => n.Length > 0).ToList();
        var matchedCards = result.Bars.Count(b => rosterNames.Any(n => TextNormalizer.SameName(n, b.PosterName)));
        Log("");
        Log("———— 名册 vs 卡片对照 ————");
        Log($"  名册给出创作者 {rosterNames.Count} 个");
        Log($"  能在卡片发布者里匹配上 {matchedCards} 条卡片");
        var unmatchedRoster = rosterNames.Where(n => !result.Bars.Any(b => TextNormalizer.SameName(n, b.PosterName))).ToList();
        if (unmatchedRoster.Count > 0)
            Log($"  名册里有、但话题卡片中找不到对应发布者：{string.Join("、", unmatchedRoster)}");

        var jsonPath = Path.Combine(workRoot, topicId.Value.ToString(), "roster.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        using (var w = new StreamWriter(jsonPath, false, new UTF8Encoding(false)))
        {
            foreach (var s in slots)
                w.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    time = s.Time,
                    qualifier = s.Qualifier,
                    creator = s.Creator,
                    work_url = s.WorkUrl,
                    source_card = card.Id,
                }, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        Log("");
        Log($"已写入 {jsonPath}");
        return 0;
    }

    /// <summary>从数字 id 或话题链接里取出 topic_id。</summary>
    private static long? ParseTopicId(string s)
    {
        s = s.Trim();
        if (long.TryParse(s, out var direct)) return direct;
        var m = Regex.Match(s, @"topic_id=(\d{4,})", RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var fromQuery)) return fromQuery;
        m = Regex.Match(s, @"/topic/(?:name/)?[^/]*/?(\d{4,})", RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var fromPath)) return fromPath;
        return null;
    }

    /// <summary>从 exe 位置向上找到含活动目录的工作区根（以 ai_guild.md 或 relay-tool 目录为标记）。</summary>
    private static string? FindWorkspaceRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            if (File.Exists(Path.Combine(parent, "ai_guild.md"))) return parent;
            if (Directory.Exists(Path.Combine(parent, "relay-tool")) &&
                !string.Equals(Path.GetFileName(parent), "relay-tool", StringComparison.OrdinalIgnoreCase))
                return parent;
            dir = parent;
        }
        var cwd = Directory.GetCurrentDirectory();
        return Directory.Exists(cwd) ? cwd : null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("jieli — 东方Project接力归档工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  jieli [--root <工作区路径>] [--slug <活动目录名>] [--plain]");
        Console.WriteLine("  jieli --collect <topic_id|话题链接> [--out <输出目录>]");
        Console.WriteLine("  jieli --parse <topic_id> [--out <输出目录>]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --root     工作区根目录（含各活动归档目录）。默认自动向上查找 ai_guild.md。");
        Console.WriteLine("  --slug     直接进入指定活动的棒链表视图。");
        Console.WriteLine("  --collect  采集模式：抓取话题全部卡片到 work/<topic_id>/（需要网络）。");
        Console.WriteLine("  --parse    解析模式：离线读取已采集数据，做交叉校验并产出 review.jsonl。");
        Console.WriteLine("  --out      工作输出目录，默认 <root>/relay-tool/work。");
        Console.WriteLine("  --plain    纯文本模式（不重绘、不读键盘），用于脚本化验收与管道输出。");
        Console.WriteLine();
        Console.WriteLine("按键（交互模式）:");
        Console.WriteLine("  ↑/↓ 选择    Enter 进入棒表    PgUp/PgDn 翻页    Home/End 首尾");
        Console.WriteLine("  Esc 返回    Q 退出");
    }
}
