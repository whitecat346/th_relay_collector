#nullable enable
using System.Text;
using System.Text.RegularExpressions;

namespace JieLi.Tui.Data;

/// <summary>
/// 文本归一化。
/// 实测动机：话题 id=1343535 里有一条写的是「第六棒 4：30」，
/// 纯阿拉伯数字正则会把第 6 棒整体漏掉，进而误报"缺第6棒"。
/// </summary>
public static class TextNormalizer
{
    private static readonly Dictionary<char, int> Digits = new()
    {
        ['零'] = 0, ['〇'] = 0, ['○'] = 0,
        ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
        ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
    };

    /// <summary>把全角数字/制表符等归一化，并把全角冒号留给调用方按需处理。</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                >= '０' and <= '９' => (char)(ch - '０' + '0'),   // 全角数字 → 半角
                '\u3000' => ' ',                                  // 表意空格 → 普通空格
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>把中文数字串转成整数；支持"十/十五/二十/二十四/三十四"，失败返回 null。</summary>
    public static int? ChineseToInt(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        if (int.TryParse(s, out var plain)) return plain;

        int total = 0, current = 0;
        bool any = false;
        foreach (var ch in s)
        {
            if (ch == '十')
            {
                // "十五" → 15；"二十" → 20；"十" → 10
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
                any = true;
                continue;
            }
            if (ch is '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
                any = true;
                continue;
            }
            if (!Digits.TryGetValue(ch, out var d)) return null;
            current = d;
            any = true;
        }
        if (!any) return null;
        return total + current;
    }

    /// <summary>"第 N 棒" 的棒号捕获串：允许阿拉伯数字或中文数字。</summary>
    public const string BarNumberPattern = @"(?:第\s*)?([0-9]{1,3}|[零〇○一二两三四五六七八九十]{1,4})\s*棒";

    /// <summary>把 HH:MM / HH：MM 统一成 H:MM 形式（保留原始小时数，不补零）。</summary>
    public static string NormalizeTime(string hh, string mm)
    {
        var h = int.TryParse(hh, out var hv) ? hv : 0;
        return $"{h}:{mm}";
    }

    /// <summary>
    /// 去掉 @ 前缀、括号修饰与"代发/代投"尾巴，得到纯名字。
    /// 实测需要处理："@鴉CORVUS（代发）"、"@顽咲_Little （超级可爱）"、"@林檎林（X：Ringorin_art）"。
    /// </summary>
    public static string CleanMention(string s)
    {
        s = s.Trim();
        s = s.TrimStart('@');
        // 截掉第一个中文/英文左括号及其后内容
        int cut = s.IndexOfAny(new[] { '（', '(', '【' });
        if (cut > 0) s = s[..cut];
        s = s.TrimEnd('）', ')', '　', ' ', '：', ':', '|', '｜');
        // 去掉紧跟在名字后的"代发/代投"
        foreach (var suffix in new[] { "代发", "代投", "代发布" })
            if (s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length)
                s = s[..^suffix.Length];
        return s.Trim();
    }

    /// <summary>名字比较：忽略大小写、空白、零宽字符与常见全半角差异。</summary>
    public static bool SameName(string a, string b)
    {
        static string Key(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch) || ch == '\u200b') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
        var ka = Key(CleanMention(a));
        var kb = Key(CleanMention(b));
        return ka.Length > 0 && ka == kb;
    }

    /// <summary>合并文本里是否提到某名字（用于判断"发布者即作者"）。</summary>
    public static bool MentionsName(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var key = CleanMention(name);
        if (key.Length < 2) return false;
        return text.Contains(key, StringComparison.OrdinalIgnoreCase);
    }
}
