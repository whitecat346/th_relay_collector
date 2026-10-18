#nullable enable
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「ui+ai」
// 只允许改本文件与 Tui/ChainView.cs / Tui/ReviewView.cs。
//
// 职责：AI 辅助判定（Q7 / Q16）。**末位手段：只在所有非 AI 手段失败后调用。**
//   * 默认关闭；`--ai` 显式开启；逐次人工确认（`--ai-yes` 免）；`--ai-budget` 上限（建议 20）
//   * 配置读 relay-tool/ai.yml：base_url / api_key / model（OpenAI 兼容 /chat/completions）
//   * **绝不发整页 HTML**，只发片段 + 候选 uid/name + 期望棒号时段
//   * 每次调用写 work/<id>/ai-log.jsonl（请求摘要、返回、判定、耗时）
//   * 配置文件缺失或 Enabled=false 时 Load 返回 null，调用方据此完全不调 AI
// ===========================================================================

/// <summary>OpenAI 兼容的 AI 辅助判定。</summary>
public sealed class AiAssist : IAiAssist
{
    /// <summary>
    /// 契约里 AiRequest.Snippet 上限 1500 字；这里再收紧到 1200。
    /// 为什么：AI 是末位手段，片段越短越省 token、越不容易被无关内容带偏，也天然避免整页 HTML 被误发。
    /// </summary>
    private const int MaxSnippetChars = 1200;

    /// <summary>ai-log.jsonl 单行里保留的回复原文上限（够复盘，又不至于让日志无限膨胀）。</summary>
    private const int MaxReplyLogChars = 4000;

    /// <summary>序列化请求/日志用的选项：中文不转义，日志与请求体都保持可读。</summary>
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 为什么不复用调用方传进来的 ChainHttp：它只实现了 GET（且带 B 站 Referer/限速），
    // 而这里要 POST 一个非 B 站域名；ChainHttp 属别人正在改的文件，不能动。
    // 保持同步：与既有 TUI/采集器同构，不在这轮引入 async 污染。
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _budget;
    private readonly bool _yes;
    private int _used;

    private AiAssist(string baseUrl, string apiKey, string model, int budget, bool yes)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _budget = Math.Max(0, budget);
        _yes = yes;
    }

    /// <summary>读配置并构造；未开启/无配置返回 null。</summary>
    public static AiAssist? Load(ChainHttp http, string configPath, int budget, bool yes, Action<string>? log)
    {
        // http 属于冻结签名的一部分：当前 AI 调用走独立的 POST 客户端（见 _http 的注释）。
        // 保留它，是为了将来若把 AI 也纳入同一条限速/登录态通道，不必改 Program.cs 的调用点。
        _ = http;

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            log?.Invoke($"AI 配置无效：找不到 {configPath}。需要 base_url / api_key / model 三个字段。");
            return null;
        }

        MiniYaml.Node root;
        try
        {
            // 复用 ArchiveMeta 里零依赖的 MiniYaml：ai.yml 只有一层 key: value，不值得再引一个 YAML 库。
            root = MiniYaml.Parse(configPath);
        }
        catch (Exception ex)
        {
            log?.Invoke("AI 配置无效：读取 ai.yml 失败 —— " + ex.Message);
            return null;
        }

        var baseUrl = root.Get("base_url")?.Trim();
        var apiKey = root.Get("api_key")?.Trim();
        var model = root.Get("model")?.Trim();

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(baseUrl)) missing.Add("base_url");
        if (string.IsNullOrWhiteSpace(apiKey)) missing.Add("api_key");
        if (string.IsNullOrWhiteSpace(model)) missing.Add("model");
        if (missing.Count > 0)
        {
            log?.Invoke($"AI 配置无效：{configPath} 缺少字段 {string.Join(" / ", missing)}。");
            return null;
        }

        // base_url 必须能拼出 /chat/completions：非法就直接判定"没配置"，
        // 让调用方完全不调 AI，而不是等第一次 Ask 才在遍历中途失败。
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            log?.Invoke($"AI 配置无效：base_url 不是合法的 http(s) 地址：「{baseUrl}」。");
            return null;
        }

        var ai = new AiAssist(baseUrl!, apiKey!, model!, budget, yes);
        var where = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        log?.Invoke($"AI 已就绪：{model} @ {where}{uri.AbsolutePath.TrimEnd('/')}"
                    + $"（本次预算 {ai._budget} 次，逐次确认{(yes ? "关闭" : "开启")}）");
        return ai;
    }

    /// <inheritdoc />
    public bool Enabled => true;

    /// <summary>ai-log.jsonl 的落盘路径（由调用方按 work/&lt;slug&gt;/ 指定）。</summary>
    public string? LogPath { get; set; }

    /// <inheritdoc />
    public AiVerdict? Ask(AiRequest request, Action<string>? log, CancellationToken ct)
    {
        if (request is null) return null;

        if (_used >= _budget)
        {
            log?.Invoke($"⚠ AI 预算耗尽（已用 {_used}/{_budget} 次），跳过本次判定。");
            return null;
        }

        // 逐次确认：默认每次调用前都要人点头，避免 AI 在无人值守时把预算偷偷烧完。
        if (!_yes && !Confirm(request, log)) return null;

        _used++;
        var snippet = PrepareSnippet(request.Snippet, log);
        var started = Stopwatch.StartNew();
        string? reply = null;
        AiVerdict? verdict = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            req.Content = new StringContent(BuildPayload(request, snippet), Encoding.UTF8, "application/json");

            using var resp = _http.Send(req, HttpCompletionOption.ResponseContentRead, ct);
            var body = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                throw new ChainFetchException(
                    $"AI 接口返回 HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}；响应片段：{Head(body)}");

            reply = ExtractAssistantContent(body);
            verdict = ParseVerdict(reply, log);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 真的是调用方取消（Ctrl+C / 截止时间）就原样向上抛，不能被当成"AI 没答"吞掉。
            started.Stop();
            log?.Invoke("AI 调用已取消。");
            AppendLog(request, snippet.Length, reply, null, started.ElapsedMilliseconds, "cancelled", log);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            started.Stop();
            log?.Invoke("AI 调用失败（超时）：" + ex.Message);
            AppendLog(request, snippet.Length, reply, null, started.ElapsedMilliseconds, "timeout", log);
            return null;
        }
        catch (Exception ex)
        {
            started.Stop();
            log?.Invoke("AI 调用失败：" + ex.Message);
            AppendLog(request, snippet.Length, reply, null, started.ElapsedMilliseconds, "error", log);
            return null;
        }

        started.Stop();
        if (verdict is not null)
        {
            log?.Invoke(verdict.Confident
                ? $"AI 判定：确信 {verdict.PickName ?? "?"}（uid {verdict.PickUid ?? "无"}，"
                  + $"棒 {verdict.Bar?.ToString() ?? "?"}）：{verdict.Reason ?? "未给理由"}"
                : $"AI 判定：不确信 —— {verdict.Reason ?? "未给理由"}");
        }

        AppendLog(request, snippet.Length, reply, verdict,
                  started.ElapsedMilliseconds, verdict is null ? "unparsable" : "ok", log);
        return verdict;
    }

    // -----------------------------------------------------------------------
    // 请求构造 / 返回解析
    // -----------------------------------------------------------------------

    private string BuildPayload(AiRequest r, string snippet)
    {
        const string system =
            "你在判定一条 B 站「东方 Project 接力」帖子是不是要找的那一棒。"
            + "只依据给出的片段与候选判断，不要编造。只输出一个 JSON 对象，不要解释、不要代码块。";

        var user = new StringBuilder();
        user.AppendLine("任务：" + PurposeText(r.Purpose));
        if (!string.IsNullOrWhiteSpace(r.RelayName)) user.AppendLine("活动：" + r.RelayName);
        if (r.ExpectedBar is { } bar) user.AppendLine("期望棒号：" + bar);
        if (!string.IsNullOrWhiteSpace(r.ExpectedTime)) user.AppendLine("期望时段：" + r.ExpectedTime);
        if (r.Candidates.Count > 0)
        {
            user.AppendLine("候选（可能与片段里的 @ 对应）：");
            foreach (var cand in r.Candidates.Take(30))
                user.AppendLine($"- {cand.Name}{(string.IsNullOrWhiteSpace(cand.Uid) ? string.Empty : $"（uid {cand.Uid}）")}");
        }
        if (!string.IsNullOrWhiteSpace(r.Question)) user.AppendLine("问题：" + r.Question);
        user.AppendLine("片段（已截断，不含整页 HTML）：");
        user.AppendLine(snippet);
        user.AppendLine();
        user.AppendLine("只回 JSON："
            + "{\"confident\":true|false,\"uid\":\"命中候选的 uid 或 null\",\"name\":\"命中候选的昵称或 null\","
            + "\"bar\":数字或null,\"reason\":\"不超过 40 字的中文理由\"}");

        // 用匿名对象 + 手写字段名：OpenAI 兼容接口只认 model/messages/temperature，不需要契约 DTO 的 PascalCase。
        var payload = new
        {
            model = _model,
            temperature = 0,
            max_tokens = 300,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user.ToString() },
            },
        };
        return JsonSerializer.Serialize(payload, PayloadJson);
    }

    private static string PurposeText(string purpose) => purpose switch
    {
        "uid" => "从片段里找出被 @ 的用户的真实 uid / 昵称。",
        "neighbor" => "判断片段里声明的「上一棒 / 下一棒」分别是谁。",
        "bar" => "判断这条帖子自己声明的棒号（数字或 EX）。",
        _ => "在候选里判断哪一条才是要找的那一根棒（找不到就不确信）。",
    };

    /// <summary>
    /// 逐次确认。用 Console.Error 提问（不污染 stdout 的 CSV/MD 契约），
    /// 非交互环境（stdin 重定向/EOF）一律当作"否"，绝不能卡住遍历。
    /// </summary>
    private bool Confirm(AiRequest request, Action<string>? log)
    {
        if (Console.IsInputRedirected)
        {
            log?.Invoke("AI 需要逐次确认，但当前不是交互终端；本次跳过（等同回答 N）。");
            return false;
        }

        try
        {
            var hint = request.Candidates.Count > 0
                ? $"候选 {request.Candidates.Count} 个：" + string.Join("、", request.Candidates.Take(5).Select(x => x.Name))
                : "无候选";
            Console.Error.Write($"[AI] 要调用 {_model} 判定（{hint}），消耗 1 次预算（{_used}/{_budget}），继续吗？[y/N] ");
            var line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line)
                && (line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                    || line.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            log?.Invoke("已跳过本次 AI 判定（人工回答 N）。");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke("AI 逐次确认读取失败：" + ex.Message + "；本次跳过。");
            return false;
        }
    }

    /// <summary>从 OpenAI 兼容响应里取 message.content（字符串或分段数组都兼容）。</summary>
    private static string ExtractAssistantContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!TryGet(doc.RootElement, "choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new ChainFetchException("AI 响应缺少 choices：" + Head(body));

        var choice = choices[0];
        if (TryGet(choice, "message", out var msg) && TryGet(msg, "content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) parts.Add(part.GetString() ?? string.Empty);
                    else if (TryGet(part, "text", out var t) && t.ValueKind == JsonValueKind.String)
                        parts.Add(t.GetString() ?? string.Empty);
                }
                return string.Join("\n", parts);
            }
        }
        if (TryGet(choice, "text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString() ?? string.Empty;

        throw new ChainFetchException("AI 响应里找不到 message.content：" + Head(body));
    }

    private static AiVerdict? ParseVerdict(string reply, Action<string>? log)
    {
        var jsonText = ExtractJsonObject(reply);
        if (jsonText is null)
        {
            log?.Invoke("AI 返回无法解析：回复里没有 JSON 对象（原文已记 ai-log.jsonl）。");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                log?.Invoke("AI 返回无法解析：JSON 不是对象（原文已记 ai-log.jsonl）。");
                return null;
            }

            return new AiVerdict
            {
                Confident = GetBool(root, "confident"),
                PickUid = GetString(root, "uid"),
                PickName = GetString(root, "name"),
                Bar = GetInt(root, "bar"),
                Reason = GetString(root, "reason"),
                RawReply = reply,
            };
        }
        catch (JsonException ex)
        {
            log?.Invoke("AI 返回 JSON 解析失败：" + ex.Message + "（原文已记 ai-log.jsonl）。");
            return null;
        }
    }

    /// <summary>模型常把 JSON 包在 ```json 或解释文字里，这里抠出第一个 { 到最后一个 } 再解析。</summary>
    private static string? ExtractJsonObject(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : null;
    }

    /// <summary>片段双保险：剥掉疑似 HTML 标签 + 截到 1200 字。调用方已截过也要再截一次。</summary>
    private static string PrepareSnippet(string? raw, Action<string>? log)
    {
        var s = raw ?? string.Empty;
        if (s.Length == 0) return s;

        if (LooksLikeHtml(s))
        {
            s = Regex.Replace(s, @"<[^>]{1,400}>", " ");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            log?.Invoke("AI 片段疑似含 HTML 标签，已剥离标签、只发纯文本（绝不发送整页 HTML）。");
        }

        if (s.Length > MaxSnippetChars)
        {
            var cut = MaxSnippetChars;
            if (char.IsHighSurrogate(s[cut - 1])) cut--; // 别把 emoji 劈成半个代理对
            s = s[..cut];
        }
        return s;
    }

    private static bool LooksLikeHtml(string s) =>
        s.Contains("</", StringComparison.Ordinal)
        || s.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<div", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<script", StringComparison.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // 落盘：work/<slug>/ai-log.jsonl，一行一次调用
    // -----------------------------------------------------------------------

    private void AppendLog(AiRequest request, int snippetLen, string? reply, AiVerdict? verdict,
                           long elapsedMs, string status, Action<string>? log)
    {
        var path = LogPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var record = new
            {
                time = DateTimeOffset.Now.ToString("O"),
                purpose = request.Purpose,
                snippet_len = snippetLen,
                candidates = request.Candidates.Count,
                expected_bar = request.ExpectedBar,
                expected_time = request.ExpectedTime,
                relay = request.RelayName,
                question = request.Question,
                status,
                reply = TruncateForLog(reply),
                verdict = verdict is null
                    ? null
                    : new
                    {
                        confident = verdict.Confident,
                        uid = verdict.PickUid,
                        name = verdict.PickName,
                        bar = verdict.Bar,
                        reason = verdict.Reason,
                    },
                elapsed_ms = elapsedMs,
                used = _used,
                budget = _budget,
            };

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, JsonSerializer.Serialize(record, PayloadJson) + Environment.NewLine,
                               new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 日志写不进去不能反过来毁掉判定流程，但要让调用方看见。
            log?.Invoke("⚠ AI 日志写入失败：" + ex.Message);
        }
    }

    private static string? TruncateForLog(string? reply)
    {
        if (reply is null) return null;
        if (reply.Length <= MaxReplyLogChars) return reply;
        var cut = MaxReplyLogChars;
        if (char.IsHighSurrogate(reply[cut - 1])) cut--;
        return reply[..cut] + "…";
    }

    // -----------------------------------------------------------------------
    // 小工具（大小写无关取值，容忍模型把键写成 Confident/uid 等）
    // -----------------------------------------------------------------------

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value)) return true;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool GetBool(JsonElement e, string name)
        => TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static string Head(string s, int n = 200)
        => s.Length <= n ? s : s[..n] + "…";
}
