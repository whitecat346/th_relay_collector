#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JieLi.Tui.Chain;
using JieLi.Tui.Text;
using JieLi.Tui.Tui.Rendering;

namespace JieLi.Tui.Tui;

// ===========================================================================
// 文件所有者：Agent「ui+ai」
// 只允许改本文件与 Chain/AiAssist.cs / Tui/ReviewView.cs。
//
// 职责：接力链的只读浏览视图（对应 Q22 的 C 键）。显示：棒号/时段/创作者/来源方法/方向/置信/@反指，
// 缺口高亮。**不进 CSV 契约**，纯展示。
// 实现自由，但：不要动 CSV/MD 渲染契约，不要把逻辑写进 ReviewView 之外的地方。
// ===========================================================================

/// <summary>链条只读视图。</summary>
public sealed class ChainView
{
    // chain.json 由 ChainWriter 落盘。这里对两种写法都宽容：
    //   * PascalCase（System.Text.Json 默认）与 snake_case（和 review.jsonl 保持一致）
    //   * 枚举写成数字（默认）或字符串（若对方开了 JsonStringEnumConverter）
    // 归一化 key + 大小写无关 + 宽容枚举转换，三件一起做，避免链路口径一改这边就瞎。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new TolerantEnumConverter<PostKind>(),
            new TolerantEnumConverter<WalkDirection>(),
            new TolerantEnumConverter<FetchMethod>(),
        },
    };

    /// <summary>chain.json 的绝对路径（供错误提示与渲染标题使用）。</summary>
    public string JsonPath { get; }

    public ChainResult Result { get; }

    public int GapCount => Result.Gaps.Count;

    /// <summary>当前选中的节点（OrderNodes 后的下标）；-1 表示还没进过链条视图。</summary>
    public int NodeCursor { get; private set; } = -1;

    /// <summary>按 ↓/↑ 移动节点选择。返回是否发生了移动（用于决定要不要重绘）。</summary>
    public bool MoveNodeCursor(int delta, bool toEnd = false)
    {
        var ordered = OrderNodes(Result.Nodes);
        if (ordered.Count == 0) return false;
        int before = NodeCursor;
        int next = toEnd ? ordered.Count - 1 : (NodeCursor < 0 ? 0 : NodeCursor) + delta;
        NodeCursor = Math.Clamp(next, 0, ordered.Count - 1);
        return NodeCursor != before;
    }

    /// <summary>选中节点的详情（喂给 ReviewView 的全文查看器，Esc 返回）。</summary>
    public string[] NodeDetailLines()
    {
        var ordered = OrderNodes(Result.Nodes);
        if (ordered.Count == 0) return new[] { "（链条里没有节点）" };
        int i = Math.Clamp(NodeCursor < 0 ? 0 : NodeCursor, 0, ordered.Count - 1);
        var n = ordered[i];
        var f = n.Facts;
        var lines = new List<string>
        {
            $"棒号      {BarText(f)}",
            $"时段      {Blank(f?.DeclaredTime)}",
            $"创作者    {CreatorOf(f)}",
            $"代发      {(f is { IsProxy: true } ? Blank(f.ProxyName) : "-")}",
            $"类型      {Blank(f?.WorkKindLabel)}",
            $"方向      {DirArrow(n.Direction)}   来源 {SourceLabel(n.Source)}   置信 {(n.Confidence > 0 ? n.Confidence.ToString("0.00") : "-")}",
            $"帖子ID    {n.Ref.Id}",
            $"链接      {Blank(f?.Url)}",
            $"定位      {(n.IsStart ? "★入口" : n.IsSupplement ? "+补录" : "链上节点")} · 深度 {n.Depth}",
        };

        var rev = ReversePointer(n, ordered, i);
        if (rev.Text.Length > 0) lines.Add($"@反指     {(rev.Mismatch ? "⚠ " : string.Empty)}{rev.Text}");
        if (n.Notes.Count > 0) lines.Add("备注      " + string.Join("；", n.Notes));
        if (n.MatchedBy is { Length: > 0 }) lines.Add("匹配依据  " + n.MatchedBy);

        var gaps = Result.Gaps.Where(g => string.Equals(g.FromPostId, n.Ref.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var g in gaps)
        {
            lines.Add(string.Empty);
            lines.Add($"⚠ 缺口 {DirArrow(g.Direction)} 期望 {ExpectedOf(g)}");
            lines.Add("  原因 " + g.Reason);
            if (g.SuggestedCommand is { Length: > 0 } cmd) lines.Add("  补： " + cmd);
        }

        lines.Add(string.Empty);
        lines.Add("— 正文（前 12 行，按 ↑↓ 继续滚）—");
        var body = (f?.MergedText ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        foreach (var l in body.Where(l => l.Trim().Length > 0).Take(12)) lines.Add(l.Trim());
        return lines.ToArray();
    }

    private static string BarText(PostFacts? f) => f is null ? "?" : f.IsEx ? "EX" : (f.BarNumber?.ToString() ?? "?");

    private static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s!;

    private ChainView(ChainResult result, string jsonPath)
    {
        Result = result;
        JsonPath = jsonPath;
    }

    public static string PathFor(string workRoot, string slug) => Path.Combine(workRoot, slug, "chain.json");

    /// <summary>读 work/&lt;slug&gt;/chain.json。不存在/读不动/结构不认识都返回 null，并用 error 说明原因。</summary>
    public static ChainView? TryLoad(string workRoot, string slug, out string? error)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            error = "当前没有可用的目录名（话题模式不产生 chain.json）。";
            return null;
        }

        var path = PathFor(workRoot, slug);
        if (!File.Exists(path))
        {
            error = $"没有找到 {path}。";
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            error = $"读取 {path} 失败：{ex.Message}";
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            error = $"{path} 是空文件。";
            return null;
        }

        try
        {
            var result = Deserialize(json);
            if (result is null)
            {
                error = $"{path} 的结构无法识别为 ChainResult（缺少 nodes / gaps）。";
                return null;
            }
            error = null;
            return new ChainView(result, path);
        }
        catch (Exception ex)
        {
            error = $"解析 {path} 失败：{ex.Message}";
            return null;
        }
    }

    private static ChainResult? Deserialize(string json)
    {
        var normalized = NormalizeKeys(json);
        var result = JsonSerializer.Deserialize<ChainResult>(normalized, JsonOptions);
        if (result is null) return null;

        // JSON 里显式 null 的集合字段会被反序列化成 null —— 渲染前统一兜底，避免只读视图因脏数据崩掉。
        result.Nodes ??= new List<ChainNode>();
        result.Gaps ??= new List<GapItem>();
        result.Audit ??= new List<ChainAuditEntry>();
        result.Warnings ??= new List<string>();
        return result;
    }

    // -----------------------------------------------------------------------
    // 缺口视图（G）
    // -----------------------------------------------------------------------

    public void RenderGaps(FrameBuilder f, string? toast = null)
    {
        var r = Result;
        var t = f.Theme;

        var info = new StringBuilder("  ");
        if (!string.IsNullOrWhiteSpace(r.RelayName)) info.Append(r.RelayName).Append(" · ");
        info.Append("节点 ").Append(r.Nodes.Count).Append(" · 缺口 ").Append(r.Gaps.Count);
        if (r.Gaps.Count == 0 && r.Nodes.Count > 0) info.Append("（链路完整）");

        f.Header(
            t.Bold("缺口清单（只读）"),
            t.Muted(TextWidth.Truncate(info.ToString(), f.Width)),
            t.Muted(TextWidth.Truncate("  chain.json：" + JsonPath, Math.Max(10, f.Width - 2))));

        int bodyH = f.BodyHeight;
        var tail = new List<string>();
        if (r.Warnings.Count > 0)
            tail.Add(t.Warning("⚠ " + TextWidth.Truncate(r.Warnings[0], Math.Max(10, f.Width - 6))));
        if (!string.IsNullOrWhiteSpace(r.StartPostId))
            tail.Add(t.Muted("入口帖子：" + r.StartPostId
                + (string.IsNullOrWhiteSpace(r.Slug) ? string.Empty : "    目录：" + r.Slug)));

        if (r.Gaps.Count == 0)
        {
            f.Body(t.Success("没有缺口：本次遍历在已知方向上都接上了。"));
            f.Body(tail);
            f.Toast(toast);
            f.Keys(("Esc", "返回"), ("G", "缺口清单"), ("C", "链条视图"), ("A", "补录"));
            f.Render();
            return;
        }

        // 每处缺口 2~3 行；预留 1 行给截断提示。
        int budget = Math.Max(1, bodyH - tail.Count - 1);
        var lines = new List<string>();
        int shown = 0;
        for (int i = 0; i < r.Gaps.Count; i++)
        {
            var g = r.Gaps[i];
            int cost = 2 + (g.SuggestedCommand is { Length: > 0 } ? 1 : 0);
            if (lines.Count + cost > budget) break;

            var line1 = new StringBuilder();
            line1.Append(t.Warning("#" + (i + 1))).Append(' ');
            line1.Append(t.Accent(DirArrow(g.Direction))).Append(' ');
            line1.Append(t.Muted(TextWidth.Pad(TextWidth.Truncate(KindLabel(g.Kind), 6), 6))).Append(' ');
            line1.Append(TextWidth.Truncate("期望 " + ExpectedOf(g), Math.Max(10, f.Width - 22)));
            lines.Add(line1.ToString());

            var reason = string.IsNullOrWhiteSpace(g.Reason) ? "（未给原因）" : g.Reason!;
            lines.Add("    " + t.Muted(TextWidth.Truncate(reason, Math.Max(10, f.Width - 8))));

            if (g.SuggestedCommand is { Length: > 0 } cmd)
                lines.Add("    " + t.Muted("补：") + t.Success(TextWidth.Truncate(cmd, Math.Max(10, f.Width - 8))));
            shown++;
        }

        if (shown < r.Gaps.Count)
            lines.Add(t.Muted($"  …另有 {r.Gaps.Count - shown} 处缺口未显示（终端高度限制；可用 --report 看全文）"));

        f.Body(lines);
        f.Body(tail);
        f.Toast(toast);
        f.Keys(("Esc", "返回"), ("G", "缺口清单"), ("C", "链条视图"), ("A", "补录"));
        f.Render();
    }

    // -----------------------------------------------------------------------
    // 链条视图（C）
    // -----------------------------------------------------------------------

    public void RenderChain(FrameBuilder f, string? toast = null)
    {
        var r = Result;
        var t = f.Theme;
        var ordered = OrderNodes(r.Nodes);

        var info = new StringBuilder("  ");
        info.Append("选 ").Append(NodeCursor + 1).Append('/').Append(ordered.Count).Append(" · ");
        info.Append("入口 ").Append(string.IsNullOrWhiteSpace(r.StartPostId) ? "-" : r.StartPostId);
        info.Append(" · 节点 ").Append(ordered.Count).Append(" · 缺口 ").Append(r.Gaps.Count);
        info.Append(r.ForwardComplete ? " · 已见末棒" : " · 未见末棒");
        if (!string.IsNullOrWhiteSpace(r.Slug)) info.Append(" · 目录 ").Append(r.Slug);


        var header = new List<string>
        {
            t.Bold("链条视图（只读）"),
            t.Muted(TextWidth.Truncate(info.ToString(), f.Width)),
        };
        // 矮窗口（<26 行）先省掉 chain.json 路径行：正文行比路径更有用。
        if (f.Height >= 26)
            header.Add(t.Muted(TextWidth.Truncate("  chain.json：" + JsonPath, Math.Max(10, f.Width - 2))));
        // 图例放页头而不是表格上方：BodyHeight 会按页头行数自动收缩，省下来的行全给表格。
        header.Add(t.Muted(TextWidth.Truncate(
            "★入口 +补录 ←上一棒 →下一棒 ↔双向 ·未知  置信=结构/时间窗  ⚠=与链上相邻棒对不上", f.Width)));
        f.Header(header.ToArray());

        var table = new TableBuilder(
            new TableColumn { Header = "棒", Width = 5, RightAlign = true },
            new TableColumn { Header = "时段", Width = 6 },
            new TableColumn { Header = "创作者", Width = 20, Min = 8 },
            new TableColumn { Header = "方向", Width = 4 },
            new TableColumn { Header = "来源", Width = 10, Priority = Priority.P1, ShowFromWidth = 96 },
            new TableColumn { Header = "置信", Width = 6, RightAlign = true, Priority = Priority.P2, ShowFromWidth = 96 },
            new TableColumn { Header = "@反指", Width = 18, Priority = Priority.P3, ShowFromWidth = 120 });

        var w = table.Widths(f.Width, out var keep);

        NodeCursor = ordered.Count == 0 ? -1 : Math.Clamp(NodeCursor < 0 ? 0 : NodeCursor, 0, ordered.Count - 1);

        // 从哪个帖子断掉的 → 该行标黄，并在行下补一条缺口摘要（"缺口高亮"）。
        var gapFrom = r.Gaps
            .Where(g => !string.IsNullOrWhiteSpace(g.FromPostId))
            .GroupBy(g => g.FromPostId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 主体 = 表头 1 + 行 + 截断提示 + 告警 + 缺口行。
        int tailCount = (r.Warnings.Count > 0 ? 1 : 0) + (r.Gaps.Count > 0 ? 1 : 0);
        int maxRows = Math.Max(1, f.BodyHeight - 1 - tailCount - 1);

        f.Body(table.Header(f.Width, t, w, keep));

        // 滚动窗口：让选中节点留在可见区域（按节点下标近似，缺口行会占用额外行）。
        int start = Math.Max(0, NodeCursor - maxRows + 1);
        int rows = 0;
        for (int i = start; i < ordered.Count; i++)
        {
            if (rows >= maxRows) break;
            var n = ordered[i];
            var facts = n.Facts;
            bool hasGap = gapFrom.ContainsKey(n.Ref.Id);
            Role? role = i == NodeCursor ? Role.Selected
                : hasGap ? Role.Warning
                : n.IsStart ? Role.Accent
                : n.IsSupplement ? Role.Muted
                : null;

            string bar = facts is null ? "?" : facts.IsEx ? "EX" : (facts.BarNumber?.ToString() ?? "?");
            string mark = n.IsStart ? "★" : (n.IsSupplement ? "+" : " ");
            var rev = ReversePointer(n, ordered, i);
            var revText = rev.Text.Length == 0 ? string.Empty : (rev.Mismatch ? "⚠" : string.Empty) + rev.Text;

            var cells = new[]
            {
                mark + bar,
                facts?.DeclaredTime ?? "-",
                CreatorOf(facts),
                DirArrow(n.Direction),
                SourceLabel(n.Source),
                n.Confidence > 0 ? n.Confidence.ToString("0.00") : "-",
                revText,
            };
            f.Body(table.DataRow(cells, w, keep, role, t));
            rows++;

            if (hasGap)
            {
                foreach (var g in gapFrom[n.Ref.Id].Take(2))
                {
                    if (rows >= maxRows) break;
                    f.Body("    " + t.Warning("⚠ 缺口 " + DirArrow(g.Direction) + " 期望 "
                           + TextWidth.Truncate(ExpectedOf(g), Math.Max(8, f.Width - 26))));
                    rows++;
                }
            }
        }

        if (rows >= maxRows && start + rows < ordered.Count)
            f.Body(t.Muted("  …另有节点未显示（终端高度限制；按 G 看缺口，或用 --report 看全文）"));
        if (r.Warnings.Count > 0)
            f.Body(t.Warning("⚠ " + TextWidth.Truncate(r.Warnings[0], Math.Max(10, f.Width - 6))));
        if (r.Gaps.Count > 0)
            f.Body(t.Warning($"缺口 {r.Gaps.Count} 处") + t.Muted("（按 G 查看方向/期望对象/建议命令）"));

        f.Toast(toast);
        f.Keys(("Esc", "返回"), ("↑↓", "选节点"), ("Enter", "节点详情"), ("G", "缺口清单"), ("C", "重载链条"), ("A", "补录"));
        f.Render();
    }

    /// <summary>没有 chain.json 时的统一提示（G / C 共用）。</summary>
    public static void RenderUnavailable(FrameBuilder f, string title, string? error, string hint, string? toast = null)
    {
        var t = f.Theme;
        f.Header(
            t.Bold(title + "（只读）"),
            t.Warning(string.IsNullOrWhiteSpace(error) ? "没有可显示的链条数据。" : error!));

        f.Body(t.Muted("先跑：") + t.Accent(hint));
        f.Body(t.Muted("它会生成 work/<目录名>/chain.json，之后按 G 看缺口、按 C 看链条。"));
        f.Toast(toast);
        f.Keys(("Esc", "返回"), ("G", "缺口清单"), ("C", "链条视图"));
        f.Render();
    }

    // -----------------------------------------------------------------------
    // 渲染辅助
    // -----------------------------------------------------------------------

    private sealed record ReverseInfo(string Text, bool Mismatch);

    /// <summary>
    /// 排序：上一棒侧（远→近）→ 入口/双向 → 下一棒侧（近→远）。
    /// 不直接按 Depth 排，是因为两个方向的 Depth 都从 0 开始，混排会把链读断。
    /// </summary>
    private static List<ChainNode> OrderNodes(IEnumerable<ChainNode> nodes) =>
        nodes
            .OrderBy(n => Rank(n.Direction))
            .ThenBy(n => n.Direction == WalkDirection.Backward ? -n.Depth : n.Depth)
            .ThenBy(n => n.Facts?.BarNumber ?? int.MaxValue)
            .ThenBy(n => n.Facts?.DeclaredTime ?? string.Empty, StringComparer.Ordinal)
            .ToList();

    private static int Rank(WalkDirection d) => d switch
    {
        WalkDirection.Backward => 0,
        WalkDirection.Both => 1,
        WalkDirection.None => 2,
        _ => 3, // Forward 放最后：从入口向未来读
    };

    private static string CreatorOf(PostFacts? f)
    {
        if (f is null) return "-";
        var name = !string.IsNullOrWhiteSpace(f.CreatorInText) ? f.CreatorInText! : f.AuthorName;
        return string.IsNullOrWhiteSpace(name) ? "-" : name;
    }

    private static string DirArrow(WalkDirection d) => d switch
    {
        WalkDirection.Forward => "→",
        WalkDirection.Backward => "←",
        WalkDirection.Both => "↔",
        _ => "·",
    };

    private static string SourceLabel(FetchMethod m) => m switch
    {
        FetchMethod.StartLink => "入口",
        FetchMethod.Supplement => "补录",
        FetchMethod.TopicCards => "话题卡",
        FetchMethod.SpaceOpusFeed => "空间流",
        FetchMethod.ArticleSearch => "文章搜",
        FetchMethod.VideoSearch => "视频搜",
        FetchMethod.MembersMid => "评论@",
        FetchMethod.UserSearch => "用户搜",
        FetchMethod.TopicAuthorTable => "作者表",
        FetchMethod.AiAssist => "AI",
        FetchMethod.Manual => "人工",
        _ => "?",
    };

    private static string KindLabel(string? kind) => kind switch
    {
        "neighbor" => "邻居",
        "candidate" => "候选",
        "uid" => "用户",
        "reader" => "读取",
        "bar" => "棒号",
        "time" => "时段",
        null or "" => "未知",
        _ => kind!,
    };

    private static string ExpectedOf(GapItem g)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(g.ExpectedName))
            parts.Add("@" + g.ExpectedName
                      + (string.IsNullOrWhiteSpace(g.ExpectedUid) ? string.Empty : $"（uid {g.ExpectedUid}）"));
        else if (!string.IsNullOrWhiteSpace(g.ExpectedUid))
            parts.Add("uid " + g.ExpectedUid);

        if (!string.IsNullOrWhiteSpace(g.ExpectedBar)) parts.Add(g.ExpectedBar + " 棒");
        if (!string.IsNullOrWhiteSpace(g.ExpectedTime)) parts.Add(g.ExpectedTime);
        if (!string.IsNullOrWhiteSpace(g.FromPostId)) parts.Add("断在 " + g.FromPostId);
        return parts.Count == 0 ? "（未给出期望对象）" : string.Join(" · ", parts);
    }

    /// <summary>
    /// 「@反指」：该节点的反向 @ 指认（向前遍历看它声明的上一棒，向后看下一棒）。
    /// 与链上相邻节点核对不上时返回 ⚠ —— 这正是链路最容易出错/最该人工复核的地方。
    /// </summary>
    private static ReverseInfo ReversePointer(ChainNode node, IReadOnlyList<ChainNode> ordered, int index)
    {
        var f = node.Facts;
        if (f is null) return new ReverseInfo("-", false);

        IEnumerable<NeighborLink> decls = node.Direction switch
        {
            WalkDirection.Forward => f.Prevs,
            WalkDirection.Backward => f.Nexts,
            _ => f.Prevs.Concat(f.Nexts),
        };

        var links = decls.Where(d => !string.IsNullOrWhiteSpace(d.Name)).ToList();
        if (links.Count == 0) return new ReverseInfo("-", false);

        var neighbors = new List<string>();
        if (index > 0) neighbors.Add(CreatorOf(ordered[index - 1].Facts));
        if (index + 1 < ordered.Count) neighbors.Add(CreatorOf(ordered[index + 1].Facts));

        bool matched = links.Any(d => neighbors.Any(n => NamesMatch(d.Name, n)));
        var text = string.Join("/", links.Take(2).Select(d => "@" + d.Name));
        return new ReverseInfo(text, !matched);
    }

    private static bool NamesMatch(string a, string b)
        => a.Length > 0 && b.Length > 0
           && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    // -----------------------------------------------------------------------
    // JSON 宽容读取：key 归一化 + 宽容枚举
    // -----------------------------------------------------------------------

    private static string NormalizeKeys(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteNormalized(doc.RootElement, writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNormalized(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in element.EnumerateObject())
                {
                    writer.WritePropertyName(NormalizeKey(p.Name));
                    WriteNormalized(p.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteNormalized(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>把 start_post_id / StartPostId / start-post-id 统一成 StartPostId。</summary>
    private static string NormalizeKey(string name)
    {
        if (name.IndexOf('_') < 0 && name.IndexOf('-') < 0) return name;
        var parts = name.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(name.Length);
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1) sb.Append(part.AsSpan(1));
        }
        return sb.ToString();
    }

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt32(out var n)) return (T)Enum.ToObject(typeof(T), n);
                throw new JsonException($"无法把数字解析为 {typeof(T).Name}");
            }
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                if (Enum.TryParse<T>(s, ignoreCase: true, out var parsed)) return parsed;
                if (int.TryParse(s, out var i)) return (T)Enum.ToObject(typeof(T), i);
                throw new JsonException($"无法把「{s}」解析为 {typeof(T).Name}");
            }
            if (reader.TokenType == JsonTokenType.Null) return default;
            throw new JsonException($"无法把 {reader.TokenType} 解析为 {typeof(T).Name}");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
