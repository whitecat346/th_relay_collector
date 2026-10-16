#nullable enable
using System.Text;

namespace JieLi.Tui.Tui;

/// <summary>
/// 无依赖的 ANSI 终端渲染底座（不引入 Spectre.Console：NuGet 缓存里没有，需联网拉包）。
/// 支持两种模式：
///   - 交互模式（stdout 非重定向且无参数）：清屏 + 光标定位 + 颜色
///   - 纯文本模式（stdout 重定向 / --dump）：顺序打印，便于自动化验收
/// </summary>
public sealed class ConsoleHost
{
    public bool Interactive { get; }
    private readonly StringBuilder _frame = new();

    public ConsoleHost(bool forcePlain)
    {
        Interactive = !forcePlain && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); } catch { /* 重定向时可能不支持 */ }
        try { Console.CursorVisible = false; } catch { /* ignore */ }
    }

    // ---- 颜色（ANSI 转义只在交互模式下发） ----
    private string C(string code) => Interactive ? code : string.Empty;

    public string Reset => C("\u001b[0m");
    public string Bold => C("\u001b[1m");
    public string Dim => C("\u001b[2m");
    public string Red => C("\u001b[31m");
    public string Green => C("\u001b[32m");
    public string Yellow => C("\u001b[33m");
    public string Blue => C("\u001b[34m");
    public string Magenta => C("\u001b[35m");
    public string Cyan => C("\u001b[36m");
    public string Inverse => C("\u001b[7m");

    public void BeginFrame()
    {
        _frame.Clear();
    }

    public void Line(string text = "")
    {
        _frame.Append(text).Append('\n');
    }

    public void EndFrame()
    {
        if (Interactive)
        {
            var sb = new StringBuilder();
            sb.Append("\u001b[?25l");   // 隐藏光标
            sb.Append("\u001b[H");      // 光标回家
            sb.Append(_frame);
            sb.Append("\u001b[J");      // 清到屏幕末尾（覆盖上一帧残留）
            Console.Write(sb.ToString());
        }
        else
        {
            Console.Write(_frame.ToString());
        }
        Console.Out.Flush();
    }

    public static int WindowWidth
    {
        get
        {
            try { return Math.Max(60, Console.WindowWidth); }
            catch { return 120; }
        }
    }

    public static int WindowHeight
    {
        get
        {
            try { return Math.Max(12, Console.WindowHeight); }
            catch { return 40; }
        }
    }

    public void Restore()
    {
        if (!Interactive) return;
        try { Console.CursorVisible = true; } catch { }
        Console.Write("\u001b[0m");
    }

    /// <summary>水平分隔线（按显示宽度铺满）。</summary>
    public static string Rule(int width, char ch = '─') => new(ch, Math.Max(1, width));
}
