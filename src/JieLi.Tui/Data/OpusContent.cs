#nullable enable
using System.Text;
using System.Text.Json;

namespace JieLi.Tui.Data;

/// <summary>正文里的一段富文本片段（保留 @ 与跳转链接的结构化信息）。</summary>
public sealed class BodySpan
{
    public string Text { get; set; } = string.Empty;
    /// <summary>跳转链接（`TEXT_NODE_TYPE_RICH` 的 jump_url，如 //space.bilibili.com/123）。</summary>
    public string? JumpUrl { get; set; }
    /// <summary>@ 提及的 UID（`TEXT_NODE_TYPE_AT`）。</summary>
    public string? AtMid { get; set; }
    /// <summary>@ 提及的昵称。</summary>
    public string? AtName { get; set; }
}

/// <summary>一条 opus 帖子的完整可解析内容（正文 + 评论）。</summary>
public sealed class OpusContent
{
    public string OpusId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public long PubTs { get; set; }

    /// <summary>评论接口需要的 oid 与 type（opus 的 comment_type 实测为 12）。</summary>
    public string? CommentOid { get; set; }
    public int CommentType { get; set; } = 12;

    /// <summary>正文段落（已展开为片段序列，保留换行）。</summary>
    public List<BodySpan> BodySpans { get; } = new();

    /// <summary>评论（含楼中楼，按接口顺序）。</summary>
    public List<CommentItem> Comments { get; } = new();

    /// <summary>正文的纯文本视图（@ 还原为 @名字，链接附在文字后）。</summary>
    public string BodyText => RenderSpans(BodySpans);

    /// <summary>评论的纯文本视图。</summary>
    public string CommentsText => string.Join("\n", Comments.Select(c => c.Text));

    /// <summary>正文与评论文本合一，供既有 BarParser 直接复用。</summary>
    public string MergedText
    {
        get
        {
            var sb = new StringBuilder(BodyText);
            if (Comments.Count > 0)
            {
                sb.Append("\n\n【评论区】\n");
                sb.Append(CommentsText);
            }
            return sb.ToString();
        }
    }

    /// <summary>正文 + 评论里出现的所有跳转链接（供链路遍历）。</summary>
    public IEnumerable<BodySpan> AllSpans()
    {
        foreach (var s in BodySpans) yield return s;
        foreach (var c in Comments)
            foreach (var s in c.Spans)
                yield return s;
    }

    public static string RenderSpans(IEnumerable<BodySpan> spans)
    {
        var sb = new StringBuilder();
        foreach (var s in spans)
        {
            if (s.AtMid is not null)
                sb.Append('@').Append(s.AtName ?? s.AtMid);
            else
                sb.Append(s.Text);
            if (s.JumpUrl is { Length: > 0 })
                sb.Append('<').Append(s.JumpUrl).Append('>');
        }
        return sb.ToString();
    }
}

/// <summary>一条评论（或楼中楼）。</summary>
public sealed class CommentItem
{
    public string Rpid { get; set; } = string.Empty;
    public long Mid { get; set; }
    public string UserName { get; set; } = string.Empty;
    public long Ctime { get; set; }
    /// <summary>该评论的作者是否就是帖主（`member.mid == detail.basic.uid`）。</summary>
    public bool IsUp { get; set; }
    /// <summary>是否置顶评论。</summary>
    public bool IsPinned { get; set; }
    /// <summary>是否楼中楼。</summary>
    public bool IsSubReply { get; set; }

    public List<BodySpan> Spans { get; } = new();
    public string Text => OpusContent.RenderSpans(Spans);
}

/// <summary>
/// 单条 opus 页面的内容提取。
/// 页面是**客户端渲染**的：正文不在 HTML 里，而在 `__INITIAL_STATE__` 的
/// `detail.modules[MODULE_TYPE_CONTENT].module_content.paragraphs[]`。
/// ⚠ 正文**不在** `module_content.text`（实测为空），必须走 paragraphs。
/// ⚠ `__INITIAL_STATE__` 的结尾形式每次不同（`</script>` / `(function` 等），
///    必须用**括号配平**提取，不能靠正则。
/// </summary>
public static class OpusContentExtractor
{
    /// <summary>从页面 HTML 里提取并解析 `__INITIAL_STATE__`。</summary>
    public static JsonElement? ExtractInitialState(string html)
    {
        const string key = "__INITIAL_STATE__";
        int i = html.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int j = html.IndexOf('{', i);
        if (j < 0) return null;

        int depth = 0;
        bool inStr = false, esc = false;
        for (int k = j; k < html.Length; k++)
        {
            char c = html[k];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(html[j..(k + 1)]);
                        return doc.RootElement.Clone();
                    }
                    catch { return null; }
                }
            }
        }
        return null;
    }

    public static OpusContent? FromHtml(string html)
    {
        var state = ExtractInitialState(html);
        if (state is null) return null;
        if (!state.Value.TryGetProperty("detail", out var detail)) return null;
        return FromDetail(detail);
    }

    private static OpusContent FromDetail(JsonElement detail)
    {
        var oc = new OpusContent();
        if (detail.TryGetProperty("id_str", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            oc.OpusId = idEl.GetString() ?? string.Empty;

        if (detail.TryGetProperty("basic", out var basic))
        {
            oc.CommentOid = Str(basic, "comment_id_str");
            if (basic.TryGetProperty("comment_type", out var ct) && ct.ValueKind == JsonValueKind.Number)
                oc.CommentType = ct.GetInt32();
        }

        if (!detail.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
            return oc;

        foreach (var mod in modules.EnumerateArray())
        {
            var type = Str(mod, "module_type");
            switch (type)
            {
                case "MODULE_TYPE_AUTHOR":
                    if (mod.TryGetProperty("module_author", out var au))
                    {
                        oc.AuthorName = Str(au, "name") ?? string.Empty;
                        oc.AuthorMid = Long(au, "mid");
                        oc.PubTs = Long(au, "pub_ts");
                    }
                    break;

                case "MODULE_TYPE_TITLE":
                    if (mod.TryGetProperty("module_title", out var ti))
                        oc.Title = Str(ti, "text") ?? string.Empty;
                    break;

                case "MODULE_TYPE_CONTENT":
                    if (mod.TryGetProperty("module_content", out var content))
                        ParseParagraphs(content, oc.BodySpans);
                    break;
            }
        }
        return oc;
    }

    /// <summary>
    /// 展开 `module_content.paragraphs[]`。
    /// 实测节点类型：`TEXT_NODE_TYPE_WORD`（文字在 `word.words`）、
    /// `TEXT_NODE_TYPE_RICH`（文字在 `rich.text`/`rich.orig_text`，跳转在 `rich.jump_url`）、
    /// `TEXT_NODE_TYPE_AT`（@ 在 `at.*`）。
    /// ⚠ `@` 是**独立节点**，不在 WORDS 里 —— 漏掉它会让"上一棒 | @某人"变成"上一棒 |"。
    /// </summary>
    private static void ParseParagraphs(JsonElement content, List<BodySpan> sink)
    {
        if (!content.TryGetProperty("paragraphs", out var paras) || paras.ValueKind != JsonValueKind.Array)
            return;

        foreach (var p in paras.EnumerateArray())
        {
            // 段落内可能有图片/链接卡片等，先把可提取的文字拿到
            if (p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object
                && text.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in nodes.EnumerateArray())
                    ParseNode(n, sink);
            }
            else if (p.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.Object)
            {
                var url = Str(pic, "url");
                if (url is { Length: > 0 }) sink.Add(new BodySpan { Text = "[图片]", JumpUrl = url });
            }

            sink.Add(new BodySpan { Text = "\n" });
        }
    }

    private static void ParseNode(JsonElement n, List<BodySpan> sink)
    {
        var type = Str(n, "type");
        switch (type)
        {
            case "TEXT_NODE_TYPE_WORD":
                if (n.TryGetProperty("word", out var w))
                    sink.Add(new BodySpan { Text = Str(w, "words") ?? string.Empty });
                break;

            case "TEXT_NODE_TYPE_RICH":
                if (n.TryGetProperty("rich", out var r))
                {
                    var label = Str(r, "text") ?? Str(r, "orig_text") ?? string.Empty;
                    var richType = Str(r, "type") ?? string.Empty;

                    // ⚠ 实测：@ 提及**不是** TEXT_NODE_TYPE_AT，而是 RICH 节点，
                    //   类型在 rich.type = "RICH_TEXT_NODE_TYPE_AT"，UID 在 rich.rid，
                    //   且 jump_url 为空字符串。漏掉这一条会让"下一棒 | @某人"只拿到 "@"。
                    if (richType.Contains("AT", StringComparison.OrdinalIgnoreCase))
                    {
                        sink.Add(new BodySpan
                        {
                            Text = "@",
                            AtName = label.TrimStart('@').Trim(),
                            AtMid = Str(r, "rid") ?? Str(r, "mid"),
                            JumpUrl = NullIfEmpty(Str(r, "jump_url")),
                        });
                    }
                    else
                    {
                        sink.Add(new BodySpan
                        {
                            Text = label,
                            JumpUrl = NullIfEmpty(Str(r, "jump_url")),
                        });
                    }
                }
                break;

            case "TEXT_NODE_TYPE_AT":
                if (n.TryGetProperty("at", out var at))
                {
                    sink.Add(new BodySpan
                    {
                        Text = "@",
                        AtName = Str(at, "name") ?? Str(at, "show_name") ?? Str(at, "text"),
                        AtMid = Str(at, "rid") ?? Str(at, "mid"),
                        JumpUrl = Str(at, "jump_url"),
                    });
                }
                break;

            case "TEXT_NODE_TYPE_EMOTE":
                sink.Add(new BodySpan { Text = "[表情]" });
                break;

            default:
                // 未知节点类型：尽量把可见文字捞出来，不静默丢弃
                var t = Str(n, "text") ?? Str(n, "words");
                if (t is { Length: > 0 }) sink.Add(new BodySpan { Text = t, JumpUrl = Str(n, "jump_url") });
                break;
        }
    }

    // ---------------------------------------------------------------- 评论

    /// <summary>把 `x/v2/reply` 的 data 解析成评论列表（含楼中楼）。</summary>
    public static List<CommentItem> ParseComments(JsonElement data, long upMid)
    {
        var list = new List<CommentItem>();
        if (!data.TryGetProperty("replies", out var replies) || replies.ValueKind != JsonValueKind.Array)
            return list;

        var pinned = new HashSet<string>(StringComparer.Ordinal);
        if (data.TryGetProperty("top", out var top))
        {
            if (top.ValueKind == JsonValueKind.Object) CollectRpids(top, pinned);
            else if (top.ValueKind == JsonValueKind.Array)
                foreach (var t in top.EnumerateArray()) CollectRpids(t, pinned);
        }

        foreach (var r in replies.EnumerateArray())
        {
            list.Add(ParseOneComment(r, upMid, pinned, isSub: false));
            if (r.TryGetProperty("replies", out var subs) && subs.ValueKind == JsonValueKind.Array)
                foreach (var s in subs.EnumerateArray())
                    list.Add(ParseOneComment(s, upMid, pinned, isSub: true));
        }
        return list;
    }

    private static void CollectRpids(JsonElement obj, HashSet<string> into)
    {
        var id = Str(obj, "rpid_str") ?? Str(obj, "rpid");
        if (id is { Length: > 0 }) into.Add(id);
    }

    private static CommentItem ParseOneComment(JsonElement r, long upMid, HashSet<string> pinned, bool isSub)
    {
        var item = new CommentItem
        {
            Rpid = Str(r, "rpid_str") ?? Str(r, "rpid") ?? string.Empty,
            Ctime = Long(r, "ctime"),
            IsSubReply = isSub,
            IsPinned = pinned.Contains(Str(r, "rpid_str") ?? string.Empty),
        };

        if (r.TryGetProperty("member", out var m))
        {
            item.UserName = Str(m, "uname") ?? string.Empty;
            item.Mid = Long(m, "mid");
        }
        item.IsUp = upMid > 0 && item.Mid == upMid;

        if (r.TryGetProperty("content", out var c))
        {
            var message = Str(c, "message") ?? string.Empty;

            // @ 提及：message 里是占位符，实际信息在 members[]
            var atByPlaceholder = new Dictionary<string, (string Name, string Mid)>(StringComparer.Ordinal);
            if (c.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
            {
                foreach (var mem in members.EnumerateArray())
                {
                    var uname = Str(mem, "uname") ?? string.Empty;
                    var mid = Str(mem, "mid");
                    if (uname.Length > 0) atByPlaceholder[uname] = (uname, mid ?? string.Empty);
                }
            }

            // 链接：message 里是占位符，实际 URL 在 jump_url{} 的值里
            // 注意：视频/专栏卡片类的占位符，值是 **对象**（如 {"url":...}），不是字符串。
            // 早先直接 GetString() 会在这种评论上抛
            // InvalidOperationException: requires an element of type 'String', but ... 'Object'，
            // 导致整条视频帖读取失败（实测 BV1iapN6KEtk 就是这么漏掉的）。
            var urlByPlaceholder = new Dictionary<string, string>(StringComparer.Ordinal);
            if (c.TryGetProperty("jump_url", out var ju) && ju.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ju.EnumerateObject())
                {
                    var value = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                        JsonValueKind.Object => Str(p.Value, "url") ?? Str(p.Value, "jump_url") ?? string.Empty,
                        _ => string.Empty,
                    };
                    urlByPlaceholder[p.Name] = value;
                }
            }

            SplitCommentMessage(message, atByPlaceholder, urlByPlaceholder, item.Spans);
        }

        if (item.Spans.Count == 0 && item.UserName.Length == 0)
            item.Spans.Add(new BodySpan { Text = string.Empty });

        if (!isSub) item.Spans.Add(new BodySpan { Text = "\n" });
        return item;
    }

    /// <summary>
    /// 拆解评论文本：把 `@昵称` 占位符与链接占位符替换成结构化片段。
    /// 实测：`message` 里 @ 与链接都以文本形式出现（如 `@某人` 与裸 URL），
    /// 而 `members[]` / `jump_url{}` 给出对应关系；两者都保留即可覆盖。
    /// </summary>
    private static void SplitCommentMessage(
        string message,
        Dictionary<string, (string Name, string Mid)> ats,
        Dictionary<string, string> urls,
        List<BodySpan> sink)
    {
        int i = 0;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            sink.Add(new BodySpan { Text = sb.ToString() });
            sb.Clear();
        }

        while (i < message.Length)
        {
            // 链接（http/https 或 B站 短链）
            var rest = message[i..];
            var url = MatchUrl(rest);
            if (url is not null)
            {
                Flush();
                sink.Add(new BodySpan { Text = url, JumpUrl = url });
                i += url.Length;
                continue;
            }

            // @ 提及（取最长匹配的昵称）
            if (message[i] == '@')
            {
                string? hit = null;
                foreach (var name in ats.Keys)
                {
                    if (rest.Length > 1 + name.Length && rest[1..].StartsWith(name, StringComparison.Ordinal))
                    {
                        if (hit is null || name.Length > hit.Length) hit = name;
                    }
                }
                if (hit is not null && ats.TryGetValue(hit, out var info))
                {
                    Flush();
                    sink.Add(new BodySpan { Text = "@", AtName = info.Name, AtMid = info.Mid });
                    i += 1 + hit.Length;
                    continue;
                }
            }

            sb.Append(message[i]);
            i++;
        }
        Flush();
    }

    private static string? MatchUrl(string s)
    {
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            int end = 0;
            while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '）' && s[end] != ')') end++;
            return s[..end];
        }
        return null;
    }

    // ---------------------------------------------------------------- 工具

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null)
            : null;

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n)) return n;
        return 0;
    }
}
