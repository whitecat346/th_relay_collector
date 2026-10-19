using JieLi.Tui.Text;
using JieLi.Tui.Tui;
using JieLi.Tui.Tui.Rendering;

namespace JieLi.Tui.Tests;

/// <summary>
/// 把一串帧字符串回放到一块虚拟屏幕上，逐格断言「屏幕上最后留下的就是最后一帧」。
/// 这是「页面不刷新 / 叠字 / 尾巴残留」这类 bug 的直接回归测试：
/// 只比较帧字符串查不出残留——残留发生在**旧帧比新帧长**的那些行尾。
/// </summary>
internal sealed class VirtualScreen
{
    private const char WideFiller = '\u0001';
    private readonly int _width;
    private readonly int _height;
    private readonly char[][] _cells;
    private int _row;
    private int _col;

    public VirtualScreen(int width, int height)
    {
        _width = width;
        _height = height;
        _cells = new char[height][];
        for (int i = 0; i < height; i++) _cells[i] = new string(' ', width).ToCharArray();
    }

    public IReadOnlyList<string> RowText()
    {
        var list = new List<string>(_height);
        foreach (char[] row in _cells) list.Add(new string(row).Replace(WideFiller.ToString(), string.Empty));
        return list;
    }

    /// <summary>按终端语义回放：H 绝对归位、A 上移、K 清到行尾、J 清到屏尾、\n 换行、可见字符按下标写入。</summary>
    public void Apply(string frame)
    {
        for (int i = 0; i < frame.Length; i++)
        {
            char ch = frame[i];
            if (ch == '\u001b')
            {
                i = ApplyCsi(frame, i);
                continue;
            }
            if (ch == '\r') { _col = 0; continue; }
            if (ch == '\n')
            {
                _col = 0;
                _row = Math.Min(_row + 1, _height - 1);
                continue;
            }
            int width = TextWidth.ClusterWidth(ch.ToString());
            if (width <= 0) continue;
            if (_col < _width)
            {
                _cells[_row][_col] = ch;
                // 宽字符占两格：第二格记成占位符，读回时跳过（否则 CJK 比对会多出空格）
                for (int k = 1; k < width && _col + k < _width; k++) _cells[_row][_col + k] = WideFiller;
            }
            _col += width;
        }
    }

    private int ApplyCsi(string s, int i)
    {
        int j = i + 1;
        if (j >= s.Length || s[j] != '[') return j;
        j++;
        int paramStart = j;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == ';' || s[j] == '?')) j++;
        if (j >= s.Length) return j;
        string param = s[paramStart..j];
        char final = s[j];
        string[] parts = param.TrimStart('?').Split(';', StringSplitOptions.RemoveEmptyEntries);
        int First(int fallback) => parts.Length > 0 && int.TryParse(parts[0], out int v) ? v : fallback;

        switch (final)
        {
            case 'H':
            case 'f':
                _row = Math.Clamp(First(1) - 1, 0, _height - 1);
                _col = Math.Clamp((parts.Length > 1 ? First(1) : 1) - 1, 0, _width - 1);
                break;
            case 'A': _row = Math.Max(0, _row - First(1)); break;
            case 'B': _row = Math.Min(_height - 1, _row + First(1)); break;
            case 'C': _col = Math.Min(_width - 1, _col + First(1)); break;
            case 'D': _col = Math.Max(0, _col - First(1)); break;
            case 'K':
                for (int c = _col; c < _width; c++) _cells[_row][c] = ' ';
                break;
            case 'J':
                for (int c = _col; c < _width; c++) _cells[_row][c] = ' ';
                for (int r = _row + 1; r < _height; r++)
                    for (int c = 0; c < _width; c++) _cells[r][c] = ' ';
                break;
        }
        return j;
    }

    /// <summary>把屏幕与期望帧比对（忽略行尾空白与 ANSI）。</summary>
    public void AssertMatches(IReadOnlyList<string> expected, string note = "")
    {
        IReadOnlyList<string> actual = RowText();
        for (int r = 0; r < _height; r++)
        {
            string want = r < expected.Count ? TextWidth.StripAnsi(expected[r]).TrimEnd() : string.Empty;
            string got = actual[r].TrimEnd();
            Assert.True(want == got, $"{note} 第 {r + 1} 行不一致：期望「{want}」实际「{got}」");
        }
    }
}

public sealed class ScreenSimTests
{
    private const string Work = @"D:\东方接力收集\relay-tool\work";
    private const string Slug = "2026_7月26日卡娜日_07-26";

    private static ConsoleKeyInfo K(ConsoleKey key, char c = '\0') => new(c, key, false, false, false);

    private static List<string> FrameLines(string frame) =>
        frame.Replace("\u001b[J", string.Empty)
            .Replace("\u001b[K", string.Empty)
            .Replace("\u001b[H", string.Empty)
            .Split('\n')
            .ToList();

    /// <summary>祖先用例：新帧某行比旧帧短时，旧尾巴必须被清掉（「叠字」的最小复现）。</summary>
    [Fact]
    public void ShorterLine_ClearsPreviousLongerLine()
    {
        var screen = new VirtualScreen(20, 5);
        screen.Apply(ConsoleHost.ComposeFrameAlt(new[] { "AAAAAAAAAA", "BBBBBBBBBB" }));
        screen.Apply(ConsoleHost.ComposeFrameAlt(new[] { "A", "B" }));
        screen.AssertMatches(new[] { "A", "B" });
    }

    /// <summary>
    /// 反向对照：帧里没有逐行 EL 时，**最后一行之上**的行尾会残留（帧尾 ED 只从光标往下清）。
    /// 保证上面的断言确实能抓到残留，而不是恒真。
    /// </summary>
    [Fact]
    public void WithoutPerLineEl_TailsRemain()
    {
        var screen = new VirtualScreen(20, 5);
        screen.Apply("\u001b[HAAAAAAAAAA\nBBBBBBBBBB\u001b[J");
        screen.Apply("\u001b[HA\nB\u001b[J");
        Assert.Equal("AAAAAAAAAA", screen.RowText()[0].TrimEnd());
        Assert.Equal("B", screen.RowText()[1].TrimEnd());
    }

    /// <summary>连续操作（长输入、逐字退格、切视图）后，屏幕必须逐行等于最后一帧。</summary>
    [Fact]
    public void ScreenAfterLongerThenShorterLine_HasNoResidue()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 100, 30);
        var view = new ReviewView(host, Work, 0, Slug, Slug, Theme.None);
        host.EnterAltScreen();
        var screen = new VirtualScreen(100, 30);

        void Frame(string note)
        {
            host.BeginFrame();
            view.Render(host.Width, host.Height);
            host.EndFrame();
            string frame = host.LastFrameText ?? string.Empty;
            screen.Apply(frame);
            var lines = FrameLines(frame);
            Assert.True(lines.Count <= host.Height, $"{note}: 帧 {lines.Count} 行超过视口 {host.Height}");
            screen.AssertMatches(lines, note);
        }

        Frame("首帧");

        view.HandleKey(K(ConsoleKey.U));
        Frame("进入输入");
        foreach (char c in "一个很长的创作者昵称")
        {
            view.HandleKey(K(ConsoleKey.A, c));
            Frame("输入 " + c);
        }
        for (int i = 0; i < 20; i++)
        {
            view.HandleKey(K(ConsoleKey.Backspace));
            Frame("退格 " + i);
        }
        view.HandleKey(K(ConsoleKey.Escape));
        Frame("取消输入");

        view.HandleKey(K(ConsoleKey.C));
        Frame("链条");
        view.HandleKey(K(ConsoleKey.Escape));
        Frame("回列表");
        view.HandleKey(K(ConsoleKey.Oem2, '?'));
        Frame("帮助");
        view.HandleKey(K(ConsoleKey.Escape));
        Frame("关帮助");
    }

    /// <summary>在列表/缺口/链条/节点详情/全文之间来回切换，屏幕必须始终等于最后一帧。</summary>
    [Fact]
    public void ScreenAfterSwitchBetweenViews_MatchesLastFrame()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 80, 24);
        var view = new ReviewView(host, Work, 0, Slug, Slug, Theme.None);
        host.EnterAltScreen();
        var screen = new VirtualScreen(80, 24);

        foreach (ConsoleKey key in new[]
                 {
                     ConsoleKey.None, ConsoleKey.G, ConsoleKey.Escape, ConsoleKey.C, ConsoleKey.DownArrow,
                     ConsoleKey.Enter, ConsoleKey.Escape, ConsoleKey.Escape, ConsoleKey.V, ConsoleKey.Escape,
                 })
        {
            if (key != ConsoleKey.None) view.HandleKey(K(key));
            host.BeginFrame();
            view.Render(host.Width, host.Height);
            host.EndFrame();
            string frame = host.LastFrameText ?? string.Empty;
            screen.Apply(frame);
            screen.AssertMatches(FrameLines(frame), key.ToString());
        }
    }
}
