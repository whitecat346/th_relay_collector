#nullable enable
using System.Text;
using JieLi.Tui.Text;

namespace JieLi.Tui.Tui;

/// <summary>
/// ANSI 终端渲染底座。
/// 两种模式：
///   - 交互模式（stdout 非重定向且无参数）：内存组帧 + 相对光标回退 + 按视口裁剪
///   - 纯文本模式（stdout 重定向 / --dump）：顺序打印，便于自动化验收
/// 样式/颜色能力检测与语义色板在 Tui/Rendering/Theme.cs，排版组件在 FrameBuilder/TableBuilder。
///
/// 帧不变量（P0 修复的核心）：
///   1) 每帧行数 ≤ 终端高度、每行显示宽度 ≤ 终端宽度，写入不引起滚动（或滚动后整帧仍在视口内）；
///   2) 帧与帧之间只用相对移动（\r + 上移 n 行），绝不用 \u001b[H 绝对归位 ——
///      帧一旦超高，终端会滚动，绝对归位便落到视口上方，上一帧残留在下方，\u001b[J 也清不掉，
///      于是出现「重复输出 / 新帧叠在旧帧上」；
///   3) 末行不发换行，光标停在帧尾，下次从帧尾相对上移 (行数-1) 即回到帧首。
/// </summary>
public sealed class ConsoleHost
{
    private readonly bool _interactive;
    private readonly TextWriter _out;
    /// <summary>true = 主动探测 Console 尺寸（真实运行）；false = 使用给定尺寸（测试/嵌入）。</summary>
    private readonly bool _probeConsole;
    private readonly List<string> _lines = new();
    private int _prevLines;      // 上一帧实际写入的行数（相对回退的锚）
    private int _width = 80;
    private int _height = 24;
    private int _lastW;
    private int _lastH;
    private bool _cursorHidden;

    public ConsoleHost(bool forcePlain)
    {
        _interactive = !forcePlain && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        _out = Console.Out;
        _probeConsole = true;
        try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); } catch { /* 重定向时可能不支持 */ }
        try { Console.CursorVisible = false; } catch { /* ignore */ }
        // 关闭自动换行：行宽只要差 1 列，终端就会把该行折成两行，帧高随之失真，
        // 内联模式的相对回退和备用屏的行数都会错位。退出时在 Restore() 里恢复。
        if (_interactive)
        {
            try { _out.Write("[?7l"); _out.Flush(); } catch { /* ignore */ }
        }
        if (_interactive)
        {
            try
            {
                _width = ForcedWidth is int fw ? Math.Max(20, fw) : Math.Max(20, Console.WindowWidth);
                _height = ForcedHeight is int fh ? Math.Max(8, fh) : Math.Max(8, Console.WindowHeight);
            }
            catch { /* 保持默认 80x24 */ }
        }
    }

    /// <summary>
    /// 测试/嵌入用：显式给定输出与视口尺寸，驱动与真实运行完全相同的帧路径，
    /// 但不再探测控制台、不改光标可见性（可在无 TTY 环境下对每帧做断言）。
    /// </summary>
    public ConsoleHost(TextWriter output, int width, int height, bool interactive = true)
    {
        _out = output;
        _interactive = interactive;
        _probeConsole = false;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _lastW = _width;
        _lastH = _height;
    }

    // ---- 颜色（ANSI 转义只在交互模式下发） ----
    private string C(string code) => _interactive ? code : string.Empty;

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

    public bool Interactive => _interactive;

    /// <summary>--width / --height：固定视口尺寸（截图、快照、自动化验收）。null = 跟随终端。</summary>
    public static int? ForcedWidth { get; set; }
    public static int? ForcedHeight { get; set; }

    /// <summary>--no-alt-screen：false 时交互模式仍用备用屏。</summary>
    public static bool AltScreenEnabled { get; set; } = true;

    /// <summary>--ascii：把 East Asian Ambiguous 字符（★ ⚠ → ← ↔ · … ─）降级成纯 ASCII，
    /// 供把 ambiguous 记成 2 列的终端使用（宽度口径按 1 列，替换后仍是 1 列）。</summary>
    public static bool AsciiOnly { get; set; }

    private bool _altScreen;

    /// <summary>
    /// 进入备用屏（CSI ?1049h）：交互视图独占一屏，退出后原终端内容原样恢复，
    /// 不会把 TUI 的帧留在用户的滚动记录里。不支持 1049 的旧 conhost 会忽略该序列。
    /// </summary>
    public void EnterAltScreen()
    {
        if (!_interactive || _altScreen || !AltScreenEnabled) return;
        // ?7l 关掉自动换行：只要有一行比终端宽一点点，终端也不会把它折成两行；
        // 帧高一旦失真，整帧会被顶上去，表现就是「页面不刷新 / 内容叠加」。
        _out.Write("\u001b[?1049h\u001b[?7l");
        _out.Write("\u001b[2J\u001b[H");
        _out.Flush();
        _altScreen = true;
        _prevLines = 0;
    }

    public void ExitAltScreen()
    {
        if (!_altScreen) return;
        _out.Write("\u001b[?7h\u001b[?1049l");
        _out.Flush();
        _altScreen = false;
    }

    /// <summary>本帧可用的显示宽度（交互模式取实时终端宽度，非交互固定 80）。</summary>
    /// <summary>最近一帧写入终端的原始文本（测试断言用；空帧为 null）。</summary>
    public string? LastFrameText { get; private set; }

    public int Width => _width;

    /// <summary>本帧可用的显示高度（交互模式取实时终端高度）。</summary>
    public int Height => _height;

    public void BeginFrame()
    {
        _lines.Clear();
        if (!_interactive)
        {
            // 非交互（--plain / 重定向）也要响应 --width/--height：
            // 否则自动化验收永远按 80 列渲染，快照覆盖不到宽终端。
            _width = ForcedWidth is int fw ? Math.Max(20, fw) : 100;
            _height = ForcedHeight is int fh ? Math.Max(8, fh) : 40;
            return;
        }

        if (_probeConsole)
        {
            try
            {
                int w = ForcedWidth is int fw ? Math.Max(20, fw) : Math.Max(20, Console.WindowWidth);
                int h = ForcedHeight is int fh ? Math.Max(8, fh) : Math.Max(8, Console.WindowHeight);
                if (_lastW > 0 && (w != _lastW || h != _lastH))
                {
                    // 尺寸变化：整屏重画，避免旧宽度的残留行错位。
                    _out.Write("\u001b[2J\u001b[H");
                    _prevLines = 0;
                }
                _width = w;
                _height = h;
            }
            catch { /* 保持上次尺寸 */ }
        }

        if (!_cursorHidden)
        {
            _out.Write("\u001b[?25l");
            _cursorHidden = true;
        }
        _lastW = _width;
        _lastH = _height;
    }

    public void Line(string? text = "")
    {
        text ??= string.Empty;
        if (AsciiOnly) text = SanitizeAscii(text);
        if (!_interactive)
        {
            _lines.Add(text);
            return;
        }

        // 帧高上限：多余行丢弃（视图应自行按可用高度裁剪，这里只是兜底）。
        if (_lines.Count >= _height) return;
        _lines.Add(ClipLine(text, _width));
    }

    public void EndFrame()
    {
        if (!_interactive)
        {
            if (_lines.Count > 0) _out.Write(string.Join('\n', _lines) + "\n");
            _out.Flush();
            return;
        }
        if (_lines.Count == 0) return;

        // 备用屏没有滚动历史，帧高受 Line() 硬限制（≤ 视口高），所以绝对归位是安全的，
        // 而且不受上一帧光标位置/外部写入影响 —— 这是「按了键页面不刷新」的根因修法。
        string frame = _altScreen ? ComposeFrameAlt(_lines) : ComposeFrame(_lines, _prevLines);
        LastFrameText = frame;
        _out.Write(frame);
        _out.Flush();
        _prevLines = _lines.Count;
    }

    /// <summary>
    /// 备用屏内的一帧：绝对归位到左上角，写完清到屏幕末尾。
    /// 与 <see cref="ComposeFrame"/> 的区别是不依赖上一帧行数，因此不会因为中途有
    /// 外部写屏（例如 ReadLine、异常栈）而把后续帧画到错误的位置。
    /// </summary>
    public static string ComposeFrameAlt(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return string.Empty;
        // 每行收尾都发 EL（清到行尾）：新帧某行比旧帧短时，旧行尾巴不会残留在它右边。
        // 只在整帧末尾发 ED 不够——ED 只从光标往下清，光标以上的行尾巴谁都管不到。
        return "\u001b[H" + string.Join("\u001b[K\r\n", lines) + "\u001b[K\u001b[J";
    }

    /// <summary>
    /// 把一帧拼成写入终端的字符串（纯函数，便于单测）。
    /// prevLines = 上一帧的行数；0 表示没有可回退的旧帧（首帧或视图刚直接写过控制台）。
    /// </summary>
    public static string ComposeFrame(IReadOnlyList<string> lines, int prevLines)
    {
        if (lines.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.Append('\r');                                   // 回到行首
        if (prevLines > 1) sb.Append("\u001b[").Append(prevLines - 1).Append('A');  // 相对上移，滚动安全
        sb.Append(string.Join("\u001b[K\r\n", lines));     // 每行清到行尾；用 CRLF，避免个别终端 LF 不回车
        sb.Append("\u001b[K");
        sb.Append("\u001b[J");                             // 清到屏幕末尾，覆盖旧帧更长的尾巴
        return sb.ToString();
    }

    /// <summary>
    /// 按显示宽度裁剪一行，保留 ANSI 转义序列（不占列）。
    /// 视图层不必再逐处 Truncate：只要经过 Line() 就不会撑破行宽。
    /// 宽度计算（含 ZWJ emoji 聚合）统一由 <see cref="TextWidth.ClipVisible"/> 负责。
    /// </summary>
    /// <summary>把常被终端算成「宽」的符号换成 ASCII（--ascii 兜底）。替换在裁剪之前做，宽度仍然准。</summary>
    public static string SanitizeAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '─': case '━': case '—': case '–': case '│': case '┃': sb.Append('-'); break;
                case '★': case '☆': sb.Append('*'); break;
                case '⚠': sb.Append('!'); break;
                case '→': sb.Append('>'); break;
                case '←': sb.Append('<'); break;
                case '↔': sb.Append('~'); break;
                case '·': sb.Append('.'); break;
                case '…': sb.Append("..."); break;
                case '✓': sb.Append('v'); break;
                case '✗': case '×': sb.Append('x'); break;
                case '“': case '”': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '【': case '「': sb.Append('['); break;
                case '】': case '」': sb.Append(']'); break;
                case '、': sb.Append(','); break;
                case '　': sb.Append(' '); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    public static string ClipLine(string text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return string.Empty;

        string clipped = TextWidth.ClipVisible(text, width, out bool wasClipped);
        if (wasClipped) clipped += "\u001b[0m";   // 被截断时收束挂起的颜色，避免染到整屏
        return clipped;
    }

    public static int WindowWidth =>
        ForcedWidth is int fw ? Math.Max(20, fw)
        : Console.IsOutputRedirected ? 100
        : Probe(() => Math.Max(20, Console.WindowWidth), 120);

    public static int WindowHeight =>
        ForcedHeight is int fh ? Math.Max(8, fh)
        : Console.IsInputRedirected || Console.IsOutputRedirected ? 40
        : Probe(() => Math.Max(8, Console.WindowHeight), 40);

    private static int Probe(Func<int> read, int fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    /// <summary>
    /// 视图绕过帧模型直接写了控制台（Console.ReadLine 等）之后调用：
    /// 帧的相对锚点已经失效，下一帧改为就地重画，而不是按上一帧行数往回跳。
    /// </summary>
    public void InvalidateFrame() => _prevLines = 0;

    public void Restore()
    {
        if (!_interactive) return;
        if (_probeConsole)
        {
            try { Console.CursorVisible = true; } catch { }
        }
        _out.Write("\u001b[0m");
        ExitAltScreen();
        _out.Flush();
        _cursorHidden = false;
    }

    /// <summary>水平分隔线（按显示宽度铺满）。</summary>
    public static string Rule(int width, char ch = '─') => new(ch, Math.Max(1, width));
}
