#nullable enable
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wcwidth;

namespace JieLi.Tui.Text;

/// <summary>
/// 终端显示宽度计算（以 Wcwidth 的 Unicode 宽度表为准）。
/// .NET 的 string.Length 按 UTF-16 码元计数，汉字/全角标点/emoji 在终端占 2 列，
/// 所以 PadRight 一定会让表格错位。这里提供按"显示宽度"对齐的唯一入口。
///
/// 与旧实现（手写区间表）的差别：
///   - 宽度表来自 Wcwidth（Unicode 17），不再靠猜区间；
///   - 按扩展字素簇（grapheme cluster）计宽：👨‍👩‍👧 这类 ZWJ 序列算 2 列而不是 6 列；
///   - ANSI 转义序列不占列（Measure/Truncate 先剥离，ClipVisible 原样保留）；
///   - 制表符按 4 列制表位展开；其它控制字符 0 列。
/// East Asian Ambiguous（★ ⚠ → ‹）按 Wcwidth 默认算 1 列——Windows Terminal 如此；
/// 个别中文 conhost 渲染成 2 列时，用 --ascii 走纯 ASCII 符号兜底。
/// </summary>
public static class TextWidth
{
    /// <summary>制表位宽度。</summary>
    public const int TabWidth = 4;

    private static readonly Regex AnsiPattern = new("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>去掉 ANSI CSI 转义序列（只保留可见字符）。</summary>
    public static string StripAnsi(string? s) => string.IsNullOrEmpty(s) ? string.Empty : AnsiPattern.Replace(s, string.Empty);

    /// <summary>单个码点的终端显示宽度（0/1/2）。</summary>
    public static int RuneWidth(int cp)
    {
        if (cp <= 0) return 0;
        if (cp == '\t') return TabWidth;
        if (cp < 32 || cp == 0x7F) return 0;
        if (!Rune.IsValid(cp)) return 1;
        int w = UnicodeCalculator.GetWidth(cp);
        return w < 0 ? 0 : w;
    }

    /// <summary>字符串的终端显示宽度（ANSI 不占列）。</summary>
    public static int Measure(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = StripAnsi(s);

        int w = 0;
        for (int i = 0; i < s.Length; )
        {
            char ch = s[i];
            if (ch == '\t') { w += TabWidth; i++; continue; }
            if (ch < ' ') { i++; continue; }          // 其它控制字符：不占列

            string elem = StringInfo.GetNextTextElement(s, i);
            w += ClusterWidth(elem);
            i += elem.Length;
        }
        return w;
    }

    /// <summary>一个字素簇的显示宽度（含 ZWJ emoji / 变体选择符聚合）。</summary>
    public static int ClusterWidth(string? elem)
    {
        if (string.IsNullOrEmpty(elem)) return 0;
        if (elem == "\t") return TabWidth;
        if (elem.Length == 1 && elem[0] < ' ') return 0;

        int w = UnicodeCalculator.GetWidth(elem);
        return w < 0 ? 0 : w;   // 零宽/控制类返回 -1 时按不占列处理
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

    /// <summary>按显示宽度截断，超出时以省略号收尾（入参应为纯文本，非 ANSI 着色串）。</summary>
    public static string Truncate(string? s, int maxWidth, string ellipsis = "…")
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (maxWidth <= 0) return string.Empty;
        if (Measure(s) <= maxWidth) return s;

        int ell = Measure(ellipsis);
        int budget = maxWidth - ell;
        if (budget <= 0) return ellipsis;

        var sb = new StringBuilder();
        int w = 0;
        for (int i = 0; i < s.Length; )
        {
            char ch = s[i];
            if (ch == '\t') { if (w + TabWidth > budget) break; sb.Append(' ', TabWidth); w += TabWidth; i++; continue; }
            if (ch < ' ') { i++; continue; }

            string elem = StringInfo.GetNextTextElement(s, i);
            int ew = ClusterWidth(elem);
            if (w + ew > budget) break;
            sb.Append(elem);
            w += ew;
            i += elem.Length;
        }
        return sb.Append(ellipsis).ToString();
    }

    /// <summary>
    /// 按显示宽度裁剪一行，ANSI 转义序列（颜色）原样保留、不计列；
    /// 制表符展开为空格，换行不该出现在单行内。clipped 表示是否发生了截断。
    /// </summary>
    public static string ClipVisible(string? text, int maxWidth, out bool clipped)
    {
        clipped = false;
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) { clipped = !string.IsNullOrEmpty(text); return string.Empty; }

        var sb = new StringBuilder(text.Length);
        int w = 0;

        for (int i = 0; i < text.Length; )
        {
            char ch = text[i];

            // ANSI CSI：ESC [ ... 终止字节(0x40-0x7E)，原样保留，不计宽。
            if (ch == '\u001b')
            {
                int j = i + 1;
                if (j < text.Length && text[j] == '[')
                {
                    j++;
                    while (j < text.Length && !(text[j] >= '@' && text[j] <= '~')) j++;
                    if (j < text.Length) j++;
                }
                if (j <= i) j = i + 1;
                sb.Append(text, i, j - i);
                i = j;
                continue;
            }

            if (ch == '\r') { i++; continue; }

            if (ch == '\t' || ch == '\n')
            {
                // 制表符按 4 列制表位展开；换行不该出现在单行内，替换为空格。
                int n = ch == '\t' ? TabWidth - (w % TabWidth) : 1;
                if (w + n > maxWidth) { clipped = true; break; }
                sb.Append(new string(' ', n));
                w += n;
                i++;
                continue;
            }

            if (ch < ' ') { i++; continue; }   // 其它控制字符：丢弃，不占列

            string elem = StringInfo.GetNextTextElement(text, i);
            int ew = ClusterWidth(elem);
            if (w + ew > maxWidth) { clipped = true; break; }
            sb.Append(elem);
            w += ew;
            i += elem.Length;
        }

        if (w > maxWidth) clipped = true;
        return sb.ToString();
    }

    /// <summary>
    /// 按显示宽度折行（优先在空格处断，宽字符不切开）。用于全文查看器这类只读长文本。
    /// 返回值不含结尾空行。
    /// </summary>
    public static List<string> Wrap(string? text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        width = Math.Max(4, width);
        foreach (var para in text.Replace("\r", string.Empty).Split('\n'))
        {
            var sb = new StringBuilder();
            int w = 0;
            int lastSpace = -1;

            for (int i = 0; i < para.Length;)
            {
                char ch = para[i];
                if (ch == '\t')
                {
                    int n = TabWidth - (w % TabWidth);
                    sb.Append(new string(' ', n));
                    w += n;
                    i++;
                    continue;
                }
                if (ch < ' ') { i++; continue; }

                string elem = StringInfo.GetNextTextElement(para, i);
                int ew = ClusterWidth(elem);
                if (w + ew > width)
                {
                    if (lastSpace > 0)
                    {
                        string rest = sb.ToString(lastSpace + 1, sb.Length - lastSpace - 1);
                        lines.Add(sb.ToString(0, lastSpace));
                        sb.Clear().Append(rest);
                        w = Measure(rest);
                        lastSpace = -1;
                    }
                    else if (sb.Length > 0)
                    {
                        lines.Add(sb.ToString());
                        sb.Clear();
                        w = 0;
                        lastSpace = -1;
                    }
                }

                if (elem == " ") lastSpace = sb.Length;
                sb.Append(elem);
                w += ew;
                i += elem.Length;
            }

            lines.Add(sb.ToString());
        }

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
