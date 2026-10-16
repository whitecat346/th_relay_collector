#nullable enable
using System.Text;

namespace JieLi.Tui.Text;

/// <summary>
/// 终端显示宽度计算。
/// .NET 的 string.Length 按 UTF-16 码元计数，汉字/全角标点/emoji 在终端占 2 列，
/// 所以 PadRight 一定会让表格错位。这里提供按"显示宽度"对齐的唯一入口。
/// </summary>
public static class TextWidth
{
    /// <summary>单个码点的终端显示宽度（0/1/2）。</summary>
    public static int RuneWidth(int cp)
    {
        if (cp == 0) return 0;

        // 组合用记号 / 零宽字符：不占列
        if (cp < 32) return 0;
        if (cp is >= 0x0300 and <= 0x036F) return 0;   // Combining Diacritical Marks
        if (cp is >= 0x200B and <= 0x200F) return 0;   // ZWSP/ZWJ/ZWNJ/LRM/RLM
        if (cp is >= 0xFE00 and <= 0xFE0F) return 0;   // Variation Selectors
        if (cp == 0xFEFF) return 0;                    // BOM / ZWNBSP
        if (cp is >= 0x1AB0 and <= 0x1AFF) return 0;   // Combining Diacritical Marks Extended
        if (cp is >= 0x20D0 and <= 0x20FF) return 0;   // Combining Diacritical Marks for Symbols

        // 宽字符（East Asian Wide / Fullwidth）
        if (cp is >= 0x1100 and <= 0x115F) return 2;   // Hangul Jamo 初声
        if (cp is >= 0x2E80 and <= 0x303E) return 2;   // CJK 部首补充 / 康熙部首 / CJK 符号标点
        if (cp is >= 0x3041 and <= 0x33FF) return 2;   // 平假名/片假名/注音/谚文兼容/CJK 兼容
        if (cp is >= 0x3400 and <= 0x4DBF) return 2;   // CJK 统一表意文字扩展 A
        if (cp is >= 0x4E00 and <= 0x9FFF) return 2;   // CJK 统一表意文字
        if (cp is >= 0xA000 and <= 0xA4CF) return 2;   // 彝文
        if (cp is >= 0xAC00 and <= 0xD7A3) return 2;   // 谚文音节
        if (cp is >= 0xF900 and <= 0xFAFF) return 2;   // CJK 兼容表意文字
        if (cp is >= 0xFE10 and <= 0xFE19) return 2;   // 竖排形式
        if (cp is >= 0xFE30 and <= 0xFE6F) return 2;   // CJK 兼容形式 / 小写变体
        if (cp is >= 0xFF00 and <= 0xFF60) return 2;   // 全角 ASCII
        if (cp is >= 0xFFE0 and <= 0xFFE6) return 2;   // 全角符号
        if (cp is >= 0x1F300 and <= 0x1F64F) return 2; // 绘文字
        if (cp is >= 0x1F900 and <= 0x1F9FF) return 2; //  Supplemental Symbols and Pictographs
        if (cp is >= 0x20000 and <= 0x3FFFD) return 2; // CJK 扩展 B 及以上

        return 1;
    }

    /// <summary>字符串的终端显示宽度。</summary>
    public static int Measure(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int w = 0;
        foreach (var r in s.EnumerateRunes()) w += RuneWidth(r.Value);
        return w;
    }

    /// <summary>按显示宽度右侧补空格。</summary>
    public static string Pad(string? s, int width)
    {
        s ??= string.Empty;
        int w = Measure(s);
        return w >= width ? s : s + new string(' ', width - w);
    }

    /// <summary>按显示宽度左侧补空格（右对齐）。</summary>
    public static string PadLeft(string? s, int width)
    {
        s ??= string.Empty;
        int w = Measure(s);
        return w >= width ? s : new string(' ', width - w) + s;
    }

    /// <summary>按显示宽度截断，超出时以省略号收尾。</summary>
    public static string Truncate(string? s, int maxWidth, string ellipsis = "…")
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (Measure(s) <= maxWidth) return s;

        int ell = Measure(ellipsis);
        int budget = maxWidth - ell;
        if (budget <= 0) return ellipsis;

        var sb = new StringBuilder();
        int w = 0;
        foreach (var r in s.EnumerateRunes())
        {
            int rw = RuneWidth(r.Value);
            if (w + rw > budget) break;
            sb.Append(r.ToString());
            w += rw;
        }
        return sb.Append(ellipsis).ToString();
    }
}
