using JieLi.Tui.Text;
using JieLi.Tui.Tui;
using JieLi.Tui.Tui.Rendering;

namespace JieLi.Tui.Tests;

/// <summary>
/// 回归：任何操作（改字段、忽略、开面板）之后，下一帧必须是完整的一整帧。
/// 曾经的 bug 根因是编辑走了 Console.ReadLine：先 Restore() 退出备用屏，
/// 之后只 InvalidateFrame() 就继续 —— 后续帧既不在备用屏、又依赖相对上移，
/// 于是整帧错位，表现为「按了键页面不刷新 / 内容叠加」。
/// 这里在文本宿主上跑完整的 Render→HandleKey→Render 循环来锁住行为。
/// </summary>
public class ReviewViewInputTests : IDisposable
{
    private readonly string _root;

    public ReviewViewInputTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jieli-review-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "123"));
        File.WriteAllText(Path.Combine(_root, "123", "review.jsonl"),
            "{\"card_id\":\"1\",\"bar\":7,\"declared_time\":\"13:00\",\"poster_name\":\"甲\"," +
            "\"resolved_author\":\"甲\",\"opus_url\":\"https://www.bilibili.com/opus/1\"," +
            "\"type\":\"opus\",\"type_label\":\"动态\"}\n" +
            "{\"card_id\":\"2\",\"bar\":8,\"declared_time\":\"14:00\",\"poster_name\":\"乙\"," +
            "\"resolved_author\":\"乙\",\"opus_url\":\"https://www.bilibili.com/opus/2\"," +
            "\"type\":\"opus\",\"type_label\":\"动态\"}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0')
        => new(ch, key, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Ch(char ch)
        => new(ch, ConsoleKey.A, false, false, false);

    private static string RunFrame(ConsoleHost host, ReviewView view)
    {
        host.BeginFrame();
        view.Render(host.Width, host.Height);
        host.EndFrame();
        return host.LastFrameText ?? string.Empty;
    }

    [Fact]
    public void EditField_ThenNextFrame_IsComplete()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 100, 30);
        var view = new ReviewView(host, _root, 123, "测试活动", theme: Theme.None);

        string first = RunFrame(host, view);
        Assert.Contains("人工确认", first);

        view.HandleKey(Key(ConsoleKey.U));          // 创作者 → 帧内输入
        view.HandleKey(Ch('a'));
        view.HandleKey(Ch('b'));
        view.HandleKey(Ch('c'));
        view.HandleKey(Key(ConsoleKey.Enter));

        string second = RunFrame(host, view);
        Assert.Contains("人工确认", second);         // 头还在
        Assert.Contains("创作者", second);           // 表头还在
        Assert.Contains("创作者已改为 abc", second); // toast 是提交结果
        Assert.DoesNotContain("▏", second);          // 输入光标已消失
    }

    [Fact]
    public void ReadLineNeverRuns_NoAltScreenExit()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 100, 30);
        var view = new ReviewView(host, _root, 123, "测试活动", theme: Theme.None);

        host.EnterAltScreen();
        RunFrame(host, view);

        view.HandleKey(Key(ConsoleKey.N));   // 备注：曾经走 ReadLine
        view.HandleKey(Ch('x'));
        view.HandleKey(Key(ConsoleKey.Enter));
        view.HandleKey(Key(ConsoleKey.A));   // 补录：曾经走 ReadLine
        view.HandleKey(Key(ConsoleKey.Escape));

        string all = sw.ToString();
        Assert.DoesNotContain("\u001b[?1049l", all);   // 会话中途绝不离开备用屏
        Assert.DoesNotContain("\u001b[?25h", all);     // 光标也不在帧中途放出

        string frame = RunFrame(host, view);
        Assert.Contains("人工确认", frame);
    }

    [Fact]
    public void InvalidBarEdit_DoesNotArmDirtyQuitGuard()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 100, 30);
        var view = new ReviewView(host, _root, 123, "测试活动", theme: Theme.None);

        view.HandleKey(Key(ConsoleKey.B));
        view.HandleKey(Ch('x'));
        view.HandleKey(Key(ConsoleKey.Enter));   // 无效数字：未修改
        view.HandleKey(Key(ConsoleKey.Q));

        Assert.True(view.ShouldExit);            // 没有未保存改动 → Q 直接退
    }

    [Fact]
    public void ValidEdit_ArmsDirtyQuitGuard()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 100, 30);
        var view = new ReviewView(host, _root, 123, "测试活动", theme: Theme.None);

        view.HandleKey(Key(ConsoleKey.T));
        view.HandleKey(Ch('2'));
        view.HandleKey(Ch('1'));
        view.HandleKey(Key(ConsoleKey.Enter));

        string frame = RunFrame(host, view);
        Assert.Contains("时段已改为 21", frame);
        view.HandleKey(Key(ConsoleKey.Q));
        Assert.False(view.ShouldExit);           // 第一次 Q 只警告

        string warned = RunFrame(host, view);
        Assert.Contains("再按 Q 放弃并退出", warned);
        view.HandleKey(Key(ConsoleKey.Q));
        Assert.True(view.ShouldExit);            // 第二次才退出
    }

    [Fact]
    public void LongInput_ShowsTail_NotHead()
    {
        var sw = new StringWriter();
        var host = new ConsoleHost(sw, 60, 20);
        var view = new ReviewView(host, _root, 123, "测试活动", theme: Theme.None);

        view.HandleKey(Key(ConsoleKey.U));
        for (int i = 0; i < 40; i++) view.HandleKey(Ch((char)('a' + i % 26)));
        view.HandleKey(Ch('Z'));

        string frame = RunFrame(host, view);
        foreach (string line in frame.Split('\n'))
            Assert.True(TextWidth.Measure(line) <= 60, "输入行撑破视口宽：" + line);
        Assert.Contains("Z▏", frame);            // 正在输入的字看得见
    }
}
