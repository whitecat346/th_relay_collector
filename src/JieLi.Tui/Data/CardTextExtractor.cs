#nullable enable
using System.Text;
using System.Text.Json;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>
/// 从卡片 JSON 里抽取正文。
/// 实测结论（topic_id=1343535，34 条）：
///   - DYNAMIC_TYPE_DRAW    正文在 module_dynamic.desc.text
///   - DYNAMIC_TYPE_AV      正文在 module_dynamic.major.archive.desc（+ title 里也常有"第N棒"）
///   - DYNAMIC_TYPE_ARTICLE 正文在 module_dynamic.major.article.desc
///   - 少数卡片的 desc.text 为空且 major 正文也无棒信息 → 必须进"无法归属"清单
/// </summary>
public static class CardTextExtractor
{
    private static readonly string[] PreferredFields = { "desc", "title", "summary", "text", "content" };

    /// <summary>
    /// 无正文价值、且会污染"标题时间/时长"识别的字段。
    /// 实测教训：video 的 duration_text = "01:58" 曾被标题时间正则误当成"1:58 棒"，造成棒号重复。
    /// </summary>
    private static readonly HashSet<string> NoiseFields = new(StringComparer.Ordinal)
    {
        "src", "cover", "covers", "face", "image", "image_enhance",
        "duration_text", "duration", "bvid", "aid",       // jump_url 保留（人工可点）
        "media_type", "bg_color", "color", "text_color", "label", "stat",
        "type", "jump_url2",
    };

    /// <summary>判断某个来源字段是否为"标题"类（视频/文章的标题）。</summary>
    public static bool IsTitleField(string sourceKey)
        => sourceKey.EndsWith(".title", StringComparison.Ordinal)
           || sourceKey.EndsWith(".name", StringComparison.Ordinal);

    public static TopicCard Extract(CardItem card)
    {
        var tc = new TopicCard
        {
            Id = card.IdStr ?? string.Empty,
            Type = card.Type ?? string.Empty,
        };

        var mods = card.Modules;
        if (mods?.Author is { } a)
        {
            tc.AuthorMid = a.Mid;
            tc.AuthorName = a.Name ?? string.Empty;
            tc.PubTs = a.PubTs;
            tc.PubTimeLabel = a.PubTime;
        }

        // 1) desc.text 优先级最高
        var descText = mods?.Dynamic?.Desc?.Text;
        if (!string.IsNullOrWhiteSpace(descText))
            tc.TextSources["desc.text"] = Normalize(descText);

        // 2) major.* 下的所有字符串字段，按 文档顺序 + 字段优先级 收集
        if (mods?.Dynamic?.Major is { } major)
            CollectJsonStrings(major, "major", tc.TextSources);

        tc.MergedText = string.Join(" ⏎ ", tc.TextSources.Values);
        return tc;
    }

    /// <summary>递归收集 JSON 里的字符串值，键名形如 major.archive.desc / major.draw.items[0].src（跳过图片 URL）。</summary>
    private static void CollectJsonStrings(JsonElement el, string path, Dictionary<string, string> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // 先按 PreferredFields 的优先级，再按文档顺序补齐其余
                var props = el.EnumerateObject().ToList();
                foreach (var want in PreferredFields)
                {
                    foreach (var p in props)
                    {
                        if (!p.NameEquals(want)) continue;
                        CollectJsonStrings(p.Value, path + "." + p.Name, sink);
                    }
                }
                foreach (var p in props)
                {
                    if (PreferredFields.Contains(p.Name)) continue;
                    if (NoiseFields.Contains(p.Name)) continue;
                    CollectJsonStrings(p.Value, path + "." + p.Name, sink);
                }
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    CollectJsonStrings(item, $"{path}[{i++}]", sink);
                    if (i > 40) break; // 防御：极端情况下避免遍历巨量数组
                }
                break;

            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    // 图片是 data URI 或 http 链接时无正文价值
                    if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        (s.Contains("/bfs/", StringComparison.Ordinal) || s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)))
                        return;
                    sink[path] = Normalize(s);
                }
                break;
        }
    }

    /// <summary>换行与零宽字符归一化：解析层要面对的是 HTML 里混入的 &nbsp;/\u200b/多空行。</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\u200b':  // 零宽空格（B站 @ 提及后常带）
                case '\u200e':
                case '\u200f':
                case '\ufeff':
                case '\u00a0':  // 不换行空格
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        var text = sb.ToString();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // 折叠连续空白（保留单个换行，解析层靠 | 与换行分隔）
        var lines = text.Split('\n').Select(l => Collapse(l.Trim()));
        return string.Join("\n", lines).Trim();
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var ch in s)
        {
            bool isSpace = ch == ' ' || ch == '\t';
            if (isSpace)
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }
}
