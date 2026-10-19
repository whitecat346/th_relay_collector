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

        // K~P 是抽取器（NeighborExtractor）的回归用例：只喂文章正文，不联网、不碰遍历器。
        state.Case("K 行首时段「16：30！」：全角冒号 + 感叹号独立行也认", CaseLineStartTimeFullWidthBang);
        state.Case("L 行首时段「15：30@反应迟顿」：全角冒号紧跟 @ 也认", CaseLineStartTimeGluedAt);
        state.Case("M 创作者归属：18:30 阿雯@藏粮阁魔导书 → 创阿雯/代发", CaseSlotNameAccountCreator);
        state.Case("N 创作者归属：19:00 作者:@羽衣的皇带鱼 → 创羽衣的皇带鱼/代发", CaseAuthorLabelCreator);
        state.Case("O 鸣谢行不是作者归属：13:00 @蔼石 + 鸣谢约稿插图作者：@SAM271", CaseAcknowledgementNotCreator);
        state.Case("P AM/PM 换算：10:08 PM @交联纤维素 → 22:08", CaseMeridiemTime);
        state.Case("Q 年份闸门：同话题往年（差 365 天）的帖子即使结构+声明时段齐全也拒收", CaseOffYearRejected);
        state.Case("R 时段网格：整点/半小时成格、重复投稿只占一格、前一天的预告贴不进格", CaseSlotGrid);
        state.Case("S 按届过滤：同一话题被复用四届时，只留含今年的那一届（--year/--date 可选届）", CaseTopicYearFilter);
        state.Case("T 人工棒号（--at-bar）：只作棒号权威，缺口位置仍按时间判定", CaseManualBarOnlyLabel);
        state.Case("U 补录补全（--add-link --into）：已知节点零网络重放，补录帖续走并补掉缺口", CaseAddLinkCompletes);

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

    // ================================================================ 用例 K~P：抽取器回归

    private static void CaseLineStartTimeFullWidthBang(RunState s)
    {
        var facts = Extracted("龙胆紫苑", "16：30！");
        s.Check("DeclaredTime=16:30（全角冒号归一成半角）", facts.DeclaredTime == "16:30",
            $"实际 {facts.DeclaredTime ?? "null"}");
    }

    private static void CaseLineStartTimeGluedAt(RunState s)
    {
        var facts = Extracted("反应迟顿", "15：30@反应迟顿");
        s.Check("DeclaredTime=15:30（全角冒号紧贴 @ 仍认）", facts.DeclaredTime == "15:30",
            $"实际 {facts.DeclaredTime ?? "null"}");
    }

    private static void CaseSlotNameAccountCreator(RunState s)
    {
        var facts = Extracted("藏粮阁魔导书", "18:30 阿雯@藏粮阁魔导书\n下一棒：@羽衣的皇带鱼", "阿雯", "藏粮阁魔导书", "羽衣的皇带鱼");
        s.Check("18:30 / 创阿雯 / 代发（名字@账号形态，取第一个名字）",
            facts.DeclaredTime == "18:30" && facts.CreatorInText == "阿雯" && facts.IsProxy,
            $"实际 time={facts.DeclaredTime ?? "null"} creator={facts.CreatorInText ?? "null"} proxy={facts.IsProxy}");
    }

    private static void CaseAuthorLabelCreator(RunState s)
    {
        var facts = Extracted("龙胆紫苑", "19:00 作者:@羽衣的皇带鱼", "羽衣的皇带鱼", "藏粮阁魔导书");
        s.Check("19:00 / 创羽衣的皇带鱼 / 代发（作者: 标注优先）",
            facts.DeclaredTime == "19:00" && facts.CreatorInText == "羽衣的皇带鱼" && facts.IsProxy,
            $"实际 time={facts.DeclaredTime ?? "null"} creator={facts.CreatorInText ?? "null"} proxy={facts.IsProxy}");
    }

    private static void CaseAcknowledgementNotCreator(RunState s)
    {
        var facts = Extracted("蔼石", "13:00 @蔼石\n鸣谢约稿插图作者：@SAM271", "蔼石", "SAM271");
        s.Check("13:00 / 无创作者 / 不算代发（鸣谢行的作者：不是归属）",
            facts.DeclaredTime == "13:00" && facts.CreatorInText is null && !facts.IsProxy,
            $"实际 time={facts.DeclaredTime ?? "null"} creator={facts.CreatorInText ?? "null"} proxy={facts.IsProxy}");
    }

    private static void CaseMeridiemTime(RunState s)
    {
        var facts = Extracted("交联纤维素", "10:08 PM @交联纤维素", "交联纤维素");
        s.Check("DeclaredTime=22:08（PM 换算成 24 小时制）", facts.DeclaredTime == "22:08",
            $"实际 {facts.DeclaredTime ?? "null"}");
    }

    // ================================================================ 用例 Q

    /// <summary>
    /// 年份闸门：同一个话题名年年复用（「7月26日卡娜日」2024/2025/2026 都有），
    /// 往年那批帖子会带着同样的「第N棒 / 8:00」声明时段混进来。
    /// 只有「结构证据 + 声明时段」还不够 —— 发布年份离本场活动太远就必须拒收。
    /// </summary>
    private static void CaseOffYearRejected(RunState s)
    {
        using var http = new ChainHttp();
        var verifier = new MatchVerifier(http);
        var query = Query();

        var from = Facts("1901", "作者A", 1901, BaseDay.AddHours(7), bar: 8, declared: "7:00");
        var link = Link("作者B", isPrev: false, uid: "1902", barRaw: "第9棒", declaredTime: "8:00");

        // ① 去年同一天发的「第 9 棒 8:00」：结构齐全、声明时段也对得上，但发布时间差了一年 → 必须拒收
        var lastYear = Facts("1902", "作者B", 1902, BaseDay.AddYears(-1).AddHours(8), bar: 9, declared: "8:00");
        lastYear.Prevs.Add(Link("作者A", isPrev: true, uid: "1901", declaredTime: "7:00"));
        var outcomeLastYear = verifier.Verify(from, link, lastYear, query, WalkDirection.Forward);
        s.Check("往年同话题帖（差 365 天）→ 拒收", !outcomeLastYear.Accepted,
            $"Accepted={outcomeLastYear.Accepted}：{outcomeLastYear.Reason}");
        s.Check("拒收理由写明「超出本场活动范围」",
            outcomeLastYear.Reason.Contains("超出本场活动范围", StringComparison.Ordinal), outcomeLastYear.Reason);

        // ② 提前一天发布占位（实测有这种情况：10-07 发 10-08 的 15:30 棒）→ 仍然收
        var early = Facts("1903", "作者B", 1902, BaseDay.AddDays(-1).AddHours(15), bar: 9, declared: "8:00");
        early.Prevs.Add(Link("作者A", isPrev: true, uid: "1901", declaredTime: "7:00"));
        var outcomeEarly = verifier.Verify(from, link, early, query, WalkDirection.Forward);
        s.Check("提前一天发布占位 → 仍然收", outcomeEarly.Accepted,
            $"Accepted={outcomeEarly.Accepted}：{outcomeEarly.Reason}");
    }

    // ================================================================ 用例 R

    /// <summary>
    /// 时段网格（无名册的活动：正文只写「17:00 @某人」，不写「第N棒」）。
    /// 实测语料 topic 1349127 的形态：整点+半小时成格、同一棒位有重复投稿、
    /// 还有一条**前一天**发的预告贴自称 15:30（当年差点把整个棒号推后一格）。
    /// </summary>
    private static void CaseSlotGrid(RunState s)
    {
        var day = new DateTime(2026, 10, 8);
        var bars = new List<ParsedBar>
        {
            Bar("a", "作者A", day.AddHours(0)),
            Bar("b", "作者B", day.AddHours(1)),
            Bar("c", "作者C", day.AddHours(1)),                 // 同一棒位重复投稿
            Bar("d", "作者D", day.AddHours(2), declared: "2:00"),
            Bar("e", "作者E", day.AddHours(1).AddMinutes(37), declared: "1:30"), // 迟到但声明了时段
            Bar("f", "作者F", day.AddDays(-1).AddHours(15), declared: "15:30"),  // 前一天的预告贴
        };

        var plan = SlotGrid.Infer(bars, null);
        s.Check("网格建成", plan.Ok, string.Join("；", plan.Warnings));
        s.Check("活动日=2026-10-08", plan.DayText.Contains("10-08", StringComparison.Ordinal), plan.DayText);
        s.Check("槽位数=4（00:00/01:00/01:30/02:00）", plan.Slots.Count == 4,
            $"实际 {plan.Slots.Count}：{string.Join(",", plan.Slots.Select(x => x.Time))}");
        s.Check("前一天 15:30 的预告贴不在网格上", SlotGrid.SlotOf(bars[5], plan) is null,
            SlotGrid.SlotOf(bars[5], plan)?.Time ?? "null");
        s.Check("声明时段优先于发布时刻（1:37 → 1:30 槽）",
            SlotGrid.SlotOf(bars[4], plan)?.Minutes == 90,
            SlotGrid.SlotOf(bars[4], plan) is { } slotE ? $"{slotE.Time}（{slotE.Minutes} 分）" : "null");

        var result = new ValidationResult();
        SlotGrid.Assign(bars, plan, result);
        s.Check("第 i 格 = 第 i 棒：00:00→1、01:00→2、01:30→3、02:00→4",
            bars[0].BarNumber == 1 && bars[1].BarNumber == 2 && bars[4].BarNumber == 3 && bars[3].BarNumber == 4,
            string.Join(",", bars.Select(b => $"{b.CardId}:{b.BarNumber?.ToString() ?? "null"}")));
        s.Check("同棒位重复投稿：只留一条，另一条标记 IsDuplicate 并进 ignored 清单",
            result.Ignored.Count == 1 && bars.Count(b => b.IsDuplicate) == 1,
            $"Ignored={result.Ignored.Count}，IsDuplicate={bars.Count(b => b.IsDuplicate)}");
        s.Check("不在活动日的预告贴不赋棒号", bars[5].BarNumber is null,
            bars[5].BarNumber?.ToString() ?? "null");
    }

    /// <summary>
    /// 按届过滤：同一个话题常被年度接力反复复用（实测 topic 1101507 跨 2023–2026 四届，88 条）。
    /// 规则：按发布时间排序、相邻间隔 &gt;45 天切届，只留「含今年」的那一届。
    /// </summary>
    private static void CaseTopicYearFilter(RunState s)
    {
        var cards = new List<TopicCard>();
        void Add(string id, DateTime day) =>
            cards.Add(new TopicCard { Id = id, AuthorName = "作者" + id, PubTs = ToUnix(day) });

        for (var i = 0; i < 24; i++) Add("26_" + i, new DateTime(2026, 10, 1).AddHours(i));
        for (var i = 0; i < 16; i++) Add("25_" + i, new DateTime(2025, 10, 1).AddHours(i));
        for (var i = 0; i < 18; i++) Add("24_" + i, new DateTime(2024, 10, 1).AddHours(i));
        for (var i = 0; i < 30; i++) Add("23_" + i, new DateTime(2023, 10, 1).AddHours(i));

        var sel = TopicYearFilter.Select(cards, currentYear: 2026);
        s.Check("按届过滤：只留含今年的那一届（2026，24 条）", sel.Kept.Count == 24 && sel.Year == 2026, sel.Note);
        s.Check("排除其它三届（16+18+30=64 条）", sel.Dropped.Count == 64 && sel.DroppedEditions.Count == 3, sel.Note);
        s.Check("说明里写明保留届的起止日期", sel.Note.Contains("2026-10-01", StringComparison.Ordinal), sel.Note);

        var old = TopicYearFilter.Select(cards, yearOverride: 2024, currentYear: 2026);
        s.Check("--year 2024 能挑出 2024 那一届", old.Year == 2024 && old.Kept.Count == 18, old.Note);

        var byDate = TopicYearFilter.Select(cards, dateOverride: new DateTime(2025, 10, 1, 5, 0, 0), currentYear: 2026);
        s.Check("--date 2025-10-01 按日期选届", byDate.Year == 2025 && byDate.Kept.Count == 16, byDate.Note);
    }

    /// <summary>
    /// `--at-bar`：用户给的棒号只作标签（权威），**缺口位置仍按时间判定** ——
    /// 不能因为棒号从 1,2 跳到 9 就报「缺棒 3..8」。
    /// </summary>
    private static void CaseManualBarOnlyLabel(RunState s)
    {
        var day = new DateTime(2026, 10, 8);
        var bars = new List<ParsedBar>
        {
            Bar("a", "作者A", day.AddHours(0)),
            Bar("b", "作者B", day.AddHours(1)),
            Bar("c", "作者C", day.AddHours(2)),
        };
        var manual = new Dictionary<string, int>(StringComparer.Ordinal) { ["c"] = 9 };

        var result = ArtifactValidator.ValidateFromBars(bars, null, null, manual);
        var c = bars.First(x => x.CardId == "c");

        s.Check("人工棒号生效（第 9 棒）", c.BarNumber == 9, c.BarNumber?.ToString() ?? "null");
        s.Check("人工棒号带规定的 note 前缀",
            c.Notes.Any(n => n.StartsWith(ArtifactValidator.BarManualNote, StringComparison.Ordinal)),
            string.Join("；", c.Notes));
        s.Check("不因棒号跳号而断言失败（缺口按时间判定）", result.AssertionFailures.Count == 0,
            string.Join("；", result.AssertionFailures));
        s.Check("给出「缺口位置按时间」的警告",
            result.Warnings.Any(w => w.Contains("缺口位置", StringComparison.Ordinal)),
            string.Join("；", result.Warnings));
    }

    /// <summary>
    /// `--add-link --into &lt;slug&gt;` 的补全语义：已知节点零网络种回，补录帖当新落点续走，
    /// 碰到已知节点即停，缺口减少、目录名不变。
    /// </summary>
    private static void CaseAddLinkCompletes(RunState s)
    {
        var reader = new FakeReader();
        var locator = new FakeLocator();

        var p1 = Facts("2001", "作者A", 2001, BaseDay.AddHours(20), bar: 1, declared: "20:00", first: true);
        p1.Nexts.Add(Link("作者B", isPrev: false, uid: "2002", declaredTime: "21:00"));

        var p2 = Facts("2002", "作者B", 2002, BaseDay.AddHours(21), bar: 2, declared: "21:00");
        p2.Prevs.Add(Link("作者A", isPrev: true, uid: "2001", declaredTime: "20:00"));
        p2.Nexts.Add(Link("作者C", isPrev: false, uid: "2003", declaredTime: "22:00"));

        var p3 = Facts("2003", "作者C", 2003, BaseDay.AddHours(22), bar: 3, declared: "22:00");
        p3.Prevs.Add(Link("作者B", isPrev: true, uid: "2002", declaredTime: "21:00"));
        p3.LastMark = true;
        p3.LastMarkRaw = "最后一棒";

        // 第一次遍历时作者C 还搜不到 → 留下一个缺口
        Register(reader, locator, p1);
        Register(reader, locator, p2);
        var first = Walk(reader, locator, PostRef.Opus("2001"), Query());
        s.Check("补录前：2 个节点、1 处缺口",
            first.Nodes.Count == 2 && first.Gaps.Count == 1,
            $"节点 {first.Nodes.Count}，缺口 {first.Gaps.Count}");

        Register(reader, locator, p3);
        reader.ReadLog.Clear();

        using var http = new ChainHttp();
        var walker = new ChainWalker(http, reader, locator) { MaxNodes = 40, MaxConsecutiveFailures = 2 };
        var completed = walker.Complete(first, new[] { (PostRef.Opus("2003"), (int?)3) }, Query(), log: null);

        s.Check("已知节点不重读（补录不重放整条链）",
            !reader.ReadLog.Contains("2001") && !reader.ReadLog.Contains("2002"),
            string.Join(",", reader.ReadLog));
        s.Check("补录后节点数=3", completed.Nodes.Count == 3,
            string.Join(",", completed.Nodes.Select(n => n.Ref.Id)));
        s.Check("补录后缺口归零", completed.Gaps.Count == 0, DescribeGaps(completed));
        s.Check("补录帖标记为补充落点",
            completed.Nodes.Any(n => n.IsSupplement && n.Ref.Id == "2003"),
            string.Join(",", completed.Nodes.Select(n => $"{n.Ref.Id}:{n.IsSupplement}")));
        s.Check("目录名（slug）沿用原链", completed.Slug == first.Slug, $"{first.Slug} → {completed.Slug}");
    }

    /// <summary>造一条 ParsedBar：CardId、发布者、本地发布时刻（可选声明时段）。</summary>
    private static ParsedBar Bar(string id, string poster, DateTime local, string? declared = null) => new()
    {
        CardId = id,
        PosterName = poster,
        ResolvedAuthor = poster,
        PubTs = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeSeconds(),
        DeclaredTime = declared,
    };

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

        /// <summary>自检里没有真实话题接口，忽略即可（假 locator 直接按用户表返回）。</summary>
        public void PrimeTopic(long? topicId) { }

        /// <summary>自检里没有话题卡片索引，返回 null 让遍历器跳过「按话题时间表补全」。</summary>
        public TopicIndex? EnsureTopic(Action<string>? log, CancellationToken ct) => null;
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

    /// <summary>
    /// 只跑抽取器（NeighborExtractor.Extract），造一条最小 PostFacts：不联网、不碰遍历器。
    /// 正文给什么就抽什么，用于「段首时段 / 创作者归属」的回归用例。
    /// </summary>
    private static PostFacts Extracted(string author, string body, params string[] mentionNames)
    {
        var facts = new PostFacts
        {
            PostId = "9001",
            Kind = PostKind.Opus,
            Url = "https://www.bilibili.com/opus/9001",
            AuthorUid = 9001,
            AuthorName = author,
            PubTs = ToUnix(BaseDay.AddHours(22)),
            RelayName = "东方接力测试",
            Title = string.Empty,
            BodyText = body,
            MergedText = body,
        };
        foreach (var name in mentionNames)
        {
            facts.Mentions.Add(new AtMention { Name = name, Ordinal = facts.Mentions.Count });
        }

        NeighborExtractor.Extract(facts);
        return facts;
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
