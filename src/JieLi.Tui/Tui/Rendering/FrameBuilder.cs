#nullable enable
using System.Text;
using JieLi.Tui.Text;

namespace JieLi.Tui.Tui.Rendering;

/// <summary>
/// 一帧的声明式组装器：固定头部 + 可伸缩主体 + 固定底部两行（toast / 键位）。
///
/// 布局契约（行数恒等于视口高度，交互模式下）：
///   header 行（视图给定）+ 水平分隔线
///   body：吃掉剩余高度（内容不足时补空行，把页脚钉在屏幕底部）
///   水平分隔线 + toast 行（没有内容也占位）+ 键位行
///
/// 视图不再自己数 reserved：主体高度由 <see cref="BodyHeight"/> 给出，
/// 底部永远是「分隔 + toast + 键位」三行，不会再被内容挤掉。
/// </summary>
public sealed class FrameBuilder
{
    private const int FooterLines = 3;   // 分隔 / toast / 键位

    private readonly ConsoleHost _host;
    private readonly List<string> _header = new();
    private readonly List<string> _body = new();
    private readonly List<(string Key, string Label)> _keys = new();
    private string? _toast;

    public FrameBuilder(ConsoleHost host, Theme theme, int width, int height)
    {
        _host = host;
        Theme = theme;
        Width = Math.Max(20, width);
        Height = Math.Max(8, height);
    }

    public Theme Theme { get; }
    public int Width { get; }
    public int Height { get; }
    public bool Interactive => _host.Interactive;

    /// <summary>头部行（自上而下），自动补一条分隔线。</summary>
    public void Header(params string[] lines)
    {
        _header.Clear();
        foreach (var line in lines) _header.Add(line);
        if (_header.Count == 0) _header.Add(string.Empty);
        _header.Add(Theme.Muted(ConsoleHost.Rule(Width)));
    }

    /// <summary>主体可用行数。必须在 <see cref="Header"/> 之后取值。</summary>
    public int BodyHeight => Math.Max(1, Height - _header.Count - FooterLines);

    public int BodyLines => _body.Count;

    public void Body(string line) => _body.Add(line);

    public void Body(IEnumerable<string> lines) => _body.AddRange(lines);

    /// <summary>底部提示行（始终占位：null 时输出空行，避免帧高抖动）。</summary>
    public void Toast(string? message) => _toast = message;

    public void Keys(params (string Key, string Label)[] keys)
    {
        _keys.Clear();
        _keys.AddRange(keys);
    }

    /// <summary>把本帧写进宿主。行数不会超过视口高度（多余主体行直接丢弃）。</summary>
    public void Render()
    {
        int bodyH = BodyHeight;
        var lines = new List<string>(_header.Count + bodyH + FooterLines);
        lines.AddRange(_header);

        int emitted = 0;
        foreach (var line in _body)
        {
            if (emitted >= bodyH) break;   // 视图应自己算好，这里只兜底
            lines.Add(line);
            emitted++;
        }
        if (Interactive)
        {
            while (emitted++ < bodyH) lines.Add(string.Empty);
        }

        lines.Add(Theme.Muted(ConsoleHost.Rule(Width)));
        lines.Add(_toast is null ? string.Empty : Theme.Warning(_toast));
        lines.Add(KeysLine());

        if (lines.Count > Height) lines.RemoveRange(Height, lines.Count - Height);
        foreach (var line in lines) _host.Line(Fit(line));
    }

    /// <summary>
    /// 兜底裁剪：视图给的行也必须过宽度闸门（例如超长的 chain.json 路径）。
    /// 有颜色时补一个收束码，避免挂起的颜色染到下一行。
    /// </summary>
    private string Fit(string line)
    {
        var text = TextWidth.ClipVisible(line, Width, out bool clipped);
        return clipped && Theme.HasColor ? text + "\u001b[0m" : text;
    }

    /// <summary>键位行：放不下时保留前缀并追加「? 帮助」，绝不折行。</summary>
    private string KeysLine()
    {
        if (_keys.Count == 0) return string.Empty;

        var pieces = _keys.Select(k => $"{k.Key} {k.Label}").ToList();
        string full = string.Join("   ", pieces);
        if (TextWidth.Measure(full) <= Width) return Theme.Muted(full);

        const string more = "? 帮助";
        int avail = Width - TextWidth.Measure("   " + more);
        var sb = new StringBuilder();
        foreach (var piece in pieces)
        {
            int add = (sb.Length > 0 ? 3 : 0) + TextWidth.Measure(piece);
            if (TextWidth.Measure(sb.ToString()) + add > avail) break;
            if (sb.Length > 0) sb.Append("   ");
            sb.Append(piece);
        }

        string text = sb.Length == 0 ? more : sb + "   " + more;
        return Theme.Muted(text);
    }
}
