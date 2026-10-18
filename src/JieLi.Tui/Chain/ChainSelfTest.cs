#nullable enable
using System.Globalization;
using System.Text.Json;
using JieLi.Tui.Data;
using JieLi.Tui.Model;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」
// 只允许改本文件与 Chain/MatchVerifier.cs / Chain/ChainWalker.cs / Chain/ChainWriter.cs。
//
// 职责：遍历器 + 落盘的**离线**自检用例（`jieli --selftest-chain` 会在链接嗅探用例之后调用它）。
// 硬性要求：
//   * 不联网：用假的 IPostReader / IUserLocator 驱动 ChainWalker（接口在 ChainModels.cs，
//     ChainHttp 可以构造但不发请求）。
//   * 每条用例都必须能独立失败并打印原因，不要吞异常。
//   * 至少覆盖：首棒无上一棒、末棒标记终止、两向相遇即停、连续 2 次失败停方向、
//     一人多棒（同 uid 两个 postId 都要在链上）、补充链接优先、方向冲突先到者胜。
// ===========================================================================

/// <summary>自检结果计数。</summary>
public sealed class ChainSelfTestResult
{
    public int Passed { get; set; }
    public int Failed { get; set; }
}

/// <summary>
/// 遍历器与落盘的离线自检。
/// 为什么用假 reader/locator 而不是真联网：
///   ① 自检必须在没有网络/没有登录态的环境下也能跑（CI、用户第一次跑 --selftest-chain）；
///   ② 断言的确定性 —— B 站上的帖子随时会变，用假数据才能稳定复现"两向相遇""一人多棒"这些边界。
/// </summary>
public static class ChainSelfTest
{
    /// <summary>自检统一的时间基日：所有用例的帖子都落在这天，避免跨日/时区让断言飘。</summary>
    private static readonly DateTime BaseDay = new(2026, 9, 6);

    /// <summary>跑全部用例；返回通过/失败数。</summary>
    public static ChainSelfTestResult Run(Action<string> log)
    {
        var state = new RunState(log ?? (_ => { }));

        // 下列用例一一对应用户敲定的遍历规则；每条独立判定、独立打印失败原因。
        state.Case("A 首棒无上一棒：Backward 在入口就停，不往虚空中走", CaseStartHasNoPrev);
        state.Case("B 末棒标记终止：Forward 到 LastMark 就停，不再解析后面的 @", CaseLastMarkStopsForward);
        state.Case("C 两向相遇即停：两向指向同一帖子时只保留一个节点", CaseTwoArmsMeet);
        state.Case("D 某方向连续 2 次失败即停该方向", CaseConsecutiveFailures);
        state.Case("E 一人多棒：同一个 uid 的多个 postId 都要在链上（不能用 uid 去重）", CaseOnePersonManyBars);
        state.Case("F 补充链接优先：--add-link 的帖子先入链，棒号人工指定，再从它继续走", CaseSupplementFirst);
        state.Case("G 方向冲突：先到者胜，同一帖子只出现一次", CaseDirectionConflict);
        state.Case("H ChainWriter 产出三个文件（chain.json / review.jsonl / report.md）", CaseWriterWritesThreeFiles);
        state.Case("I 验收分级：结构/时间/弱证据的置信度与拒收", CaseVerifierGrades);
        state.Case("J 日期与 slug 推导：未给 --date 时用入口帖发布日，Year==1 时补年份", CaseDateAndSlug);

        state.Line($"  遍历器与落盘用例：通过 {state.Passed}，失败 {state.Failed}");
        return new ChainSelfTestResult { Passed = state.Passed, Failed = state.Failed };
    }

    // ================================================================ 用例 A

    private static void CaseStartHasNoPrev(RunState s)
    {
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p1 = Facts("1001", "作者A", 1001, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        p1.Nexts.Add(Link("作者B", isPrev: false, uid: "1002", barRaw: "第2棒", declaredTime: "21:00"));

        var p2 = Facts("1002", "作者B", 1002, BaseDay.AddHours(21), bar: 2, declared: "21:00", last: true);
        p2.Prevs.Add(Link("作者A", isPrev: true, uid: "1001", barRaw: "第1棒", declaredTime: "20:00"));

        Register(reader, locator, p1);
        Register(reader, locator, p2);

        var result = Walk(reader, locator, PostRef.Opus("1001"), Query());

        s.Check("节点数=2", result.Nodes.Count == 2, $"实际 {result.Nodes.Count}");
        s.Check("没有缺口", result.Gaps.Count == 0, DescribeGaps(result));
        s.Check("Backward 在入口被首棒标记停住", result.Audit.Any(a => a.Detail?.Contains("首棒显式标记", StringComparison.Ordinal) == true),
            "审计里没找到首棒标记的停止记录");
        s.Check("Forward 到底（有末棒标记）", result.ForwardComplete);
    }

    // ================================================================ 用例 B

    private static void CaseLastMarkStopsForward(RunState s)
    {
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p1 = Facts("1101", "作者A", 1101, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        p1.Nexts.Add(Link("作者B", isPrev: false, uid: "1102", declaredTime: "21:00"));

        var p2 = Facts("1102", "作者B", 1102, BaseDay.AddHours(21), bar: 2, declared: "21:00", last: true);
        p2.Prevs.Add(Link("作者A", isPrev: true, uid: "1101", declaredTime: "20:00"));
        // 末棒后面还写了一个 @，遍历器必须在标记处就停，不能去解析它
        p2.Nexts.Add(Link("作者C", isPrev: false, uid: "1103", declaredTime: "22:00"));

        Register(reader, locator, p1);
        Register(reader, locator, p2);

        var result = Walk(reader, locator, PostRef.Opus("1101"), Query());

        s.Check("节点数=2（没把作者C 拉进来）", result.Nodes.Count == 2, $"实际 {result.Nodes.Count}");
        s.Check("末棒后的 @ 没有被解析", !locator.ResolveLog.Any(n => n.Contains("作者C", StringComparison.Ordinal)),
            "Resolve 日志里出现了作者C：" + string.Join("、", locator.ResolveLog));
        s.Check("审计记录末棒标记终止", result.Audit.Any(a => a.Detail?.Contains("末棒显式标记", StringComparison.Ordinal) == true),
            "审计里没找到末棒标记的停止记录");
    }

    // ================================================================ 用例 C

    private static void CaseTwoArmsMeet(RunState s)
    {
        // 环：P2(起点) → P3 → P1 → P2；两向在 P1 相遇。没有任何首/末棒标记。
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p2 = Facts("1202", "作者B", 1202, BaseDay.AddHours(21), bar: 2, declared: "21:00");
        p2.Prevs.Add(Link("作者A", isPrev: true, uid: "1201", declaredTime: "20:00"));
        p2.Nexts.Add(Link("作者C", isPrev: false, uid: "1203", declaredTime: "22:00"));

        var p3 = Facts("1203", "作者C", 1203, BaseDay.AddHours(22), bar: 3, declared: "22:00");
        p3.Prevs.Add(Link("作者B", isPrev: true, uid: "1202", declaredTime: "21:00"));
        p3.Nexts.Add(Link("作者A", isPrev: false, uid: "1201", declaredTime: "20:00"));

        var p1 = Facts("1201", "作者A", 1201, BaseDay.AddHours(20), bar: 1, declared: "20:00");
        p1.Prevs.Add(Link("作者C", isPrev: true, uid: "1203", declaredTime: "22:00"));
        p1.Nexts.Add(Link("作者B", isPrev: false, uid: "1202", declaredTime: "21:00"));

        Register(reader, locator, p2);
        Register(reader, locator, p3);
        Register(reader, locator, p1);

        var result = Walk(reader, locator, PostRef.Opus("1202"), Query());

        var ids = result.Nodes.Where(n => n.Facts is not null).Select(n => n.Facts!.PostId).ToList();
        s.Check("节点数=3", result.Nodes.Count == 3, $"实际 {result.Nodes.Count}");
        s.Check("postId 不重复（相遇后不重复入链）", ids.Distinct(StringComparer.Ordinal).Count() == ids.Count,
            "重复：" + string.Join("、", ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key)));
        s.Check("相遇写了方向冲突告警", result.Warnings.Any(w => w.Contains("方向冲突", StringComparison.Ordinal)),
            "告警：" + string.Join(" | ", result.Warnings));
        s.Check("审计有方向停止记录", result.Audit.Any(a => a.Step == "方向停止"));
    }

    // ================================================================ 用例 D

    private static void CaseConsecutiveFailures(RunState s)
    {
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p1 = Facts("1301", "作者A", 1301, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        // 三个都定位不到的名字：前两个各记一次失败，第三次不该再被尝试
        p1.Nexts.Add(Link("不存在的作者X", isPrev: false));
        p1.Nexts.Add(Link("不存在的作者Y", isPrev: false));
        p1.Nexts.Add(Link("不存在的作者Z", isPrev: false));

        Register(reader, locator, p1);

        var result = Walk(reader, locator, PostRef.Opus("1301"), Query());

        s.Check("解析试过 X、Y", locator.ResolveLog.Any(n => n.Contains("作者X", StringComparison.Ordinal))
            && locator.ResolveLog.Any(n => n.Contains("作者Y", StringComparison.Ordinal)),
            "Resolve 日志：" + string.Join("、", locator.ResolveLog));
        s.Check("第 3 次失败后不再解析 Z", !locator.ResolveLog.Any(n => n.Contains("作者Z", StringComparison.Ordinal)),
            "Resolve 日志：" + string.Join("、", locator.ResolveLog));
        s.Check("缺口 ≥ 2（每个没定位到的邻居一条）", result.Gaps.Count >= 2, $"实际 {result.Gaps.Count}：{DescribeGaps(result)}");
        s.Check("告警写明连续失败", result.Warnings.Any(w => w.Contains("连续 2 次失败", StringComparison.Ordinal)),
            "告警：" + string.Join(" | ", result.Warnings));
    }

    // ================================================================ 用例 E

    private static void CaseOnePersonManyBars(RunState s)
    {
        // 同一个人（uid 1401）连着发了第 1、2、3 棒：三个不同 postId 都必须进链。
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p1 = Facts("1401", "作者S", 1401, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        p1.Nexts.Add(Link("作者S", isPrev: false, uid: "1401", barRaw: "第2棒", declaredTime: "21:00"));

        var p2 = Facts("1402", "作者S", 1401, BaseDay.AddHours(21), bar: 2, declared: "21:00");
        p2.Prevs.Add(Link("作者S", isPrev: true, uid: "1401", barRaw: "第1棒", declaredTime: "20:00"));
        p2.Nexts.Add(Link("作者S", isPrev: false, uid: "1401", barRaw: "第3棒", declaredTime: "22:00"));

        var p3 = Facts("1403", "作者S", 1401, BaseDay.AddHours(22), bar: 3, declared: "22:00", last: true);
        p3.Prevs.Add(Link("作者S", isPrev: true, uid: "1401", barRaw: "第2棒", declaredTime: "21:00"));

        Register(reader, locator, p1);
        Register(reader, locator, p2);
        Register(reader, locator, p3);

        var result = Walk(reader, locator, PostRef.Opus("1401"), Query());

        var ids = result.Nodes.Where(n => n.Facts is not null).Select(n => n.Facts!.PostId).ToList();
        s.Check("节点数=3", result.Nodes.Count == 3, $"实际 {result.Nodes.Count}");
        s.Check("三个不同的 postId 都在链上（没有按 uid 去重）",
            ids.Distinct(StringComparer.Ordinal).Count() == 3,
            "链上 postId：" + string.Join("、", ids));
        s.Check("三个节点都是同一个 uid", result.Nodes.All(n => n.Facts?.AuthorUid == 1401),
            "uid：" + string.Join("、", result.Nodes.Select(n => n.Facts?.AuthorUid)));
        s.Check("三棒都找到了下一棒（没有提前断链）", result.Nodes.Count(n => n.Direction == WalkDirection.Forward) >= 2,
            "方向：" + string.Join("、", result.Nodes.Select(n => n.Direction)));
    }

    // ================================================================ 用例 F

    private static void CaseSupplementFirst(RunState s)
    {
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p2 = Facts("1502", "作者B", 1502, BaseDay.AddHours(21), bar: 2, declared: "21:00");
        p2.Prevs.Add(Link("作者A", isPrev: true, uid: "1501", declaredTime: "20:00"));
        p2.Nexts.Add(Link("作者C", isPrev: false, uid: "1503", declaredTime: "22:00"));

        var p1 = Facts("1501", "作者A", 1501, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        p1.Nexts.Add(Link("作者B", isPrev: false, uid: "1502", declaredTime: "21:00"));

        // 末棒帖子帖内没有棒号（用户补 --add-link 时手工给了 --at-bar 3）
        var p3 = Facts("1503", "作者C", 1503, BaseDay.AddHours(22), declared: "22:00", last: true);
        p3.Prevs.Add(Link("作者B", isPrev: true, uid: "1502", declaredTime: "21:00"));

        Register(reader, locator, p2);
        Register(reader, locator, p1);
        Register(reader, locator, p3);

        var supplements = new List<(PostRef Ref, int? ForcedBar)> { (PostRef.Opus("1503"), 3) };
        var result = Walk(reader, locator, PostRef.Opus("1502"), Query(), supplements);

        var order = result.Nodes.Where(n => n.Facts is not null).Select(n => n.Facts!.PostId).ToList();
        var suppNode = result.Nodes.FirstOrDefault(n => n.IsSupplement);
        s.Check("补充链接成为节点", suppNode is not null, "没有找到 IsSupplement 节点");
        s.Check("补充节点带人工指定的棒号 3", suppNode?.Facts?.BarNumber == 3, $"实际 {suppNode?.Facts?.BarNumber}");
        s.Check("Notes 记了棒号由人工指定",
            suppNode is not null && suppNode.Notes.Any(n => n.Contains("棒号由人工指定", StringComparison.Ordinal)),
            "Notes：" + (suppNode is null ? "（无节点）" : string.Join(" | ", suppNode.Notes)));
        s.Check("补充节点先入链（排在自动发现的节点之前）",
            order.IndexOf("1503") >= 0 && order.IndexOf("1501") > order.IndexOf("1503"),
            "链上顺序：" + string.Join(" → ", order));
        s.Check("没有重复节点", order.Distinct(StringComparer.Ordinal).Count() == order.Count, "链上顺序：" + string.Join(" → ", order));
        s.Check("审计记录读补充帖", result.Audit.Any(a => a.Step == "读补充帖"));
    }

    // ================================================================ 用例 G

    private static void CaseDirectionConflict(RunState s)
    {
        // 起点 P2 的两条臂都指向 X：Forward 经 C 先到 X，Backward 经 A 再到 X → 方向矛盾，先到者胜。
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p2 = Facts("1602", "作者B", 1602, BaseDay.AddHours(21), bar: 2, declared: "21:00");
        p2.Prevs.Add(Link("作者A", isPrev: true, uid: "1601", declaredTime: "20:00"));
        p2.Nexts.Add(Link("作者C", isPrev: false, uid: "1603", declaredTime: "22:00"));

        var c = Facts("1603", "作者C", 1603, BaseDay.AddHours(22), bar: 3, declared: "22:00");
        c.Prevs.Add(Link("作者B", isPrev: true, uid: "1602", declaredTime: "21:00"));
        c.Nexts.Add(Link("作者X", isPrev: false, uid: "1610", declaredTime: "23:00"));

        var a = Facts("1601", "作者A", 1601, BaseDay.AddHours(20), bar: 1, declared: "20:00");
        a.Prevs.Add(Link("作者X", isPrev: true, uid: "1610", declaredTime: "19:00"));
        a.Nexts.Add(Link("作者B", isPrev: false, uid: "1602", declaredTime: "21:00"));

        var x = Facts("1610", "作者X", 1610, BaseDay.AddHours(19), declared: "19:00");
        x.Prevs.Add(Link("作者C", isPrev: true, uid: "1603", declaredTime: "18:00"));

        Register(reader, locator, p2);
        Register(reader, locator, c);
        Register(reader, locator, a);
        Register(reader, locator, x);

        var result = Walk(reader, locator, PostRef.Opus("1602"), Query());

        var xNodes = result.Nodes.Where(n => n.Facts?.PostId == "1610").ToList();
        s.Check("X 只出现一次（先到者胜）", xNodes.Count == 1, $"实际出现 {xNodes.Count} 次");
        s.Check("X 保留先到的 Forward 方向", xNodes.Count == 1 && xNodes[0].Direction == WalkDirection.Forward,
            $"实际 {xNodes.FirstOrDefault()?.Direction}");
        s.Check("两向相遇写明方向冲突", result.Warnings.Any(w => w.Contains("方向冲突", StringComparison.Ordinal)),
            "告警：" + string.Join(" | ", result.Warnings));
    }

    // ================================================================ 用例 H

    private static void CaseWriterWritesThreeFiles(RunState s)
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "jieli-selftest-chain");
        try
        {
            var facts1 = Facts("1702", "作者B", 1702, BaseDay.AddHours(21), bar: 2, declared: "21:00");
            var facts2 = Facts("1703", "作者C", 1703, BaseDay.AddHours(22), bar: 3, declared: "22:00");

            var node2 = new ChainNode
            {
                Ref = PostRef.Opus("1703"),
                Facts = facts2,
                Direction = WalkDirection.Forward,
                Source = FetchMethod.SpaceOpusFeed,
                Depth = 1,
                Confidence = 0.95,
                MatchedBy = "反向@uid(+时间窗)",
            };
            node2.Notes.Add(ChainWalker.BarInferredPrefix + "由源帖「第3棒」标注推算");

            var result = new ChainResult
            {
                RelayName = "东方接力测试",
                Slug = "2026_东方接力测试_09-06",
                StartPostId = "1702",
            };
            result.Nodes.Add(new ChainNode
            {
                Ref = PostRef.Opus("1702"),
                Facts = facts1,
                Direction = WalkDirection.Both,
                Source = FetchMethod.StartLink,
                Depth = 0,
                Confidence = 1.0,
                IsStart = true,
            });
            result.Nodes.Add(node2);
            result.Gaps.Add(new GapItem
            {
                Kind = "candidate",
                Reason = "定位到人，但空间里没有窗口内的帖子",
                FromPostId = "1703",
                FromPostUrl = facts2.Url,
                Direction = WalkDirection.Forward,
                ExpectedName = "作者D",
                ExpectedBar = "4",
                ExpectedTime = "23:00",
                TriedMethods = { "IUserLocator.Resolve → uid=1704", "空间投稿/文章/视频搜索" },
                SuggestedCommand = "--add-link <第4棒的帖子链接>",
            });
            result.Warnings.Add("Forward 方向连续 2 次失败，停止该方向（测试用告警）");

            var dir = ChainWriter.Write(workRoot, "selftest_chain", result, Query(), manualDate: null, log: null);
            var chainJson = Path.Combine(dir, "chain.json");
            var reviewPath = Path.Combine(dir, "review.jsonl");
            var reportPath = Path.Combine(dir, "report.md");

            s.Check("产物目录存在", Directory.Exists(dir), dir);
            s.Check("chain.json 存在", File.Exists(chainJson), chainJson);
            s.Check("review.jsonl 存在", File.Exists(reviewPath), reviewPath);
            s.Check("report.md 存在", File.Exists(reportPath), reportPath);

            var jsonText = File.ReadAllText(chainJson);
            s.Check("chain.json 中文未被转义", jsonText.Contains("东方接力测试", StringComparison.Ordinal),
                "找不到未转义的活动名");
            var parsed = false;
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                parsed = true;
            }
            catch (JsonException ex)
            {
                s.Check("chain.json 是合法 JSON", false, ex.Message);
            }
            if (parsed) s.Check("chain.json 是合法 JSON", true);

            var loaded = ReviewStore.Load(workRoot, "selftest_chain");
            s.Check("review.jsonl 能被 ReviewStore 读回 2 条", loaded.Count == 2, $"实际 {loaded.Count}");
            s.Check("review 里做了字段映射（CardId=postId、TypeLabel 非空、Notes 非空）",
                loaded.Count == 2 && loaded.All(i => i.CardId.Length > 0 && i.TypeLabel.Length > 0 && i.Notes.Count > 0),
                "读回内容：" + string.Join(" | ", loaded.Select(i => i.CardId + "/" + i.TypeLabel + "/" + i.Notes.Count)));

            var report = File.ReadAllText(reportPath);
            s.Check("report.md 有缺口清单", report.Contains("缺口清单", StringComparison.Ordinal));
            s.Check("report.md 给了可复制的 --add-link 模板", report.Contains("--add-link", StringComparison.Ordinal));
            s.Check("report.md 标注了推算棒号", report.Contains("（推算）", StringComparison.Ordinal));
            s.Check("report.md 写了审计告警", report.Contains("审计告警", StringComparison.Ordinal));
        }
        finally
        {
            // 自检不能在用户目录里留垃圾；失败也要删，方便下一次重跑。
            try
            {
                if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true);
            }
            catch (IOException)
            {
                // 极端情况下删不掉（文件被占用）不该让整轮自检失败，留给用户手工清。
            }
        }
    }

    // ================================================================ 用例 I

    private static void CaseVerifierGrades(RunState s)
    {
        using var http = new ChainHttp();
        var verifier = new MatchVerifier(http);
        var query = Query();

        var from = Facts("1801", "作者A", 1801, BaseDay.AddHours(20), bar: 1, declared: "20:00");
        var link = Link("作者B", isPrev: false, uid: "1802", barRaw: "第2棒", declaredTime: "21:00");

        // ① 结构证据 + 时间窗内 = 0.99
        var both = Facts("1802", "作者B", 1802, BaseDay.AddHours(21), bar: 2, declared: "21:00");
        both.Prevs.Add(Link("作者A", isPrev: true, uid: "1801", declaredTime: "20:00"));
        var outcomeBoth = verifier.Verify(from, link, both, query, WalkDirection.Forward);
        s.Check("结构+时间 → 0.99", outcomeBoth.Accepted && Math.Abs(outcomeBoth.Confidence - 0.99) < 0.001,
            $"{outcomeBoth.Accepted}/{outcomeBoth.Confidence:0.00}：{outcomeBoth.Reason}");

        // ② 只有结构证据（发布时间远在窗口外）= 0.95
        var structuralOnly = Facts("1803", "作者B", 1802, BaseDay.AddDays(1).AddHours(3), bar: 2);
        structuralOnly.Prevs.Add(Link("作者A", isPrev: true, uid: "1801", declaredTime: "20:00"));
        var outcomeStructural = verifier.Verify(from, link, structuralOnly, query, WalkDirection.Forward);
        s.Check("仅结构（超窗）→ 0.95", outcomeStructural.Accepted && Math.Abs(outcomeStructural.Confidence - 0.95) < 0.001,
            $"{outcomeStructural.Accepted}/{outcomeStructural.Confidence:0.00}：{outcomeStructural.Reason}");

        // ③ 只有时间窗内（没有反向 @ 证据）= 0.6
        var timeOnly = Facts("1804", "作者B", 1802, BaseDay.AddHours(21), bar: 2);
        var outcomeTime = verifier.Verify(from, link, timeOnly, query, WalkDirection.Forward);
        s.Check("仅时间窗内 → 0.6", outcomeTime.Accepted && Math.Abs(outcomeTime.Confidence - 0.6) < 0.001,
            $"{outcomeTime.Accepted}/{outcomeTime.Confidence:0.00}：{outcomeTime.Reason}");

        // ④ 超窗但仍带接力学弱证据 = 0.35，并注明"可能一人多棒"
        var weak = Facts("1805", "作者B", 1802, BaseDay.AddHours(10), bar: 2, declared: "10:00");
        var outcomeWeak = verifier.Verify(from, link, weak, query, WalkDirection.Forward);
        s.Check("超窗弱证据 → 0.35", outcomeWeak.Accepted && Math.Abs(outcomeWeak.Confidence - 0.35) < 0.001,
            $"{outcomeWeak.Accepted}/{outcomeWeak.Confidence:0.00}：{outcomeWeak.Reason}");
        s.Check("弱证据结论写明「近邻超窗(可能一人多棒)」",
            outcomeWeak.Reason.Contains("近邻超窗(可能一人多棒)", StringComparison.Ordinal), outcomeWeak.Reason);

        // ⑤ 既没有结构证据、也不在窗口内、也没有接力学特征 → 拒收
        var none = Facts("1806", "无关作者", 1806, BaseDay.AddHours(10));
        var outcomeNone = verifier.Verify(from, link, none, query, WalkDirection.Forward);
        s.Check("无任何证据 → 拒收", !outcomeNone.Accepted, $"Accepted={outcomeNone.Accepted}：{outcomeNone.Reason}");
    }

    // ================================================================ 用例 J

    private static void CaseDateAndSlug(RunState s)
    {
        var reader = new FakeReader();
        var locator = new FakeLocator();
        var only = Facts("1901", "作者A", 1901, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        Register(reader, locator, only);

        // 用户没给 --date：遍历器应当用入口帖发布日补 DayStart，并推出 DayEnd=+24h
        var query = new RelayQuery { DayStart = DateTime.MinValue, WindowMinutes = 90 };
        var result = Walk(reader, locator, PostRef.Opus("1901"), query);

        s.Check("DayStart 用入口帖发布日补齐", query.DayStart == BaseDay.Date,
            $"实际 {query.DayStart:yyyy-MM-dd HH:mm}");
        s.Check("DayEnd = DayStart + 24h（DeclaredHours=0）", query.DayEnd - query.DayStart == TimeSpan.FromHours(24),
            $"实际 {query.DayEnd - query.DayStart}");
        s.Check("slug = 年份_活动名_月-日", result.Slug.StartsWith("2026_", StringComparison.Ordinal)
            && result.Slug.Contains("_09-06", StringComparison.Ordinal),
            "实际 slug：" + result.Slug);

        // 用户只给了月日（--date 9-6）：年份用入口帖的年份补齐
        var yearQuery = new RelayQuery { DayStart = new DateTime(1, 9, 6), WindowMinutes = 90 };
        Walk(reader, locator, PostRef.Opus("1901"), yearQuery);
        s.Check("DayStart.Year==1 时用入口帖年份补齐", yearQuery.DayStart.Year == 2026,
            $"实际 {yearQuery.DayStart:yyyy-MM-dd}");

        // 每个用例共用同一份假数据；上面那条帖子本身没有下一棒，所有缺口都是预期的，这里只确认没崩。
        s.Check("孤立帖不崩：节点=1", result.Nodes.Count == 1, $"实际 {result.Nodes.Count}");
    }

    // ================================================================ 假实现

    /// <summary>假 reader：从内存字典里取帖子；没有就抛 ChainFetchException（和真 reader 一样）。</summary>
    private sealed class FakeReader : IPostReader
    {
        internal readonly Dictionary<string, PostFacts> Posts = new(StringComparer.Ordinal);
        internal readonly List<string> ReadLog = new();

        public PostFacts Read(PostRef reference, Action<string>? log, CancellationToken ct)
        {
            ReadLog.Add(reference.Id);
            if (Posts.TryGetValue(reference.Id, out var facts)) return facts;
            throw new ChainFetchException("假 reader：内存里没有 postId=" + reference.Id);
        }
    }

    /// <summary>假 locator：名字 → uid 用内存表；uid → 候选帖子也用内存表。不联网。</summary>
    private sealed class FakeLocator : IUserLocator
    {
        internal readonly List<UserRef> Users = new();
        internal readonly Dictionary<string, List<PostCandidate>> CandidatesByUid = new(StringComparer.Ordinal);
        internal readonly List<string> ResolveLog = new();

        public UserRef? Resolve(string name, string? hintUid, Action<string>? log, CancellationToken ct)
        {
            ResolveLog.Add(name);

            // 真实现会先吃 hintUid；这里同名多号时也只靠名字，因为测试帖子都是名字唯一。
            var cleaned = TextNormalizer.CleanMention(name);
            foreach (var user in Users)
            {
                if (TextNormalizer.SameName(user.Name, cleaned)) return user;
            }

            return null;
        }

        public IReadOnlyList<PostCandidate> FindPosts(UserRef user, RelayQuery query, Action<string>? log, CancellationToken ct)
        {
            var key = user.Uid.ToString(CultureInfo.InvariantCulture);
            if (!CandidatesByUid.TryGetValue(key, out var list)) return Array.Empty<PostCandidate>();
            // 每次返回新列表：调用方会按时间距离排序，别把假数据的顺序改乱。
            return list.ToList();
        }
    }

    // ================================================================ 测试脚手架

    private static void Register(FakeReader reader, FakeLocator locator, PostFacts facts)
    {
        reader.Posts[facts.PostId] = facts;

        if (locator.Users.All(u => u.Uid != facts.AuthorUid))
        {
            locator.Users.Add(new UserRef
            {
                Uid = facts.AuthorUid,
                Name = facts.AuthorName,
                Method = FetchMethod.UserSearch,
                Exact = true,
            });
        }

        var key = facts.AuthorUid.ToString(CultureInfo.InvariantCulture);
        if (!locator.CandidatesByUid.TryGetValue(key, out var list))
        {
            list = new List<PostCandidate>();
            locator.CandidatesByUid[key] = list;
        }

        list.Add(new PostCandidate
        {
            Ref = PostRef.Opus(facts.PostId),
            Title = facts.Title ?? string.Empty,
            PubTs = facts.PubTs,
            Method = FetchMethod.SpaceOpusFeed,
            Why = "假 locator：该用户空间窗口内的帖子",
        });
    }

    private static ChainResult Walk(FakeReader reader, FakeLocator locator, PostRef start, RelayQuery query,
        IReadOnlyList<(PostRef Ref, int? ForcedBar)>? supplements = null)
    {
        // ChainHttp 只构造不发请求：验收/遍历都由假 reader/locator 驱动，断网也能跑。
        using var http = new ChainHttp();
        var walker = new ChainWalker(http, reader, locator) { MaxNodes = 40, MaxConsecutiveFailures = 2 };
        return walker.Walk(start, query, supplements, log: null);
    }

    private static RelayQuery Query() => new()
    {
        DayStart = BaseDay,
        DayEnd = BaseDay.AddHours(24),
        DeclaredHours = 0,
        WindowMinutes = 90,
    };

    private static PostFacts Facts(string postId, string author, long uid, DateTime pub,
        int? bar = null, string? declared = null, bool first = false, bool last = false, bool isEx = false)
    {
        return new PostFacts
        {
            PostId = postId,
            Kind = PostKind.Opus,
            Url = "https://www.bilibili.com/opus/" + postId,
            AuthorUid = uid,
            AuthorName = author,
            PubTs = ToUnix(pub),
            RelayName = "东方接力测试",
            Title = "测试帖 " + postId + " · " + author,
            BarRaw = bar is not null ? "第" + bar.Value.ToString(CultureInfo.InvariantCulture) + "棒" : null,
            BarNumber = bar,
            IsEx = isEx,
            DeclaredTime = declared,
            FirstMark = first,
            FirstMarkRaw = first ? "第一棒" : null,
            LastMark = last,
            LastMarkRaw = last ? "最后一棒" : null,
            BodyText = "接力测试正文 " + postId,
            MergedText = "接力测试正文 " + postId + "，作者 " + author,
        };
    }

    private static NeighborLink Link(string name, bool isPrev, string? uid = null, string? barRaw = null, string? declaredTime = null)
    {
        return new NeighborLink
        {
            IsPrev = isPrev,
            Name = name,
            Uid = uid,
            RawText = (isPrev ? "上一棒：" : "下一棒：") + name,
            BarRaw = barRaw,
            DeclaredTime = declaredTime,
        };
    }

    private static long ToUnix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    private static string DescribeGaps(ChainResult result)
        => result.Gaps.Count == 0 ? "（无）" : string.Join(" | ", result.Gaps.Select(g => g.Kind + ":" + g.Reason));

    // ================================================================ 计数与输出

    private sealed class RunState
    {
        private readonly Action<string> _log;

        internal RunState(Action<string> log) => _log = log;

        internal int Passed { get; private set; }
        internal int Failed { get; private set; }

        internal void Line(string text) => _log(text);

        /// <summary>跑一条用例；用例内部抛异常按失败记，但绝不吞掉原因。</summary>
        internal void Case(string title, Action<RunState> body)
        {
            _log("  · " + title);
            try
            {
                body(this);
            }
            catch (Exception ex)
            {
                Failed++;
                _log($"    ✗ 用例抛异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 断言一条。detail 一律按"失败原因"来写：通过时不打印，避免出现
        /// 「✓ …（审计里没找到…）」这种自相矛盾的输出把读者带偏。
        /// </summary>
        internal bool Check(string what, bool ok, string? detail = null)
        {
            if (ok)
            {
                Passed++;
                _log("    ✓ " + what);
            }
            else
            {
                Failed++;
                _log("    ✗ " + what + (detail is null ? string.Empty : $"：{detail}"));
            }

            return ok;
        }
    }
}
