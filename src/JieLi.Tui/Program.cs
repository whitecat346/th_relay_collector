#nullable enable
using JieLi.Tui.Data;
using JieLi.Tui.Model;
using JieLi.Tui.Tui;
using JieLi.Tui.Tui.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JieLi.Tui;

public static class Program
{
    // 复查模式（遍历/补录全部完成后再按时间查一遍链条）的全局配置：
    // 由命令行 --no-recheck / --recheck-gaps / --recheck-repairs 写入，链遍历两种入口共用。
    private static bool _recheckEnabled = true;
    private static int _recheckGaps = 12;
    private static int _recheckRepairs = 24;

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
        string? opusTarget = null;
        string? outDir = null;
        bool forcePlain = false;
        string? probeTarget = null;
        string? probeJsonUrl = null;
        string? probeUser = null;
        long? probeSpaceUid = null;
        string? relayArg = null;

        // ---- 接力链遍历（本轮主线） ----
        string? linkTarget = null;
        var addLinks = new List<(string Url, int? Bar)>();
        string? dateArg = null;
        int hoursArg = 0;
        string? asArg = null;
        bool aiFlag = false;
        bool aiYes = false;
        int aiBudget = 20;
        bool selfTestChain = false;
        Chain.PostKind? forcedKind = null;
        // 话题采集默认逐帖补全详情（话题卡面只有首行摘要）；--no-details 可关掉（离线/求快时用）
        bool fetchDetails = true;
        // 补录：--add-link 要落到哪个已有接力上（work/<slug> 或话题 id）
        string? intoArg = null;
        // 按届过滤（同一个话题被年度接力反复复用）：--year 选年份，--all-years 关闭过滤
        int? yearArg = null;
        bool allYears = false;

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
                case "--no-details":
                    fetchDetails = false;
                    break;
                case "--no-recheck":
                    _recheckEnabled = false;
                    break;
                case "--recheck":
                    _recheckEnabled = true;
                    break;
                case "--recheck-gaps":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var rg) && rg > 0) _recheckGaps = rg;
                    break;
                case "--recheck-repairs":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var rr) && rr > 0) _recheckRepairs = rr;
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
                case "--opus":
                    if (i + 1 < args.Length) opusTarget = args[++i];
                    break;
                case "--out":
                    if (i + 1 < args.Length) outDir = args[++i];
                    break;
                case "--plain":
                case "--dump":
                    forcePlain = true;
                    break;
                // 渲染验收开关：固定视口 / 关色 / 关备用屏（旧 conhost 或调试器内用）
                case "--no-color":
                    Theme.ForceNoColor = true;
                    break;
                case "--no-alt-screen":
                    ConsoleHost.AltScreenEnabled = false;
                    break;
                // 字体缺字 / 把 ambiguous 记成 2 列的终端：★ ⚠ → ← ↔ · … ─ 全降级成 ASCII
                case "--ascii":
                    ConsoleHost.AsciiOnly = true;
                    break;
                case "--width":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var forcedW)) ConsoleHost.ForcedWidth = forcedW;
                    break;
                case "--height":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var forcedH)) ConsoleHost.ForcedHeight = forcedH;
                    break;
                // 调试用：把一条帖子的读取结果原样 dump 成 JSON（链路遍历接入前的排错入口）
                case "--probe":
                    if (i + 1 < args.Length) probeTarget = args[++i];
                    break;
                case "--probe-json":
                    if (i + 1 < args.Length) probeJsonUrl = args[++i];
                    break;
                case "--probe-user":
                    if (i + 1 < args.Length) probeUser = args[++i];
                    break;
                case "--probe-space":
                    if (i + 1 < args.Length && long.TryParse(args[++i], out var probeUid)) probeSpaceUid = probeUid;
                    break;
                case "--relay":
                    if (i + 1 < args.Length) relayArg = args[++i];
                    break;
                // ---- 接力链遍历 ----
                case "--link":
                    if (i + 1 < args.Length) linkTarget = args[++i];
                    break;
                // 显式指定入口类型（嗅探认错时的后路）：--dyn / --video / --topic
                case "--dyn":
                    if (i + 1 < args.Length) { linkTarget = args[++i]; forcedKind = Chain.PostKind.Opus; }
                    break;
                case "--video":
                    if (i + 1 < args.Length) { linkTarget = args[++i]; forcedKind = Chain.PostKind.Video; }
                    break;
                case "--topic":
                    if (i + 1 < args.Length) { linkTarget = args[++i]; forcedKind = Chain.PostKind.Topic; }
                    break;
                case "--add-link":
                    if (i + 1 < args.Length) addLinks.Add((args[++i], null));
                    break;
                case "--into":
                    if (i + 1 < args.Length) intoArg = args[++i];
                    break;
                case "--year":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var yearValue)) yearArg = yearValue;
                    break;
                case "--all-years":
                    allYears = true;
                    break;
                case "--at-bar":
                    if (i + 1 < args.Length && addLinks.Count > 0 && int.TryParse(args[++i], out var forcedBar))
                    {
                        var last = addLinks[^1];
                        addLinks[^1] = (last.Url, forcedBar);
                    }
                    break;
                case "--date":
                    if (i + 1 < args.Length) dateArg = args[++i];
                    break;
                case "--hours":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out hoursArg);
                    break;
                case "--as":
                    if (i + 1 < args.Length) asArg = args[++i];
                    break;
                case "--ai":
                    aiFlag = true;
                    break;
                case "--ai-yes":
                    aiYes = true;
                    break;
                case "--ai-budget":
                    if (i + 1 < args.Length) int.TryParse(args[++i], out aiBudget);
                    break;
                case "--selftest-chain":
                    selfTestChain = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
            }
        }

        // 调试入口不需要工作目录（也就不用先找 ai_guild.md）
        if (probeTarget is not null)
            return RunProbe(probeTarget);

        if (selfTestChain)
            return RunChainSelfTest();

        root ??= FindWorkspaceRoot();
        if (root is null || !Directory.Exists(root))
        {
            Console.Error.WriteLine("找不到工作目录。请用 --root <路径> 显式指定。");
            return 2;
        }

        var probeWorkRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        if (probeJsonUrl is not null) return RunProbeJson(probeJsonUrl);
        if (probeUser is not null) return RunProbeUser(probeWorkRoot, probeUser);
        if (probeSpaceUid is not null) return RunProbeSpace(probeWorkRoot, probeSpaceUid.Value, relayArg);

        // 补录（--add-link + --into）：往**已有的**接力里补一棒，并自动把缺的部分补齐
        if (addLinks.Count > 0 && intoArg is not null)
            return RunAddLink(root, outDir, intoArg, addLinks, dateArg, hoursArg, asArg, fetchDetails);
        if (addLinks.Count > 0 && linkTarget is null)
        {
            Console.Error.WriteLine("--add-link 需要用 --into <slug|topic_id> 指定要补进哪一条接力，");
            Console.Error.WriteLine("  或者用 --link <入口帖> 现场开一条新链（补录链接会先进入链再双向展开）。");
            return 2;
        }

        // 接力链遍历（用户给一棒，双向自动补全整条链）
        if (linkTarget is not null)
            return RunLink(root, outDir, linkTarget, addLinks, dateArg, hoursArg, asArg, aiFlag, aiYes, aiBudget,
                           forcedKind, fetchDetails, yearArg, allYears);

        if (collectTarget is not null)
            return RunCollect(root, collectTarget, outDir, fetchDetails, yearArg, allYears);
        if (parseTarget is not null)
            return RunParse(root, parseTarget, outDir);

        if (rosterTarget is not null)
            return RunRoster(root, rosterTarget, outDir);

        if (pipelineTarget is not null)
            return RunPipeline(root, pipelineTarget, outDir, fetchDetails, yearArg, allYears);

        // 人工确认视图（M4）
        if (reviewTarget is not null)
        {
            var workRootForReview = outDir ?? Path.Combine(root, "relay-tool", "work");

            // 两种输入模式：话题模式用 topic_id（数字），单链接模式用 opus_<id> 目录名
            long? rid = ParseTopicId(reviewTarget);
            if (rid is not null && ReviewStore.Exists(workRootForReview, rid.Value))
                return RunReview(root, workRootForReview, rid.Value, forcePlain);

            var slug = rid is not null ? "opus_" + rid.Value : reviewTarget.Trim();
            if (ReviewStore.Exists(workRootForReview, slug))
                return RunReview(root, workRootForReview, rid ?? 0, forcePlain, slug);

            Console.Error.WriteLine($"找不到已采集的数据：既没有 work/{reviewTarget}/review.jsonl，"
                                    + $"也没有 work/opus_{reviewTarget}/review.jsonl。");
            Console.Error.WriteLine("  话题模式先跑 --pipeline <topic_id>；单链接模式先跑 --opus <opus链接>。");
            return 2;
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

        // 单链接模式（接力没有建 topic 的情形）
        if (opusTarget is not null)
        {
            var workRootForOpus = outDir ?? Path.Combine(root, "relay-tool", "work");
            return RunOpus(workRootForOpus, opusTarget);
        }

        var scanner = new Scanner(root);
        var activities = scanner.ScanAll();

        var host = new ConsoleHost(forcePlain);
        host.EnterAltScreen();
        var app = new TuiApp(host, activities, root);
        return app.Run(jumpSlug);
    }

    // ------------------------------------------------------------- 采集模式

    private static int RunCollect(string root, string target, string? outDir, bool fetchDetails = true,
        int? yearFilter = null, bool allYears = false)
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
            var manifest = collector.CollectAsync(topicId.Value, workRoot, Log,
                CancellationToken.None, yearFilter, null, allYears).GetAwaiter().GetResult();
            Log("");
            if (fetchDetails)
            {
                Log("———— 逐帖补全详情（话题卡片只有首行摘要，必须进详情页） ————");
                var detail = TopicDetailEnricher.Enrich(workRoot, manifest.TopicId, Log);
                TopicCollector.ApplyDetailReport(workRoot, manifest.TopicId, detail);
                manifest.DetailsFetched = detail.Fetched;
                manifest.DetailsFailed = detail.Failed;
                if (detail.Failed > 0)
                    manifest.Warnings.Add($"详情补全 {detail.Failed}/{detail.Total} 条失败（见 posts.jsonl 的 error 字段）");
                Log("");
            }
            Log("———— 采集清单 ————");
            Log($"  话题          : {manifest.Name}");
            Log($"  动态数(接口)  : {manifest.DynamicsReported}");
            Log($"  实际抓到      : {manifest.FetchedCards}   翻页 {manifest.Pages} 页");
            Log($"  有正文        : {manifest.EntriesWithText}");
            Log($"  无正文        : {manifest.EntriesWithoutText}");
            if (fetchDetails)
                Log($"  详情补全      : 成功 {manifest.DetailsFetched} / 失败 {manifest.DetailsFailed}");
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

    // ------------------------------------------------------------- 补录（--add-link --into）

    /// <summary>
    /// `--add-link &lt;URL&gt; [--at-bar N] --into &lt;slug|topic_id&gt;`：往**已有**的接力里补一棒，
    /// 然后自动把缺的部分补齐（用户要求：补录不应停在「加一条」，而要接着补全）。
    ///
    /// - `--into &lt;topic_id&gt;`：把这一帖追加进话题 work 目录（cards.jsonl + posts.jsonl），
    ///   再重新解析整场；人工棒号交给 `--at-bar`。
    /// - `--into &lt;slug&gt;`：读 chain.json，把补录帖当新落点双向续走，碰到已知节点即停，
    ///   再用话题时间表补断点。
    ///
    /// **缺口位置一律按时间判定**：`--at-bar` 只改棒号标签，不用来推断缺口在哪。
    /// </summary>
    private static int RunAddLink(
        string root,
        string? outDir,
        string into,
        List<(string Url, int? Bar)> addLinks,
        string? dateArg,
        int hoursArg,
        string? asArg,
        bool fetchDetails)
    {
        var workRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        if (addLinks.Count == 0)
        {
            Log("--into 需要配 --add-link <帖子链接>（可重复）。");
            return 2;
        }

        // 目标识别：先当话题 work 目录（work/<topic_id>/cards.jsonl），再当链 work 目录（work/<slug>/chain.json）
        long? topicId = ParseTopicId(into);
        if (topicId is not null && File.Exists(Path.Combine(workRoot, topicId.Value.ToString(), "cards.jsonl")))
            return RunAddLinkTopic(root, workRoot, topicId.Value, addLinks, Log);

        var slug = into.Trim();
        if (File.Exists(JieLi.Tui.Tui.ChainView.PathFor(workRoot, slug)))
            return RunAddLinkChain(root, workRoot, slug, addLinks, dateArg, hoursArg, asArg, Log);

        Log($"--into「{into}」没找到可补录的接力：");
        Log($"  话题模式需要 {Path.Combine(workRoot, into, "cards.jsonl")}");
        Log($"  链模式需要   {JieLi.Tui.Tui.ChainView.PathFor(workRoot, slug)}");
        Log("  （先跑 --link 或 --pipeline 生成 work 目录，再用 --into 补录）");
        return 2;
    }

    /// <summary>补录进话题 work：追加卡片 + 详情，然后按「话题原有 + 人工补录」重新解析。</summary>
    private static int RunAddLinkTopic(
        string root, string workRoot, long topicId, List<(string Url, int? Bar)> addLinks, Action<string> log)
    {
        var dir = Path.Combine(workRoot, topicId.ToString());
        log($"补录到话题 work：{dir}　（{addLinks.Count} 条）");
        log("");

        var manualBars = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = 0;
        foreach (var (url, forcedBar) in addLinks)
        {
            var reference = Chain.LinkResolver.Resolve(url, out var why);
            if (reference is null)
            {
                log($"✗ 「{url}」认不出来：{why ?? "未知原因"}");
                failures++;
                continue;
            }

            log($"补录：{url}");
            log($"  识别为 {reference.Kind} {reference.Id}"
                + (forcedBar is null ? "（未指定棒号，按时间/正文推断）" : $"（人工指定第 {forcedBar} 棒）"));
            try
            {
                var facts = TopicDetailEnricher.AppendManual(workRoot, topicId, reference, m => log("  " + m));
                if (forcedBar is not null) manualBars[facts.PostId] = forcedBar.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"  ✗ 读取失败：{ex.GetType().Name}: {ex.Message}");
                log($"  这条可以稍后重试：--add-link {url} --into {topicId}"
                    + (forcedBar is null ? "" : $" --at-bar {forcedBar}"));
                failures++;
            }
        }

        if (manualBars.Count > 0)
        {
            log("");
            log("人工棒号（以你为准，只作棒号标签；缺口位置仍按时间判定）：");
            foreach (var kv in manualBars) log($"  · {kv.Key} → 第 {kv.Value} 棒");
        }

        log("");
        log("补录完成，重新解析整场（卡片 = 话题原有 + 人工补录）：");
        log("");
        var parseExit = RunParse(root, topicId.ToString(), workRoot, manualBars);
        return failures == 0 ? parseExit : Math.Max(parseExit, 1);
    }

    /// <summary>补录进链 work：读 chain.json，从补录帖双向续走，再覆盖写回。</summary>
    private static int RunAddLinkChain(
        string root, string workRoot, string slug, List<(string Url, int? Bar)> addLinks,
        string? dateArg, int hoursArg, string? asArg, Action<string> log)
    {
        var view = JieLi.Tui.Tui.ChainView.TryLoad(workRoot, slug, out var loadError);
        if (view?.Result is null)
        {
            log("读取 chain.json 失败：" + (loadError ?? "未知原因"));
            return 2;
        }

        var existing = view.Result;
        log($"补录到链：{slug}");
        log($"  已有节点 {existing.Nodes.Count} 个，缺口 {existing.Gaps.Count} 处，活动名 {existing.RelayName ?? "未识别"}");
        log("");

        var supplements = new List<(Chain.PostRef Ref, int? ForcedBar)>();
        foreach (var (url, forcedBar) in addLinks)
        {
            var r = Chain.LinkResolver.Resolve(url, out var why);
            if (r is null)
            {
                log($"✗ 「{url}」认不出来：{why ?? "未知原因"}");
                return 2;
            }
            log($"补录：{r.Url}（{r.Kind} {r.Id}）"
                + (forcedBar is null ? "（未指定棒号）" : $"→ 第 {forcedBar} 棒（人工指定，只作棒号权威）"));
            supplements.Add((r, forcedBar));
        }
        log("");

        var startFacts = existing.Nodes.FirstOrDefault(n => n.IsStart)?.Facts;
        var dayStart = startFacts?.PubLocal.Date ?? DateTime.MinValue;
        var query = new Chain.RelayQuery
        {
            RelayName = existing.RelayName,
            TopicId = FirstTopicIdOf(existing),
            DayStart = dayStart,
            DayEnd = dayStart == DateTime.MinValue
                ? DateTime.MinValue
                : dayStart.AddHours(hoursArg > 0 ? hoursArg : 24),
            DeclaredHours = hoursArg,
        };

        using var http = new Chain.ChainHttp();
        var cache = new Chain.UserCache(Path.Combine(workRoot, "users.json"));
        cache.Load(log);
        var reader = new Chain.ChainPostReader(http);
        var locator = new Chain.ChainUserLocator(http, new Chain.ChainSearchClient(http), cache);
        var walker = new Chain.ChainWalker(http, reader, locator)
        {
            MaxNodes = 80,
            MaxConsecutiveFailures = 2,
            RecheckTimeGaps = _recheckEnabled,
            MaxRecheckGaps = _recheckGaps,
            MaxRecheckRepairs = _recheckRepairs,
        };

        Chain.ChainResult completed;
        try
        {
            completed = walker.Complete(existing, supplements, query, m => log("  " + m));
        }
        catch (Chain.ChainFetchException ex)
        {
            log("补录中止：" + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            log($"补录异常：{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null) log("  内层：" + ex.InnerException.Message);
            return 4;
        }

        log("");
        log($"补录后：节点 {completed.Nodes.Count} 个（原 {existing.Nodes.Count}），缺口 {completed.Gaps.Count} 处（原 {existing.Gaps.Count}）");
        foreach (var gap in completed.Gaps)
            log($"  ⚠ {gap.ExpectedName ?? gap.Kind}：{gap.Reason}　→ {gap.SuggestedCommand}");

        string dir;
        try
        {
            dir = Chain.ChainWriter.Write(workRoot, existing.Slug ?? slug, completed, query, dateArg, m => log("  " + m));
        }
        catch (Exception ex)
        {
            log("写回链数据失败：" + ex.Message);
            return 4;
        }

        log("");
        log($"已写回 {dir}");
        log("提示：review.jsonl 已按新链重新生成，所有条目的确认状态被重置为「未确认」。");
        log($"下一步：jieli --review {existing.Slug ?? slug}");
        if (completed.Gaps.Count > 0)
            log($"仍有 {completed.Gaps.Count} 处缺口：按上面的「可直接复制」命令再补一帖，直到缺口清零。");
        return 0;
    }

    private static long? FirstTopicIdOf(Chain.ChainResult result)
    {
        foreach (var node in result.Nodes)
            if (node.Facts?.TopicId is > 0 and var topicId) return topicId;
        return null;
    }

    // ------------------------------------------------------------- 单链接模式

    /// <summary>
    /// 从**单个接力链接**出发采集（接力没有建 topic 时用这个）。
    /// 输入可以是 `https://www.bilibili.com/opus/&lt;id&gt;` 或裸 id。
    /// </summary>
    private static int RunOpus(string workRoot, string target)
    {
        var opusId = ParseOpusId(target);
        if (opusId is null)
        {
            Console.Error.WriteLine($"无法从「{target}」解析出 opus id。"
                                    + "请传 https://www.bilibili.com/opus/<数字id> 形式的链接或裸 id。");
            return 2;
        }

        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        Log($"单链接模式：起始 opus/{opusId}");
        Log("  说明：接力没有建话题时用这个模式。会抓正文与评论，识别「上一棒/下一棒」，");
        Log("        并沿正文/评论里的 opus 链接继续遍历（只 @ 名字而无链接时无法自动跟进）。");
        Log("");

        using var client = new BilibiliClient();
        var collector = new SingleLinkCollector(client);

        SingleLinkResult result;
        try
        {
            result = collector.Collect(opusId, Log);
        }
        catch (CollectException ex)
        {
            Log("采集中止：" + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log($"采集异常：{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null) Log("  内层：" + ex.InnerException.Message);
            return 4;
        }

        if (result.Entries.Count == 0)
        {
            Log("没有抓到任何帖子（见上面的警告）。");
            return 1;
        }

        string dir;
        try
        {
            dir = collector.WriteAsync(workRoot, opusId, result).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log("落盘失败：" + ex.Message);
            return 2;
        }

        Log("");
        Log("———— 采集清单 ————");
        Log($"  抓到帖子     : {result.Entries.Count} 条（沿链接遍历）");
        Log($"  含评论       : {result.Entries.Count(e => e.CommentCount > 0)} 条帖子有评论");
        Log($"  输出目录     : {dir}");
        Log($"  review.jsonl : {Path.Combine(dir, "review.jsonl")}");
        foreach (var warn in result.Warnings) Log($"  ⚠ {warn}");
        Log("");
        Log($"下一步：jieli --review opus_{opusId}   进入 TUI 人工确认（与话题模式完全一致的界面）");
        return 0;
    }

    /// <summary>从链接或裸串里取出 opus id。</summary>
    private static string? ParseOpusId(string s)
    {
        s = s.Trim();
        if (Regex.IsMatch(s, @"^\d{6,}$")) return s;
        var m = Regex.Match(s, @"/opus/(\d{6,})", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(s, @"(?:^|[^\d])(\d{15,})(?:[^\d]|$)");
        if (m.Success) return m.Groups[1].Value;
        return null;
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
        // target 有两种：话题管线用 topic_id（纯数字），链遍历用 slug（--as 或 chain_<id>）。
        // 链遍历没有 topic_id，但它的 work/<slug>/ 目录结构、review.jsonl、meta.yml 完全同形，
        // 所以这里只是把「按 topic_id 定位工作目录」换成「按 slug 定位」，其余流程一字不改。
        long? topicId = ParseTopicId(target);
        var isSlug = topicId is null;
        var workKey = isSlug ? target.Trim() : topicId!.Value.ToString();

        if (isSlug && !IsSafeSlug(workKey))
        {
            Console.Error.WriteLine($"无法从「{target}」解析出 topic_id，也不像一个安全的工作目录名（slug）。");
            return 2;
        }

        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        var manifest = isSlug ? null : TopicCollector.LoadManifest(workRoot, topicId!.Value);
        var metaPath = isSlug
            ? Path.Combine(workRoot, workKey, "meta.yml")
            : ArchiveMeta.PathFor(workRoot, topicId!.Value);

        // meta.yml 不存在时生成草稿（宣传信息是人工拍板项，草稿只做提示，绝不填机器猜测值）
        if (!File.Exists(metaPath))
        {
            var dir = Path.GetDirectoryName(metaPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(metaPath,
                isSlug
                    ? ArchiveMeta.DraftYaml(workKey, 0, "", null)
                    : ArchiveMeta.DraftYaml(manifest?.Name ?? topicId!.Value.ToString(), topicId!.Value,
                                            manifest?.TopicUrl ?? "", null),
                new UTF8Encoding(false));
            Log($"未找到 meta.yml，已生成草稿：{metaPath}");
            Log("请先填写主催与宣传链接，再重新运行 --render。");
            return 1;
        }

        var meta = ArchiveMeta.Load(metaPath);
        var items = isSlug
            ? ReviewStore.Load(workRoot, workKey)
            : ReviewStore.Load(workRoot, topicId!.Value);
        if (items.Count == 0)
        {
            Log($"没有找到 review.jsonl（先跑 --pipeline {workKey}，或 --link 链遍历）。");
            return 2;
        }

        var archiveDir = archiveRoot ?? root;
        try
        {
            var r = ArchiveRenderer.Render(
                workRoot,
                isSlug ? 0 : topicId!.Value,
                meta, items, archiveDir, allowUnconfirmed,
                writeFiles: true,
                workSlug: isSlug ? workKey : null,
                // 链遍历的 report.md 是缺口清单，别被审阅报告覆盖
                reportFileName: isSlug ? "render-report.md" : null);
            Log($"话题「{meta.Title ?? manifest?.Name ?? workKey}」渲染完成");
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

    private static int RunReview(string root, string workRoot, long topicId, bool forcePlain, string? slug = null)
    {
        var manifest = topicId > 0 ? TopicCollector.LoadManifest(workRoot, topicId) : null;
        var title = manifest?.Name ?? slug ?? topicId.ToString();
        var host = new ConsoleHost(forcePlain);
        host.EnterAltScreen();
        var view = new ReviewView(host, workRoot, topicId, title, slug);

        if (!host.Interactive)
        {
            // 非交互：渲染一帧就退出，便于自动化验收
            host.BeginFrame();
            view.Render(ConsoleHost.WindowWidth, ConsoleHost.WindowHeight);
            host.EndFrame();
            return 0;
        }

        try
        {
            while (!view.ShouldExit)
            {
                host.BeginFrame();
                view.Render(ConsoleHost.WindowWidth, ConsoleHost.WindowHeight);
                host.EndFrame();
                view.HandleKey(Console.ReadKey(intercept: true));
            }
        }
        finally
        {
            host.Restore();   // 异常也不能把用户困在备用屏里
        }
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
    private static int RunPipeline(string root, string target, string? outDir, bool fetchDetails = true,
        int? yearFilter = null, bool allYears = false)
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
                m => Log("  " + m), CancellationToken.None, yearFilter, null, allYears).GetAwaiter().GetResult();
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

        // 1b) 逐帖补全详情：话题卡片只给首行摘要（desc.text / major.desc / 标题），
        //     正文段落、评论区、图片卡片里的文字都在各自的详情页里，必须逐帖去取。
        if (fetchDetails)
        {
            Log("");
            Log("【1b/3】逐帖补全详情（话题卡片只有首行摘要，必须进详情页）");
            var detail = TopicDetailEnricher.Enrich(workRoot, topicId.Value, m => Log("  " + m));
            TopicCollector.ApplyDetailReport(workRoot, topicId.Value, detail);
        }

        // 2) 解析与交叉校验
        Log("");
        Log("【2/3】解析与交叉校验");
        var cards = TopicCollector.LoadCards(workRoot, topicId.Value);
        var result = ArtifactValidator.Validate(cards, manifest.DynamicsReported, manifest.Name);
        Log($"  话题标签 {SummaryExtractor.InferTopicTag(cards, manifest.Name) ?? "（未识别）"}");
        Log($"  总结名册 {(result.RosterCardId is null ? "未找到" : $"opus/{result.RosterCardId}（{result.RosterSlots.Count} 个棒位）")}");
        Log($"  有棒号/EX {result.Attributed}   无法归属 {result.Unattributable}   含冲突 {result.ConflictCount}");
        if (result.SlotGridNote is { Length: > 0 } gridNote) Log("  " + gridNote);
        foreach (var f in result.AssertionFailures) Log("  ✗ " + f);

        // 3) 写 review.jsonl
        Log("");
        Log("【3/3】写 review.jsonl");
        TopicCollector.WriteReviewAsync(workRoot, topicId.Value, result, CancellationToken.None)
                       .GetAwaiter().GetResult();
        var rp = Path.Combine(workRoot, topicId.Value.ToString(), "review.jsonl");
        Log($"  {rp}  ({new FileInfo(rp).Length:N0} 字节)");
        TopicCollector.WriteIgnoredAsync(workRoot, topicId.Value, result, CancellationToken.None)
                       .GetAwaiter().GetResult();
        if (result.Ignored.Count > 0)
        {
            Log($"  {Path.Combine(workRoot, topicId.Value.ToString(), "ignored.jsonl")}  "
                + $"（{result.Ignored.Count} 条被排除的卡片：重复投稿/插曲/约稿）");
            foreach (var ig in result.Ignored.Take(8))
                Log($"    · {ig.Time} @{ig.Author}：{ig.Reason}");
            if (result.Ignored.Count > 8) Log($"    · … 另有 {result.Ignored.Count - 8} 条，见文件");
        }
        Log("");
        Log("下一步：运行 jieli 进入 TUI → 选择本话题进行人工确认（待确认项修完才能产出最终归档）。");
        return 0;
    }

    private static int RunParse(string root, string target, string? outDir,
        IReadOnlyDictionary<string, int>? manualBars = null)
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

        var result = ArtifactValidator.Validate(cards, manifest?.DynamicsReported, manifest?.Name, manualBars);

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
    /// <summary>
    /// 链遍历的工作目录名（slug）是否安全：只允许字母/数字/下划线/短横/点，
    /// 且不能是 . / .. —— 因为它会直接拼进 workRoot 下的路径。
    /// </summary>
    private static bool IsSafeSlug(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 80) return false;
        if (s is "." or "..") return false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.') continue;
            return false;
        }
        return s.Any(char.IsLetterOrDigit);
    }

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
    // ------------------------------------------------------------- 接力链遍历（本轮主线）

    /// <summary>
    /// 显式指定类型时的入口解析（`--dyn` / `--video` / `--topic`）：
    /// 嗅探认错时用它兜底，仍要求输入里能取出对应 id，不做猜测。
    /// </summary>
    private static Chain.PostRef? ResolveForcedLink(string raw, Chain.PostKind kind, out string? reason)
    {
        reason = null;
        var s = (raw ?? string.Empty).Trim().Trim('"', '\'', '<', '>', ' ');

        if (kind == Chain.PostKind.Topic)
        {
            var id = ParseTopicId(s);
            if (id is not null) return Chain.PostRef.Topic(id.Value.ToString());
            reason = $"「{raw}」里没有话题 id（要 topic_id=数字 或 纯数字 topic_id）。";
            return null;
        }

        var resolved = Chain.LinkResolver.Resolve(s, out reason);
        if (resolved is null) return null;
        if (resolved.Kind == kind) return resolved;

        // 嗅探出的类型与显式指定不符：允许「纯数字被当成 topic」这类误判按显式类型纠正
        if (kind == Chain.PostKind.Opus && resolved.Kind == Chain.PostKind.Topic)
            return Chain.PostRef.Opus(resolved.Id);
        reason = $"按 --{(kind == Chain.PostKind.Video ? "video" : "dyn")} 解析失败："
                 + $"输入更像 {resolved.Kind}（{resolved.Id}）。";
        return null;
    }

    /// <summary>
    /// --link：给一棒，双向把整条接力链补全，落盘后交人工确认。
    /// </summary>
    private static int RunLink(
        string root,
        string? outDir,
        string target,
        List<(string Url, int? Bar)> addLinks,
        string? dateArg,
        int hoursArg,
        string? asArg,
        bool aiFlag,
        bool aiYes,
        int aiBudget,
        Chain.PostKind? forcedKind = null,
        bool fetchDetails = true,
        int? yearFilter = null,
        bool allYears = false)
    {
        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        void Log(string m) => w.WriteLine(m);

        var start = forcedKind is null
            ? Chain.LinkResolver.Resolve(target, out var reason)
            : ResolveForcedLink(target, forcedKind.Value, out reason);
        if (start is null)
        {
            Log("入口无法识别：" + (reason ?? "未知原因"));
            return 2;
        }
        if (start.Kind == Chain.PostKind.Topic)
        {
            // 话题入口：话题里**已经**装着这场接力的全部帖子，不需要靠 @ 一棒棒摸过去。
            // 直接走成熟的话题流程（采集 → 解析/交叉校验 → review.jsonl），
            // 后面的 --review / --render 与话题模式完全一致。
            Log($"入口是「话题」（topic_id={start.Id}）—— 话题本身就是整场接力的全集，不需要链遍历。");
            Log("改走话题流程：采集 → 解析与交叉校验 → 写 review.jsonl。");
            if (addLinks.Count > 0)
                Log("提示：话题入口不需要 --add-link（话题卡片已经覆盖全部帖子）；要补一帖请用 --add-link URL --into <topic_id>。");
            if (dateArg is not null || hoursArg > 0)
                Log("提示：话题入口不需要 --date/--hours（活动日由卡片发布时间推断），这些参数被忽略。");
            Log("");
            return RunPipeline(root, start.Id.ToString(), outDir, fetchDetails, yearFilter, allYears);
        }

        var supplements = new List<(Chain.PostRef Ref, int? ForcedBar)>();
        foreach (var (url, forcedBar) in addLinks)
        {
            var r = Chain.LinkResolver.Resolve(url, out var why);
            if (r is null)
            {
                Log($"补充链接无法识别：{url} —— {why}");
                return 2;
            }
            supplements.Add((r, forcedBar));
        }

        var query = new Chain.RelayQuery { DeclaredHours = hoursArg > 0 ? hoursArg : 0 };
        if (dateArg is not null)
        {
            if (!TryParseDateArg(dateArg, out var day))
            {
                Log($"--date 无法解析：「{dateArg}」。支持 2026-09-06 或 9-6（只给月日时年份取入口帖的年份）。");
                return 2;
            }
            query.DayStart = day;
            query.DayEnd = day.Year > 1
                ? day.AddHours(hoursArg > 0 ? hoursArg : 24)
                : day.AddHours(hoursArg > 0 ? hoursArg : 24);
        }

        var workRoot = outDir ?? Path.Combine(root, "relay-tool", "work");
        Directory.CreateDirectory(workRoot);

        using var http = new Chain.ChainHttp();
        var cache = new Chain.UserCache(Path.Combine(workRoot, "users.json"));
        cache.Load(Log);
        var search = new Chain.ChainSearchClient(http);
        var locator = new Chain.ChainUserLocator(http, search, cache);
        var reader = new Chain.ChainPostReader(http);

        Chain.AiAssist? ai = null;
        if (aiFlag)
        {
            ai = Chain.AiAssist.Load(http, Path.Combine(root, "relay-tool", "ai.yml"), aiBudget, aiYes, Log);
            if (ai is null) Log("⚠ 开启了 --ai，但没有可用的 ai.yml 配置；本次不做 AI 判定。");
            else ai.LogPath = Path.Combine(workRoot, "ai-log.jsonl");
        }

        Log($"入口：{start.Kind} {start.Id}");
        Log($"工作目录：{workRoot}");
        if (http.UsingLoginCookie) Log("本次使用了登录态（cookie.txt）。");
        Log("");

        var walker = new Chain.ChainWalker(http, reader, locator, ai)
        {
            MaxNodes = 80,
            MaxConsecutiveFailures = 2,
            RecheckTimeGaps = _recheckEnabled,
            MaxRecheckGaps = _recheckGaps,
            MaxRecheckRepairs = _recheckRepairs,
        };
        Chain.ChainResult result;
        try
        {
            result = walker.Walk(start, query, supplements, Log);
        }
        catch (Chain.ChainFetchException ex)
        {
            Log("遍历中止：" + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Log($"遍历异常：{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null) Log("  内层：" + ex.InnerException.Message);
            return 4;
        }

        var slug = !string.IsNullOrWhiteSpace(asArg)
            ? asArg!.Trim()
            : (!string.IsNullOrWhiteSpace(result.Slug) ? result.Slug : "chain_" + start.Id);

        string dir;
        try
        {
            dir = Chain.ChainWriter.Write(workRoot, slug, result, query, dateArg, Log);
        }
        catch (Exception ex)
        {
            Log("落盘失败：" + ex.Message);
            return 2;
        }

        try { cache.Save(Log); } catch (Exception ex) { Log("⚠ 用户缓存写入失败：" + ex.Message); }

        Log("");
        Log("———— 遍历结果 ————");
        Log($"  节点数       : {result.Nodes.Count}（其中拿到事实 {result.WithFacts.Count()} 条）");
        Log($"  缺口         : {result.Gaps.Count} 处");
        Log($"  请求数       : {http.RequestCount}");
        Log($"  输出目录     : {dir}");
        foreach (var warn in result.Warnings) Log($"  ⚠ {warn}");
        Log("");
        Log($"下一步：jieli --review {slug}   人工确认后 jieli --render {slug}");
        return 0;
    }

    /// <summary>
    /// --selftest-chain：离线自检（不联网）。
    /// 覆盖链接嗅探与遍历器的纯逻辑；任何用例失败即返回 1。
    /// </summary>
    private static int RunChainSelfTest()
    {
        using var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            if (ok)
            {
                passed++;
                w.WriteLine($"  ✓ {name}");
            }
            else
            {
                failed++;
                w.WriteLine($"  ✗ {name}{(detail is null ? "" : "  —— " + detail)}");
            }
        }

        w.WriteLine("接力链遍历自检（离线）");
        w.WriteLine("");
        w.WriteLine("[1] 链接嗅探");
        foreach (var (input, kind, id) in new (string, Chain.PostKind, string)[]
        {
            ("https://www.bilibili.com/opus/1236672288886095909", Chain.PostKind.Opus, "1236672288886095909"),
            ("https://t.bilibili.com/1062664246531194885", Chain.PostKind.Opus, "1062664246531194885"),
            ("https://m.bilibili.com/dynamic/1062664246531194885", Chain.PostKind.Opus, "1062664246531194885"),
            ("https://www.bilibili.com/video/BV1abg36EEZ1/", Chain.PostKind.Video, "BV1abg36EEZ1"),
            ("https://www.bilibili.com/video/av1136672288", Chain.PostKind.Video, "av1136672288"),
            ("https://www.bilibili.com/v/topic/detail?topic_id=1343535", Chain.PostKind.Topic, "1343535"),
            ("1236672288886095909", Chain.PostKind.Opus, "1236672288886095909"),
            ("1343535", Chain.PostKind.Topic, "1343535"),
        })
        {
            var r = Chain.LinkResolver.Resolve(input, out var why);
            var ok = r is not null && r.Kind == kind && r.Id == id;
            Check(string.IsNullOrEmpty(why) ? $"识别 {Short(input)}" : $"识别 {Short(input)}：{why}", ok,
                r is null ? why : $"{r.Kind} {r.Id}");
        }

        foreach (var (input, expect) in new (string, string)[]
        {
            ("https://b23.tv/abcdefg", "短链"),
            ("https://www.bilibili.com/read/cv123456", "cv"),
            ("随便一段话", "无法识别"),
        })
        {
            var r = Chain.LinkResolver.Resolve(input, out var why);
            var ok = r is null && why is not null && why.Contains(expect, StringComparison.OrdinalIgnoreCase);
            Check($"拒绝 {Short(input)} 并说明原因", ok, r is null ? why : $"意外识别成 {r.Kind} {r.Id}");
        }

        // 遍历器用例由 Chain 模块提供（离线，假 reader/locator 驱动）。
        w.WriteLine("");
        w.WriteLine("[2] 遍历器与落盘");
        var chainCase = Chain.ChainSelfTest.Run(w.WriteLine);
        passed += chainCase.Passed;
        failed += chainCase.Failed;
        w.WriteLine("");
        w.WriteLine($"自检结果：通过 {passed}，失败 {failed}");
        w.WriteLine(failed == 0 ? "✓ 自检通过" : "✗ 自检失败");
        return failed == 0 ? 0 : 1;

        static string Short(string s) => s.Length <= 42 ? s : s[..42] + "…";
    }

    /// <summary>解析 --date：2026-09-06 / 2026/9/6 / 9-6。</summary>
    private static bool TryParseDateArg(string raw, out DateTime day)
    {
        day = default;
        var s = raw.Trim().Replace('/', '-');
        var m = Regex.Match(s, @"^(\d{4})-(\d{1,2})-(\d{1,2})$");
        if (m.Success)
        {
            day = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            return true;
        }
        m = Regex.Match(s, @"^(\d{1,2})-(\d{1,2})$");
        if (m.Success)
        {
            // 只给月日：用 Year==1 表示，年份由遍历器从入口帖补齐
            day = new DateTime(1, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            return true;
        }
        return false;
    }

    /// <summary>--probe-json：用 ChainHttp 打一个任意地址并把原文打出来（开发期排错用）。</summary>
    private static int RunProbeJson(string url)
    {
        using var http = new Chain.ChainHttp();
        try
        {
            var body = http.GetText(url, allowHtml: true);
            Console.WriteLine(body.Length > 8000 ? body[..8000] + "\n…（已截断）" : body);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe-json] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>--probe-user：验证昵称 → 用户定位。</summary>
    private static int RunProbeUser(string workRoot, string name)
    {
        using var http = new Chain.ChainHttp();
        var cache = new Chain.UserCache(Path.Combine(workRoot, "users.json"));
        cache.Load(Console.Error.WriteLine);
        var locator = new Chain.ChainUserLocator(http, new Chain.ChainSearchClient(http), cache);
        try
        {
            var user = locator.Resolve(name, null, m => Console.Error.WriteLine("[probe-user] " + m), CancellationToken.None);
            if (user is null)
            {
                Console.WriteLine($"null  // 没有定位到「{name}」");
                return 0;
            }
            Console.WriteLine($"{user.Uid}\t{user.Name}\t{user.Method}\tExact={user.Exact}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe-user] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>--probe-space：验证「用户 + 活动」→ 候选帖子。</summary>
    private static int RunProbeSpace(string workRoot, long uid, string? relayName)
    {
        using var http = new Chain.ChainHttp();
        var cache = new Chain.UserCache(Path.Combine(workRoot, "users.json"));
        cache.Load(Console.Error.WriteLine);
        var locator = new Chain.ChainUserLocator(http, new Chain.ChainSearchClient(http), cache);
        try
        {
            var user = new Chain.UserRef { Uid = uid, Name = "?", Method = Chain.FetchMethod.Manual };
            var query = new Chain.RelayQuery { RelayName = relayName };
            var found = locator.FindPosts(user, query, m => Console.Error.WriteLine("[probe-space] " + m), CancellationToken.None);
            Console.WriteLine($"候选 {found.Count} 条");
            foreach (var c in found)
                Console.WriteLine($"{c.PubTs}\t{c.Method}\t{c.Ref.Kind} {c.Ref.Id}\t{c.Title}\t{c.Why}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe-space] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// --probe：把单条帖子的读取结果原样打印成 JSON。
    /// 只用于开发期排错（链路遍历接线前的唯一观察窗口），不产出任何文件。
    /// </summary>
    private static int RunProbe(string target)
    {
        var link = Chain.LinkResolver.Resolve(target, out var reason);
        if (link is null)
        {
            Console.Error.WriteLine(reason ?? "无法解析该链接。");
            return 2;
        }

        Console.Error.WriteLine($"入口：{link.Kind} {link.Id} → {link.Url}");
        using var http = new Chain.ChainHttp();
        var reader = new Chain.ChainPostReader(http);
        try
        {
            var facts = reader.Read(link, m => Console.Error.WriteLine("[probe] " + m), CancellationToken.None);
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(facts, options));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe] 读取失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

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
        Console.WriteLine("典型流程: 收集 → 纠错 → 保存");
        Console.WriteLine("  jieli --pipeline <话题链接>     # ① 收集：抓全量动态，写 work/<id>/review.jsonl");
        Console.WriteLine("  jieli --review   <topic_id>     # ② 纠错：TUI 逐条确认/修正（唯一合法确认入口）");
        Console.WriteLine("  jieli --render   <topic_id>     # ③ 保存：出 CSV + 三段式 md + 只读报告");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  jieli                                  只读审阅已有归档（默认视图，不写任何文件）");
        Console.WriteLine("  jieli --slug <活动目录名>               直接进某活动的棒表");
        Console.WriteLine("  jieli --pipeline <topic_id|话题链接>    采集 + 解析 + 写 review.jsonl（需网络）");
        Console.WriteLine("  jieli --opus     <opus链接|opus_id>     单链接模式：接力**没建话题**时用（需网络）");
        Console.WriteLine("  jieli --collect  <topic_id|话题链接>    只采集（需网络）");
        Console.WriteLine("  jieli --parse    <topic_id>            只解析校验，打印完整棒表（离线）");
        Console.WriteLine("  --no-details                           话题采集时不逐帖取详情（快速/离线用；默认会逐帖取）");
        Console.WriteLine("  jieli --roster   <topic_id>            只抽主办总结名册（离线）");
        Console.WriteLine("  jieli --render   <topic_id>            渲染归档产物（需先填 meta.yml）");
        Console.WriteLine("  jieli --backfill [--apply]             旧归档零风险回填（默认干跑）");
        Console.WriteLine("  jieli --selftest-review <topic_id>     人工确认状态机自检（合成按键）");
        Console.WriteLine("  jieli --selftest-chain                 接力链遍历自检（离线，不联网）");
        Console.WriteLine();
        Console.WriteLine("接力链遍历（给一棒，双向把整条链补全；接力靠 @ 下一个人，不是贴链接）:");
        Console.WriteLine("  jieli --link <帖子链接>                入口：opus / t.bilibili.com / video BV·av");
        Console.WriteLine("  jieli --link <话题链接>                入口也可以是话题：接力本来就建了话题时，直接走话题流程");
        Console.WriteLine("                                         （话题里没有「第N棒」时按「时段 = 棒位」推算棒号）");
        Console.WriteLine("  jieli --add-link <帖子链接> [--at-bar N]  人工补录一棒（可多次；--at-bar 强制棒号）");
        Console.WriteLine("  jieli --add-link <帖子链接> --into <slug|topic_id> [--at-bar N]");
        Console.WriteLine("                                         往**已有的**接力里补一棒，并自动把缺的部分补齐");
        Console.WriteLine("                                         slug=链模式（从补录帖双向续走）；topic_id=话题模式（重新解析）");
        Console.WriteLine("  --at-bar <N>                           人工棒号：只作棒号权威，缺口位置一律按时间判定");
        Console.WriteLine("  --year <2025> / --all-years            话题被多届复用时按届过滤（默认只留含今年的那一届）");
        Console.WriteLine("  --date <2026-09-06|9-6>                活动日期；只给月日时年份取入口帖的年份");
        Console.WriteLine("  --hours <N>                            活动声明时长（24h/32h…），用于放宽搜索区间");
        Console.WriteLine("  --as <活动目录名>                      覆盖自动命名（默认 {年份}_{活动名}_{月-日}）");
        Console.WriteLine("  --ai / --ai-yes / --ai-budget <N>      启用 AI 兜底判定（默认关闭、逐次确认、默认预算 20）");
        Console.WriteLine("  复查模式（默认开）: 走完后按时间复查链条；相邻棒之间空出整段时自动补全 ——");
        Console.WriteLine("    先从前一棒的「下一棒」@ 逐层往后递推，前一棒没写 @ 就改从后一棒往前递推，补不到就如实记缺口。");
        Console.WriteLine("  --no-recheck                           关掉复查模式（只走 @ 链，不按时间补断裂）");
        Console.WriteLine("  --recheck-gaps <N>                     最多处理几处时间断裂（默认 12）");
        Console.WriteLine("  --recheck-repairs <N>                  复查模式单次最多补入几棒（默认 24）");
        Console.WriteLine("  产出 work/<slug>/{chain.json,review.jsonl,report.md}，再用 --review / --render 接手");
        Console.WriteLine();
        Console.WriteLine("开发期探针（不产出文件）:");
        Console.WriteLine("  --probe <帖子链接>                     读一帖 → PostFacts JSON");
        Console.WriteLine("  --probe-json <url>                     用 ChainHttp 打任意地址并打印原文");
        Console.WriteLine("  --probe-user <昵称>                    昵称 → uid");
        Console.WriteLine("  --probe-space <uid> [--relay <活动名>] 用户 + 活动 → 候选帖子");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --root <路径>          工作区根目录。默认自动向上查找 ai_guild.md。");
        Console.WriteLine("  --out <路径>           过程产物目录，默认 <root>/relay-tool/work。");
        Console.WriteLine("  --archive-root <路径>  渲染输出根目录，默认 <root>。");
        Console.WriteLine("  --allow-unconfirmed    跳过\"必须人工确认\"闸门（不建议，产物可能不完整）。");
        Console.WriteLine("  --fix-header           回填时把重复的第 6 列「地址」改名「作品链接」（不建议）。");
        Console.WriteLine("  --plain / --dump       纯文本模式（不重绘、不读键盘），用于脚本化验收。");
        Console.WriteLine("  --no-color             禁用颜色（等价 NO_COLOR）。");
        Console.WriteLine("  --no-alt-screen        不进备用屏（旧 conhost / 调试器内）。");
        Console.WriteLine("  --ascii                把 ★ ⚠ → ← ↔ · … ─ 降级成 ASCII（字体缺字时用）。");
        Console.WriteLine("  --width N --height N   固定视口尺寸（快照与验收用）。");
        Console.WriteLine("  -h / --help            显示本帮助。");
        Console.WriteLine();
        Console.WriteLine("按键（只读审阅）:");
        Console.WriteLine("  ↑/↓ 选择活动   Enter 进入棒表   PgUp/PgDn 翻页   Home/End 首尾   Esc/Q 退出");
        Console.WriteLine();
        Console.WriteLine("按键（--review 人工确认）:");
        Console.WriteLine("  ↑/↓ 选择   PgUp/PgDn 翻页   Home/End 首尾   I 过滤（全部/仅待确认）");
        Console.WriteLine("  Enter 确认   X 忽略   R 撤销");
        Console.WriteLine("  B 棒号   T 时段   U 创作者   K 类型   P 代发者   N 备注");
        Console.WriteLine("  A 粘贴链接补录一棒   G 缺口清单   C 链条视图");
        Console.WriteLine("  V 全文查看器   ? 帮助   D 浏览器打开作品链接   S 保存");
        Console.WriteLine("  Esc 返回上一级   Q 退出（有未保存改动时先拦一次）");
        Console.WriteLine();
        Console.WriteLine("按键（G/C 只读面板）:");
        Console.WriteLine("  ↑/↓ PgUp/PgDn Home/End 选节点   Enter 节点详情   Esc 返回");
        Console.WriteLine();
        Console.WriteLine("完整手册: relay-tool/docs/USER-GUIDE.md");
        Console.WriteLine("实测语料规律与反模式: relay-tool/docs/CORPUS-FINDINGS.md");
    }
}
