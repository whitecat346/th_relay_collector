using JieLi.Tui.Text;

namespace JieLi.Tui.Tests;

/// <summary>
/// 显示宽度语义的回归测试：这些值决定了表格对不对齐、行会不会撑破视口。
/// 宽度表来自 Wcwidth；若升级包后 Unicode 数据变了，这些断言会最先报警。
/// </summary>
public class TextWidthTests
{
    [Fact]
    public void Chinese_CountsTwoColumns()
    {
        Assert.Equal(4, TextWidth.Measure("中文"));
    }

    [Fact]
    public void AnsiSequences_DoNotOccupyColumns()
    {
        Assert.Equal(2, TextWidth.Measure("\u001b[31m中\u001b[0m"));
        Assert.Equal(2, TextWidth.Measure("\u001b[1mab\u001b[0m"));
    }

    [Fact]
    public void ZwjEmoji_CountsAsOneCluster()
    {
        // 👨‍👩‍👧 由 3 个 emoji + 2 个 ZWJ 组成；按码点求和是 6 列，按字素簇是 2 列。
        Assert.Equal(2, TextWidth.Measure("👨‍👩‍👧"));
    }

    [Theory]
    [InlineData('★')]
    [InlineData('⚠')]
    [InlineData('→')]
    [InlineData('·')]
    public void EastAsianAmbiguous_IsOneColumn_MatchingWindowsTerminal(char c)
    {
        Assert.Equal(1, TextWidth.RuneWidth(c));
    }

    [Fact]
    public void Tab_IsFourColumns()
    {
        Assert.Equal(4, TextWidth.Measure("\t"));
        Assert.Equal(5, TextWidth.Measure("a\t"));
    }

    [Fact]
    public void ControlChars_AreZeroColumns()
    {
        Assert.Equal(0, TextWidth.Measure("\u0007"));
        Assert.Equal(0, TextWidth.Measure("\r"));
    }

    [Fact]
    public void Pad_UsesDisplayWidth()
    {
        Assert.Equal("中文  ", TextWidth.Pad("中文", 6));
        Assert.Equal("  中文", TextWidth.PadLeft("中文", 6));
        Assert.Equal("中文", TextWidth.Pad("中文", 3));
    }

    [Fact]
    public void Truncate_DoesNotSplitWideChar_AndFitsBudget()
    {
        string s = TextWidth.Truncate("中中中", 5);
        Assert.Equal("中中…", s);
        Assert.True(TextWidth.Measure(s) <= 5);
    }

    [Fact]
    public void ClipVisible_PreservesAnsi_AndClipsToWidth()
    {
        string s = TextWidth.ClipVisible("\u001b[31mabcdef\u001b[0m", 3, out bool clipped);
        Assert.True(clipped);
        Assert.Equal("\u001b[31mabc", s);
        Assert.Equal(3, TextWidth.Measure(s));
    }

    [Fact]
    public void ClipVisible_DoesNotSplitWideChar()
    {
        string s = TextWidth.ClipVisible("中中", 3, out bool clipped);
        Assert.True(clipped);
        Assert.Equal("中", s);
    }

    [Fact]
    public void ClipVisible_ExpandsTab_ToTabStops()
    {
        string s = TextWidth.ClipVisible("a\tb", 20, out bool clipped);
        Assert.False(clipped);
        Assert.Equal("a   b", s);
    }

    [Fact]
    public void ClipVisible_NoClip_WhenFits()
    {
        string s = TextWidth.ClipVisible("中文", 4, out bool clipped);
        Assert.False(clipped);
        Assert.Equal("中文", s);
    }

    [Fact]
    public void Wrap_BreaksByDisplayWidth()
    {
        var lines = TextWidth.Wrap(new string('中', 25), 10);
        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.True(TextWidth.Measure(l) <= 10));
        Assert.Equal(50, lines.Sum(TextWidth.Measure));
    }

    [Fact]
    public void Wrap_PrefersSpaces()
    {
        var lines = TextWidth.Wrap("alpha beta gamma delta", 12);
        Assert.All(lines, l => Assert.True(TextWidth.Measure(l) <= 12));
        Assert.DoesNotContain(lines, l => l.StartsWith(' '));
        Assert.Equal("alpha beta", lines[0]);
    }

    [Fact]
    public void Wrap_HonoursExplicitNewlines()
    {
        var lines = TextWidth.Wrap(string.Concat("a", (char)10, "b", (char)10, (char)10, "c"), 20);
        Assert.Equal(new[] { "a", "b", string.Empty, "c" }, lines);
    }
}
