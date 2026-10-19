#nullable enable
using Spectre.Console;

namespace JieLi.Tui.Tui.Rendering;

/// <summary>语义色角色。视图只按角色取色，不直接手写 ANSI 码。</summary>
public enum Role
{
    Normal,
    Title,
    Subtitle,
    Accent,
    Success,
    Warning,
    Error,
    Muted,
    Selected,
    Link,
}

/// <summary>
/// 语义色板 + 终端能力降级：truecolor → 256 → 16 → 无色。
/// 遵循 NO_COLOR / TERM=dumb；`--no-color` 通过 <see cref="ForceNoColor"/> 强制关闭。
///
/// 所有角色都用「开码 … 关码」配对（39/22/24/27），而不是「开码 … Reset」：
/// 嵌套时内层只关掉自己开的属性，不会顺手把外层的反显/粗体清掉
/// （旧版的选中行就是被行内 Reset 清掉反显的）。
/// </summary>
public sealed class Theme
{
    public enum Depth
    {
        None,
        Ansi16,
        Ansi256,
        TrueColor,
    }

    /// <summary>无颜色实例（--plain / 非 TTY / NO_COLOR）。</summary>
    public static Theme None { get; } = new(Depth.None);

    /// <summary>测试与 `--no-color` 用：置 true 后 <see cref="Detect"/> 一律返回无色。</summary>
    public static bool ForceNoColor { get; set; }

    public Depth ColorDepth { get; }

    public bool HasColor => ColorDepth != Depth.None;

    private Theme(Depth depth) => ColorDepth = depth;

    /// <summary>测试/工具用：按指定色深构造，绕过终端探测（快照矩阵需要固定色深）。</summary>
    public static Theme ForDepth(Depth depth) => depth == Depth.None ? None : new Theme(depth);

    /// <summary>按当前终端能力选色深。探测失败一律降级为无色，绝不猜。</summary>
    public static Theme Detect()
    {
        if (ForceNoColor) return None;
        if (Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }) return None;
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)) return None;

        try
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Detect,
                ColorSystem = ColorSystemSupport.Detect,
                Out = new AnsiConsoleOutput(Console.Out),
            });
            var cap = console.Profile.Capabilities;
            if (!cap.Ansi || cap.ColorSystem == ColorSystem.NoColors) return None;
            return new Theme(cap.ColorSystem switch
            {
                ColorSystem.TrueColor => Depth.TrueColor,
                ColorSystem.EightBit => Depth.Ansi256,
                _ => Depth.Ansi16,
            });
        }
        catch
        {
            return None;
        }
    }

    /// <summary>按角色包裹文本。角色为 Normal 或无色时原样返回。</summary>
    public string Paint(Role role, string? text)
    {
        if (text is null || text.Length == 0) return text ?? string.Empty;
        if (!HasColor || role == Role.Normal) return text;
        var (on, off) = Codes(role);
        return on.Length == 0 ? text : on + text + off;
    }

    /// <summary>粗体（不占语义角色，用于表头/强调片段）。</summary>
    public string Bold(string? text)
    {
        if (text is null || text.Length == 0 || !HasColor) return text ?? string.Empty;
        return "\u001b[1m" + text + "\u001b[22m";
    }

    public string Title(string? t) => Paint(Role.Title, t);
    public string Subtitle(string? t) => Paint(Role.Subtitle, t);
    public string Accent(string? t) => Paint(Role.Accent, t);
    public string Success(string? t) => Paint(Role.Success, t);
    public string Warning(string? t) => Paint(Role.Warning, t);
    public string Error(string? t) => Paint(Role.Error, t);
    public string Muted(string? t) => Paint(Role.Muted, t);
    public string Selected(string? t) => Paint(Role.Selected, t);
    public string Link(string? t) => Paint(Role.Link, t);

    private (string On, string Off) Codes(Role role) =>
        ColorDepth == Depth.Ansi16 ? Ansi16(role) : Rich(role);

    private static (string On, string Off) Ansi16(Role role) => role switch
    {
        Role.Title => ("\u001b[1m", "\u001b[22m"),
        Role.Subtitle => ("\u001b[2m", "\u001b[22m"),
        Role.Accent => ("\u001b[36m", "\u001b[39m"),
        Role.Success => ("\u001b[32m", "\u001b[39m"),
        Role.Warning => ("\u001b[33m", "\u001b[39m"),
        Role.Error => ("\u001b[31m", "\u001b[39m"),
        Role.Muted => ("\u001b[2m", "\u001b[22m"),
        Role.Selected => ("\u001b[7m", "\u001b[27m"),
        Role.Link => ("\u001b[4m", "\u001b[24m"),
        _ => ("", ""),
    };

    private (string On, string Off) Rich(Role role)
    {
        bool tc = ColorDepth == Depth.TrueColor;
        return role switch
        {
            Role.Title => (tc ? "\u001b[1;38;2;235;235;235m" : "\u001b[1;38;5;231m", "\u001b[22;39m"),
            Role.Subtitle => (tc ? "\u001b[38;2;138;148;158m" : "\u001b[38;5;245m", "\u001b[39m"),
            Role.Accent => (tc ? "\u001b[38;2;79;195;247m" : "\u001b[38;5;75m", "\u001b[39m"),
            Role.Success => (tc ? "\u001b[38;2;126;217;87m" : "\u001b[38;5;114m", "\u001b[39m"),
            Role.Warning => (tc ? "\u001b[38;2;255;200;87m" : "\u001b[38;5;221m", "\u001b[39m"),
            Role.Error => (tc ? "\u001b[38;2;255;92;92m" : "\u001b[38;5;203m", "\u001b[39m"),
            Role.Muted => (tc ? "\u001b[38;2;138;148;158m" : "\u001b[38;5;245m", "\u001b[39m"),
            Role.Selected => ("\u001b[7m", "\u001b[27m"),
            Role.Link => (tc ? "\u001b[4;38;2;111;183;255m" : "\u001b[4;38;5;111m", "\u001b[24;39m"),
            _ => ("", ""),
        };
    }
}
