using System.Text;
using JieLi.Tui.Text;
using JieLi.Tui.Tui;
using JieLi.Tui.Tui.Rendering;

namespace JieLi.Tui.Tests;

/// <summary>
/// Step 2 的排版契约：一帧的行数恒等于视口高度，每行显示宽度不超过视口宽度。
/// 这些不变量是 P0 渲染 bug（重复输出 / 新帧叠旧帧）不被重新引入的护栏。
/// </summary>
public class RenderLayoutTests
{
    private static (ConsoleHost Host, StringWriter Writer) MakeHost(int w, int h)
    {
        var sw = new StringWriter();
        return (new ConsoleHost(sw, w, h, interactive: true), sw);
    }

    private static string[] FrameLines(StringWriter sw)
    {
        var text = sw.ToString();
        int end = text.LastIndexOf("\u001b[J", StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        // 帧尾的逐行 EL 也去掉，剩下纯行文本
        text = text.Replace("\u001b[K", string.Empty);
        return text.Replace("\r", string.Empty).Split('\n');
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(140, 40)]
    public void FrameBuilder_FillsExactlyViewportHeight(int w, int h)
    {
        var (host, sw) = MakeHost(w, h);
        host.BeginFrame();
        var f = new FrameBuilder(host, Theme.None, w, h);
        f.Header("标题", "副标题");
        f.Body("主体一行");
        f.Toast(null);
        f.Keys(("Esc", "返回"), ("Q", "退出"));
        f.Render();
        host.EndFrame();

        var lines = FrameLines(sw);
        Assert.Equal(h, lines.Length);
        // 底部两行固定：倒数第二是 toast（空行占位），最后一行是键位。
        Assert.Contains("返回", lines[^1]);
        Assert.Equal(string.Empty, lines[^2]);
    }

    [Fact]
    public void FrameBuilder_TruncatesBodyBeyondHeight()
    {
        var (host, sw) = MakeHost(60, 12);
        host.BeginFrame();
        var f = new FrameBuilder(host, Theme.None, 60, 12);
        f.Header("t");
        for (int i = 0; i < 100; i++) f.Body("line " + i);
        f.Render();
        host.EndFrame();

        var lines = FrameLines(sw);
        Assert.Equal(12, lines.Length);
        Assert.DoesNotContain(lines, l => l.Contains("line 99"));
    }

    [Fact]
    public void FrameBuilder_ClipsOverlongLines()
    {
        var (host, sw) = MakeHost(40, 10);
        host.BeginFrame();
        var f = new FrameBuilder(host, Theme.None, 40, 10);
        f.Header("标题：" + new string('长', 80));
        f.Body(new string('文', 100));
        f.Render();
        host.EndFrame();

        foreach (var line in FrameLines(sw))
        {
            Assert.True(TextWidth.Measure(line) <= 40, $"行宽越界：{line}");
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(140)]
    [InlineData(200)]
    public void TableBuilder_NeverExceedsTerminalWidth(int width)
    {
        var table = new TableBuilder(
            new TableColumn { Header = "棒", Width = 5, RightAlign = true },
            new TableColumn { Header = "时段", Width = 6 },
            new TableColumn { Header = "创作者", Width = 18 },
            new TableColumn { Header = "方向", Width = 4 },
            new TableColumn { Header = "来源", Width = 10, Priority = Priority.P1, ShowFromWidth = 96 },
            new TableColumn { Header = "置信", Width = 6, RightAlign = true, Priority = Priority.P2, ShowFromWidth = 96 },
            new TableColumn { Header = "@反指", Width = 18, Priority = Priority.P3, ShowFromWidth = 120 },
            new TableColumn { Header = "依据", Flexible = true, Min = 10, Priority = Priority.P1 });

        var rows = new List<string[]>
        {
            new[] { "★7", "13:00", "一个非常非常非常长的创作者昵称", "↔", "入口", "1.00", "@somebody/@another", new string('文', 200) },
            new[] { "12", "09:30", "短名", "→", "反指", "0.75", string.Empty, "短依据" },
        };

        var lines = table.Render(width, rows, _ => null, null);
        foreach (var line in lines)
        {
            Assert.True(TextWidth.Measure(line) <= width, $"{width} 列下越界（{TextWidth.Measure(line)}）：{line}");
        }
    }

    [Fact]
    public void Theme_Ansi16_UsesPairedOffCodes()
    {
        var theme = Theme.ForDepth(Theme.Depth.Ansi16);
        var warning = theme.Warning("x");
        Assert.Contains("\u001b[33m", warning);
        Assert.DoesNotContain("\u001b[0m", warning);   // 不能用 Reset：会把外层的选中反显一起清掉
        Assert.Contains("\u001b[39m", warning);

        var selected = theme.Selected(theme.Warning("x"));
        Assert.Contains("\u001b[7m", selected);
        Assert.Contains("\u001b[27m", selected);
        Assert.EndsWith("\u001b[27m", selected);
    }

    [Fact]
    public void Theme_NoColor_ReturnsPlainText()
    {
        Theme.ForceNoColor = true;
        try
        {
            var theme = Theme.Detect();
            Assert.False(theme.HasColor);
            Assert.Equal("警告", theme.Warning("警告"));
        }
        finally
        {
            Theme.ForceNoColor = false;
        }
    }

    [Fact]
    public void ConsoleHost_AltScreen_WritesEnterAndLeave()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 80, 24, interactive: true);
        host.EnterAltScreen();
        host.ExitAltScreen();

        var text = sw.ToString();
        Assert.Contains("\u001b[?1049h", text);
        Assert.Contains("\u001b[?1049l", text);
    }

    [Fact]
    public void PlainHost_EmitsNoAnsi()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 80, 24, interactive: false);
        host.EnterAltScreen();   // 非交互必须忽略
        host.BeginFrame();
        host.Line("\u001b[31m红色\u001b[0m");
        host.EndFrame();

        Assert.DoesNotContain("\u001b[?1049h", sw.ToString());
        Assert.Contains("红色", sw.ToString());
    }

    [Fact]
    public void SanitizeAscii_RewritesAmbiguousSymbols()
    {
        string s = ConsoleHost.SanitizeAscii("★入口 → ← ↔ · … ─ ⚠");
        foreach (var ch in new[] { "★", "→", "←", "↔", "·", "…", "─", "⚠" })
            Assert.DoesNotContain(ch, s);
        Assert.Contains("*", s);
        Assert.Contains(">", s);
        Assert.Contains("...", s);
    }

    [Fact]
    public void AsciiOnly_AppliesToEveryLine()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 40, 6, interactive: false);
        ConsoleHost.AsciiOnly = true;
        try
        {
            host.BeginFrame();
            host.Line("★ ⚠ → ← ↔ · … ─ | 中");
            host.EndFrame();
        }
        finally { ConsoleHost.AsciiOnly = false; }

        string text = sw.ToString();
        foreach (var ch in new[] { "★", "⚠", "→", "←", "↔", "·", "…", "─" })
            Assert.DoesNotContain(ch, text);
        Assert.Contains("中", text);
    }
}
