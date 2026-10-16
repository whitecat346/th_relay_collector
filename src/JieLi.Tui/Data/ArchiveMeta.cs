#nullable enable
using System.Text;

namespace JieLi.Tui.Data;

/// <summary>
/// 极简 YAML 读取器 —— 只支持 meta.yml 需要的子集：
///   key: value
///   key:
///     nested: value       （一层缩进）
/// 引号（单/双）与行内 # 注释会被去掉。不引入第三方 YAML 库（NuGet 缓存里没有）。
/// </summary>
public static class MiniYaml
{
    public sealed class Node
    {
        public Dictionary<string, string> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => Scalars.TryGetValue(key, out var v) ? v : null;
        public Node? Sub(string key) => Children.TryGetValue(key, out var v) ? v : null;
    }

    public static Node Parse(string path)
    {
        var root = new Node();
        if (!File.Exists(path)) return root;

        var stack = new Stack<(int Indent, Node Node)>();
        stack.Push((-1, root));

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            int indent = line.Length - trimmed.Length;
            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed[..colon].Trim();
            var value = StripInlineComment(trimmed[(colon + 1)..]).Trim();

            while (stack.Count > 1 && indent <= stack.Peek().Indent) stack.Pop();
            var parent = stack.Peek().Node;

            if (value.Length == 0)
            {
                var child = new Node();
                parent.Children[key] = child;
                stack.Push((indent, child));
            }
            else
            {
                parent.Scalars[key] = Unquote(value);
            }
        }
        return root;
    }

    private static string StripInlineComment(string s)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(s[i - 1])))
                return s[..i];
        }
        return s;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }
}

/// <summary>归档元信息（meta.yml）。宣传信息是"机器猜不准、必须由人拍板"的部分，因此它是唯一真值源。</summary>
public sealed class ArchiveMeta
{
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public long? TopicId { get; set; }
    public string MainHostName { get; set; } = string.Empty;
    public string MainHostLink { get; set; } = string.Empty;
    /// <summary>宣传链接：标签 → URL（一宣/二宣/终宣/总结…）。</summary>
    public List<(string Label, string Url)> Promotions { get; } = new();
    /// <summary>可选的显式棒位定义：棒号 → 时段。</summary>
    public Dictionary<int, string> BarTimes { get; } = new();

    public static ArchiveMeta Load(string path)
    {
        var meta = new ArchiveMeta();
        var root = MiniYaml.Parse(path);
        meta.Slug = root.Get("slug");
        meta.Title = root.Get("title");
        if (long.TryParse(root.Get("topic_id"), out var tid)) meta.TopicId = tid;

        var host = root.Sub("主催");
        if (host is not null)
        {
            meta.MainHostName = host.Get("名字") ?? host.Get("name") ?? string.Empty;
            meta.MainHostLink = host.Get("链接") ?? host.Get("link") ?? string.Empty;
        }

        var promos = root.Sub("宣传");
        if (promos is not null)
            foreach (var (k, v) in promos.Scalars)
                meta.Promotions.Add((k, v));

        var bars = root.Sub("棒位");
        if (bars is not null)
            foreach (var (k, v) in bars.Scalars)
                if (int.TryParse(k, out var n)) meta.BarTimes[n] = v;

        return meta;
    }

    public static string PathFor(string workRoot, long topicId)
        => Path.Combine(workRoot, topicId.ToString(), "meta.yml");

    /// <summary>生成一份待人工填写的 meta.yml 草稿（不覆盖已存在的文件）。</summary>
    public static string DraftYaml(string title, long topicId, string topicUrl, string? suggestedSlug)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 归档元信息 —— 本文件是宣传信息的唯一真值源，render 只信这里的内容。");
        sb.AppendLine("# 修正此文件后重新运行 --render，输出会按新内容重生成。");
        sb.AppendLine();
        sb.AppendLine($"# slug: {suggestedSlug ?? ""}            # 归档目录名；留空则用目录名");
        sb.AppendLine($"title: {title}");
        sb.AppendLine($"topic_id: {topicId}");
        sb.AppendLine($"topic_url: {topicUrl}");
        sb.AppendLine();
        sb.AppendLine("主催:");
        sb.AppendLine("  名字:            # ← 人工填写");
        sb.AppendLine("  链接:            # ← 人工填写");
        sb.AppendLine();
        sb.AppendLine("宣传:");
        sb.AppendLine("  一宣:            # ← 人工填写 URL");
        sb.AppendLine("  二宣:");
        sb.AppendLine("  终宣:");
        sb.AppendLine("  总结:");
        sb.AppendLine();
        sb.AppendLine("# 可选：显式棒位定义（棒号: 时段）。留空则按采集到的时间列输出。");
        sb.AppendLine("# 棒位:");
        sb.AppendLine("#   1: 0:00");
        sb.AppendLine("#   2: 1:00");
        return sb.ToString();
    }
}
