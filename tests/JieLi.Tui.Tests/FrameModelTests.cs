using JieLi.Tui.Text;
using JieLi.Tui.Tui;

namespace JieLi.Tui.Tests;

/// <summary>
/// 帧模型契约（P0 修复的回归网）：
///   - 只做相对光标回退，绝不发 \u001b[H 绝对归位（绝对归位在帧超高滚动后会落到视口外）；
///   - 每帧行数 ≤ 终端高度、每行显示宽度 ≤ 终端宽度（超宽/超高会被裁掉而不是写穿）；
///   - 末行不带换行，帧尾发 \u001b[J 清掉旧帧更长的尾巴。
/// 这些断言在真实终端里对应的就是「重复输出 / 新旧帧叠加」不再出现。
/// </summary>
public class FrameModelTests
{
    [Fact]
    public void ComposeFrame_EmptyLines_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConsoleHost.ComposeFrame(Array.Empty<string>(), 5));
    }

    [Fact]
    public void ComposeFrame_FirstFrame_DoesNotEmitCursorUp()
    {
        string s = ConsoleHost.ComposeFrame(new[] { "a", "b" }, 0);
        Assert.StartsWith("\r", s);
        Assert.DoesNotContain("\u001b[1A", s);
        Assert.EndsWith("\u001b[J", s);
        Assert.False(s.EndsWith("\n"));
    }

    [Fact]
    public void ComposeFrame_MovesUpByPreviousLineCountMinusOne()
    {
        string s = ConsoleHost.ComposeFrame(new[] { "a", "b" }, 3);
        Assert.StartsWith("\r\u001b[2A", s);
    }

    [Fact]
    public void ClipLine_TruncatesByDisplayWidth_AndClosesColor()
    {
        string s = ConsoleHost.ClipLine("中中中", 5);
        Assert.Equal("中中\u001b[0m", s);
        Assert.Equal(4, TextWidth.Measure(s));
    }

    [Fact]
    public void ClipLine_KeepsAnsiSequences()
    {
        string s = ConsoleHost.ClipLine("\u001b[32mok\u001b[0m", 20);
        Assert.Equal("\u001b[32mok\u001b[0m", s);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(60, 20)]
    public void Frame_NeverExceedsViewport(int width, int height)
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, width, height);

        for (int step = 0; step < 20; step++)
        {
            sw.GetStringBuilder().Clear();
            host.BeginFrame();

            // 明显超出视口的内容：行数 height+5、每行 width+若干列。
            for (int i = 0; i < height + 5; i++)
                host.Line(new string('中', width) + "尾巴尾巴");

            host.EndFrame();

            string frame = sw.ToString();
            Assert.DoesNotContain("\u001b[H", frame);        // 不允许绝对归位
            Assert.EndsWith("\u001b[J", frame);

            string body = frame[..^3];
            body = body.TrimStart('\r');
            if (body.StartsWith("\u001b["))
            {
                int arrow = body.IndexOf('A');
                if (arrow > 0) body = body[(arrow + 1)..];
            }

            string[] lines = body.Split('\n');
            Assert.True(lines.Length <= height, $"第 {step} 帧行数 {lines.Length} > 视口高度 {height}");
            foreach (string line in lines)
                Assert.True(TextWidth.Measure(line) <= width,
                    $"第 {step} 帧有一行宽度 {TextWidth.Measure(line)} > 视口宽度 {width}：[{line.TrimEnd()}]");
        }
    }

    // ---------------------------------------------------------------------
    // 备用屏内的帧：绝对归位（不进滚动历史、不受上一帧行数影响）
    // ---------------------------------------------------------------------

    [Fact]
    public void ComposeFrameAlt_EmptyLines_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConsoleHost.ComposeFrameAlt(Array.Empty<string>()));
    }

    [Fact]
    public void ComposeFrameAlt_HomesAbsolutely_AndClearsTail()
    {
        string s = ConsoleHost.ComposeFrameAlt(new[] { "a", "b" });
        Assert.StartsWith("\u001b[H", s);
        Assert.EndsWith("\u001b[J", s);
        Assert.DoesNotContain("\u001b[1A", s);
    }

    [Fact]
    public void EndFrame_InAltScreen_UsesAbsoluteHoming()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 100, 30);

        host.EnterAltScreen();
        Assert.Contains("\u001b[?1049h", sw.ToString());   // 进备用屏
        Assert.Contains("\u001b[?7l", sw.ToString());      // 关自动换行：行宽差 1 列也不会折行

        sw.GetStringBuilder().Clear();
        for (int i = 0; i < 3; i++)
        {
            host.BeginFrame();
            host.Line("第一行");
            host.Line("第二行");
            host.EndFrame();
        }

        string all = sw.ToString();
        Assert.Contains("\u001b[H", all);
        // 备用屏完全不需要相对回退：出现了上移就说明选了错的帧协议。
        Assert.DoesNotContain("\u001b[1A", all);
        Assert.DoesNotContain("\u001b[2A", all);
    }

    [Fact]
    public void EnterAndExitAltScreen_RestoreWrapAndScreen()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 80, 24);
        host.EnterAltScreen();
        host.ExitAltScreen();
        string s = sw.ToString();
        Assert.Contains("\u001b[?7h", s);     // 退出时恢复自动换行
        Assert.Contains("\u001b[?1049l", s);
    }
}
