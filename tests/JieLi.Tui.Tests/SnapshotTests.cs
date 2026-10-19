using JieLi.Tui.Text;
using JieLi.Tui.Tui;
using JieLi.Tui.Tui.Rendering;

namespace JieLi.Tui.Tests;

/// <summary>
/// 布局快照：把「代表性一帧」在 80/100/140/200 列 × 无颜色/16 色下渲染成纯文本存进仓库。
/// 快照变了要人看 diff 确认是有意的外观改动，而不是悄悄回归。
/// 更新方式：设置环境变量 UPDATE_SNAPSHOTS=1 跑测试。
/// </summary>
public class SnapshotTests
{
    private static readonly int[] Widths = { 80, 100, 140, 200 };

    private static string SnapshotDir => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Snapshots"));

    public static IEnumerable<object[]> Cases()
    {
        foreach (int w in Widths)
            foreach (string mode in new[] { "plain", "ansi16" })
                yield return new object[] { w, mode };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MainView_MatchesGolden(int width, string mode)
    {
        var theme = mode == "ansi16" ? Theme.ForDepth(Theme.Depth.Ansi16) : Theme.None;
        string actual = RenderMainView(width, 30, theme);
        Verify($"review-{width}-{mode}.txt", actual);
    }

    [Fact]
    public void AsciiFallback_MatchesGolden()
    {
        ConsoleHost.AsciiOnly = true;
        try { Verify("review-80-ascii.txt", RenderMainView(80, 30, Theme.None)); }
        finally { ConsoleHost.AsciiOnly = false; }
    }

    [Fact]
    public void NarrowTable_MatchesGolden() => Verify("table-60-plain.txt", RenderTable(60));

    [Fact]
    public void WideTable_MatchesGolden() => Verify("table-160-plain.txt", RenderTable(160));

    private static string RenderMainView(int width, int height, Theme theme)
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, width, height, interactive: false);
        host.BeginFrame();

        var f = new FrameBuilder(host, theme, width, height);
        f.Header(theme.Bold(TextWidth.Truncate("人工确认 · 2026_8月15日魅魔日接力1.5_08-15", Math.Max(10, width - 14)))
                 + theme.Muted("   topic 1101507"),
                 theme.Muted("待确认 ") + theme.Warning("2") + theme.Muted("  已确认 ")
                 + theme.Success("5") + theme.Muted("  已忽略 0   共 7 条  行 2/7"));

        var table = new TableBuilder(
            new TableColumn { Header = "棒", Width = 5, RightAlign = true },
            new TableColumn { Header = "时段", Width = 6 },
            new TableColumn { Header = "创作者", Width = 18 },
            new TableColumn { Header = "状态", Width = 10 },
            new TableColumn { Header = "依据", Flexible = true, Min = 10 });

        var rows = new List<string[]>
        {
            new[] { "7", "13:00", "240岁是魔法使", "ok", "来源：入口帖（方向 双向，深度 0，置信 1.00）" },
            new[] { "8", "13:30", "一个很长很长很长的昵称", "待确认", "仅昵称匹配，需要人工核对" },
            new[] { "EX", "-", "雾雨梦灵", "重复", "棒号重复，保留其一" },
        };
        f.Body(table.Render(width, rows, i => i switch
        {
            1 => Role.Selected,
            2 => Role.Warning,
            _ => Role.Normal,
        }, theme));

        f.Body(theme.Muted(ConsoleHost.Rule(width)));
        f.Body(theme.Bold("第 8 棒") + "  13:30  " + theme.Accent("一个很长很长很长的昵称"));
        f.Body(theme.Muted("发布者") + " 一个很长很长很长的昵称  " + theme.Muted("mid") + " 491997588  "
               + theme.Muted("发布") + " 2026-08-15 13:31  " + theme.Muted("偏差") + " +1m");
        f.Body(theme.Muted("作品") + " https://www.bilibili.com/opus/1236657690311131138");
        f.Body(theme.Warning("⚠ 与链上相邻棒的时段不一致"));
        f.Body(theme.Muted("正文 ") + TextWidth.Truncate(
            "第八棒：13：30@一个很长很长很长的昵称 这个一格一格的笔刷好玩捏，但b站压码率所以看不着 上一棒：@240岁是魔法使 下一棒：@龙胆紫苑", Math.Max(10, width - 6)));

        f.Toast("已确认：第 8 棒 一个很长很长很长的昵称");
        f.Keys(("Enter", "确认"), ("X", "忽略"), ("V", "全文"), ("B", "棒号"), ("T", "时段"),
               ("U", "创作者"), ("K", "类型"), ("N", "备注"), ("G", "缺口"), ("C", "链条"),
               ("S", "保存"), ("?", "帮助"), ("Esc", "上级"), ("Q", "退出"));
        f.Render();
        host.EndFrame();
        return sw.ToString();
    }

    private static string RenderTable(int width)
    {
        var table = new TableBuilder(
            new TableColumn { Header = "棒", Width = 5, RightAlign = true },
            new TableColumn { Header = "时段", Width = 6 },
            new TableColumn { Header = "创作者", Width = 20 },
            new TableColumn { Header = "方向", Width = 4 },
            new TableColumn { Header = "来源", Width = 10, Priority = Priority.P1, ShowFromWidth = 96 },
            new TableColumn { Header = "置信", Width = 6, RightAlign = true, Priority = Priority.P2, ShowFromWidth = 96 },
            new TableColumn { Header = "@反指", Width = 18, Priority = Priority.P3, ShowFromWidth = 120 },
            new TableColumn { Header = "依据", Flexible = true, Min = 10, Priority = Priority.P1 });

        var rows = new List<string[]>
        {
            new[] { "★7", "13:00", "240岁是魔法使", "↔", "入口", "1.00", "@雾雨梦灵/@龙胆紫苑", "入口帖（双向起点）" },
            new[] { "8", "13:30", "一个很长很长很长的昵称", "→", "反指", "0.75", "@龙胆紫苑", "仅昵称匹配" },
        };
        var lines = table.Render(width, rows, i => i == 1 ? Role.Selected : Role.Normal, Theme.None);
        return string.Join("\n", lines) + "\n";
    }

    private static void Verify(string fileName, string actual)
    {
        if (!Directory.Exists(SnapshotDir)) Directory.CreateDirectory(SnapshotDir);
        string path = Path.Combine(SnapshotDir, fileName);
        string normalized = actual.Replace("\r\n", "\n");

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1" || !File.Exists(path))
        {
            File.WriteAllText(path, normalized);
            return;
        }

        string expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(expected == normalized, $"{fileName} 与快照不一致（有意改动请用 UPDATE_SNAPSHOTS=1 重录）");
    }
}
