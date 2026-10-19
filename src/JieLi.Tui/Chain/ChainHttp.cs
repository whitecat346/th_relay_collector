#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace JieLi.Tui.Chain;

/// <summary>HTTP 层的可调项（都来自 Q23 的限速与退避约定）。</summary>
public sealed class ChainHttpOptions
{
    /// <summary>串行请求的最小间隔（毫秒）。Q23：400–800ms 抖动。</summary>
    public int MinDelayMs { get; init; } = 400;
    public int MaxDelayMs { get; init; } = 800;

    /// <summary>遇到 412/-352 风控时的退避次数。</summary>
    public int MaxRetry { get; init; } = 3;
    /// <summary>每次退避的等待（毫秒）。Q23：3s。</summary>
    public int BackoffMs { get; init; } = 3000;

    /// <summary>可选登录态（cookie.txt）。Q9：做但永不强制。</summary>
    public string? CookiePath { get; init; }

    /// <summary>单次请求超时。</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// 是否先初始化「像浏览器」的会话 cookie（访问首页让服务器种 buvid3/b_nut）。
    /// **默认关**：实测（2026-10-19，同一台机器同一 IP，相隔数秒）只要带着这套 cookie 请求
    /// 搜索接口就 100% 吃 v_voucher，而完全不带 cookie 的裸请求反而稳定返回结果。
    /// 推测是「只带了 buvid3/b_nut 却缺 buvid4/_uuid/b_lsid 等其余指纹」这种**残缺组合**被判定异常。
    /// 需要时用环境变量 JIELI_SESSION=1 打开（例如将来补全整套指纹再试）。
    /// </summary>
    public bool UseSessionCookies { get; init; }

    /// <summary>
    /// 是否对 /wbi/ 端点做 wbi 签名（环境变量 JIELI_WBI=0 可关）。
    /// </summary>
    public bool UseWbiSign { get; init; } = true;
}

/// <summary>
/// 接力链路遍历专用的 HTTP 封装。
///
/// 为什么不直接复用 <c>Data/BilibiliClient</c>：那个类只覆盖话题接口，
/// 而链路遍历要打搜索、空间 opus/feed、视频详情、相关推荐、评论等多个端点，
/// 且需要 412/-352 退避与可选登录态。**刻意不改 BilibiliClient**，
/// 以免话题管线（已在生产使用的路径）承担本次改动的风险。
///
/// 为什么不异步：既有采集器与 TUI 都是同步调用（GetAwaiter().GetResult()），
/// 保持同构，避免在这轮引入 async 污染。
/// </summary>
public sealed class ChainHttp : IDisposable
{
    /// <summary>
    /// 浏览器 UA。用户实测「浏览器访问正常、exe 被风控」——Chrome 126 的 UA 本身没问题，
    /// 但只带 UA 是不够的：还要补齐 sec-ch-ua / sec-fetch-* 这一套、拿到 buvid3/buvid4 这类
    /// 设备指纹 cookie、并对 /wbi/ 端点做 wbi 签名，才更像真实浏览器。
    /// </summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string SecChUa =
        "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"";

    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly ChainHttpOptions _opt;
    private readonly Random _rng = new();
    private DateTime _lastUtc = DateTime.MinValue;
    private bool _hasRequested;

    /// <summary>会话 cookie（buvid3/buvid4/b_nut）是否已初始化。</summary>
    private bool _sessionReady;

    /// <summary>wbi 混淆键（img_key+sub_key 混合后的 32 位）。null = 还没取到。</summary>
    private string? _wbiMixin;
    private DateTime _wbiFetchedUtc = DateTime.MinValue;

    private bool _useSession = true;
    private bool _useWbi = true;

    public ChainHttp(ChainHttpOptions? options = null)
    {
        _opt = options ?? new ChainHttpOptions();
        // 便于现场排障：环境变量可关掉「会话 cookie / wbi 签名」这两项实验性行为
        if (Environment.GetEnvironmentVariable("JIELI_SESSION") == "0") _useSession = false;
        if (Environment.GetEnvironmentVariable("JIELI_WBI") == "0") _useWbi = false;
        _useSession = _useSession && _opt.UseSessionCookies;
        _useWbi = _useWbi && _opt.UseWbiSign;

        _handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
        };
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        // 注意：**GET 不发 Origin**。浏览器只在跨域 POST/部分 CORS 请求上带 Origin，
        // 每个匿名 GET 都带 Origin 反而是很显眼的机器人特征（早先版本一直带着）。

        var cookie = LoadCookie(_opt.CookiePath);
        if (cookie is { Length: > 0 })
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
            UsingLoginCookie = true;
        }
    }

    /// <summary>本次运行是否带了登录态（报告里必须注明）。</summary>
    public bool UsingLoginCookie { get; }

    /// <summary>已发出的请求数（报告与自检用）。</summary>
    public int RequestCount { get; private set; }

    /// <summary>
    /// 取一段文本（JSON 或 HTML）。
    /// 对 HTTP 412 与 JSON 里 code=-352 做退避重试；重试仍失败抛 <see cref="ChainFetchException"/>。
    /// </summary>
    public string GetText(string url, string? referer = null, bool allowHtml = false, CancellationToken ct = default)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _opt.MaxRetry; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var body = GetOnce(url, referer, ct);
                if (!allowHtml && body.TrimStart().StartsWith('<'))
                    throw new ChainFetchException($"端点返回 HTML 而非 JSON（可能被风控或端点变更）：{url}");

                // JSON 里的风控码：-352 风控校验失败 / -401 / -403 未登录
                if (!allowHtml && LooksLikeRiskControl(body))
                {
                    last = new ChainFetchException($"接口返回风控码：{ExtractCode(body)} —— {url}");
                    Backoff(attempt, ct);
                    continue;
                }
                return body;
            }
            catch (ChainHttpStatusException ex) when (ex.StatusCode is 412 or 429)
            {
                last = ex;
                Backoff(attempt, ct);
            }
            catch (ChainFetchException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= _opt.MaxRetry) break;
                Backoff(attempt, ct);
            }
        }
        throw new ChainFetchException($"请求失败（已重试 {_opt.MaxRetry} 次）：{url}\n  {last?.Message}", last ?? new Exception());
    }

    /// <summary>取 JSON 并验证 code==0；非 0 时抛异常（调用方不必重复判 code）。</summary>
    public string GetApiJson(string url, string? referer = null, CancellationToken ct = default)
    {
        var json = GetText(url, referer, allowHtml: false, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.GetInt32() != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new ChainFetchException($"接口 code={code.GetInt32()} message={msg} —— {url}");
        }

        // 风控挑战：搜索类接口在疑似自动化时回 code=0 + data={"v_voucher":"voucher_..."}，
        // 没有 result 数组。**必须单独识别**，否则会被当成「搜到 0 条」，
        // 让人误以为活动里真的没有视频/文章（实测就是这么漏掉两棒的）。
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("v_voucher", out _))
        {
            throw new ChainFetchException($"接口返回风控挑战 v_voucher（这不是「没搜到」，请稍后重试或人工提供链接）—— {url}");
        }

        return json;
    }

    private string GetOnce(string url, string? referer, CancellationToken ct)
    {
        // ① 先确保会话 cookie 与 wbi 键就绪（只做一次，失败也只是退化成"裸请求"，不阻断）
        if (_useSession) EnsureSession(ct);
        var target = url;
        if (_useWbi && url.Contains("/wbi/", StringComparison.Ordinal))
            target = SignWbiIfPossible(url, ct);

        Throttle(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        var isDoc = url.Contains("/opus/", StringComparison.Ordinal) || !url.Contains("api.bilibili.com", StringComparison.Ordinal);
        if (isDoc)
        {
            // 文档请求（opus 页）：按浏览器导航的样子发
            req.Headers.Referrer = new Uri(referer ?? "https://www.bilibili.com/");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            req.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
            req.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
            req.Headers.TryAddWithoutValidation("sec-fetch-site", "none");
            req.Headers.TryAddWithoutValidation("sec-fetch-user", "?1");
        }
        else
        {
            req.Headers.Referrer = new Uri(referer ?? "https://www.bilibili.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
            req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
            req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
        }

        using var resp = _http.Send(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        _lastUtc = DateTime.UtcNow;
        RequestCount++;

        if (!resp.IsSuccessStatusCode)
            throw new ChainHttpStatusException((int)resp.StatusCode, resp.ReasonPhrase, url, body);
        return body;
    }

    // ---------------------------------------------------------------- 会话与 wbi

    /// <summary>
    /// 初始化「像浏览器」的会话：先访问一次首页拿 buvid3/b_nut，再向官方设备指纹接口要
    /// buvid3/buvid4 并写进 cookie 容器。只做一次；任何失败都只记一次日志、继续裸请求。
    /// 为什么这么做：用户实测浏览器访问正常而 exe 被风控，差异之一就是浏览器有这套指纹 cookie。
    /// </summary>
    private void EnsureSession(CancellationToken ct)
    {
        if (_sessionReady) return;
        _sessionReady = true;
        try
        {
            using var home = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
            home.Headers.Referrer = new Uri("https://www.bilibili.com/");
            home.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            home.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
            home.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
            home.Headers.TryAddWithoutValidation("sec-fetch-site", "none");
            using var resp = _http.Send(home, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SessionNote = $"首页会话初始化失败（继续裸请求）：{ex.Message}";
        }

        // ⚠ 刻意**不**调用 x/frontend/finger/spi 去注入 buvid3/buvid4。
        // 实测（同一台机器、同一 IP、相隔数秒）：
        //   A 注入 buvid + wbi 签名  → v_voucher
        //   B 只做 wbi 签名（不注入）→ 正常返回 9 条
        //   C 注入 buvid（不签名）   → v_voucher
        //   D 都不做（只补浏览器头） → 正常返回 9 条
        // 原因是首页已经给 .bilibili.com 域种了 buvid3/b_nut，再对 www/api 子域补一份同名的
        // 就会在 Cookie 头里出现重复 buvid3 —— 这是非常显眼的异常特征，直接被挑战。
        // 结论：只用首页自然种下的 cookie，绝不手工拼 buvid。
    }

    /// <summary>会话初始化的人话结论（进日志/报告用）。</summary>
    public string? SessionNote { get; private set; }

    /// <summary>
    /// 对 /wbi/ 端点做 wbi 签名（wts + w_rid）。浏览器会对这些接口签名，未签名的匿名请求
    /// 更容易被风控挑战（实测 v_voucher）。取不到键时原样返回，不阻断请求。
    /// </summary>
    private string SignWbiIfPossible(string url, CancellationToken ct)
    {
        var mixin = EnsureWbi(ct);
        if (mixin is null) return url;

        var qIndex = url.IndexOf('?');
        var basePart = qIndex < 0 ? url : url[..qIndex];
        var query = qIndex < 0 ? "" : url[(qIndex + 1)..];

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            var k = eq < 0 ? kv : kv[..eq];
            var v = eq < 0 ? "" : kv[(eq + 1)..];
            pairs.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(k), Uri.UnescapeDataString(v)));
        }
        pairs.RemoveAll(p => p.Key is "w_rid" or "wts");
        pairs.Add(new KeyValuePair<string, string>("wts",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        pairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var sb = new StringBuilder();
        foreach (var p in pairs)
        {
            var value = new string(p.Value.Where(c => c is not ('!' or '\'' or '(' or ')' or '*')).ToArray());
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(p.Key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        var queryForSign = sb.ToString();
        var sign = Md5Hex(queryForSign + mixin);
        return $"{basePart}?{queryForSign}&w_rid={sign}";
    }

    /// <summary>取 wbi 混淆键（缓存 30 分钟）。nav 接口匿名可用，返回 wbi_img 的 img/sub key。</summary>
    private string? EnsureWbi(CancellationToken ct)
    {
        if (_wbiMixin is not null && (DateTime.UtcNow - _wbiFetchedUtc).TotalMinutes < 30) return _wbiMixin;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            req.Headers.Referrer = new Uri("https://www.bilibili.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            using var resp = _http.Send(req, HttpCompletionOption.ResponseContentRead, ct);
            var json = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("wbi_img", out var img)
                || img.ValueKind != JsonValueKind.Object) return _wbiMixin;

            var imgKey = FileKeyOf(img, "img_url");
            var subKey = FileKeyOf(img, "sub_url");
            if (imgKey.Length < 32 || subKey.Length < 32) return _wbiMixin;

            var raw = imgKey + subKey;
            var sb = new StringBuilder(32);
            foreach (var idx in WbiMixinTab) sb.Append(raw[idx]);
            _wbiMixin = sb.ToString();
            _wbiFetchedUtc = DateTime.UtcNow;
            return _wbiMixin;
        }
        catch
        {
            return _wbiMixin; // 拿不到就让请求裸奔，不阻断
        }
    }

    private static string FileKeyOf(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var url = v.GetString() ?? "";
        var slash = url.LastIndexOf('/');
        var file = slash >= 0 ? url[(slash + 1)..] : url;
        var dot = file.IndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static string Md5Hex(string text)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>bilibili wbi 混淆表（社区公开常量，与浏览器行为一致）。</summary>
    private static readonly int[] WbiMixinTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52,
    };

    private void Throttle(CancellationToken ct)
    {
        if (!_hasRequested) { _hasRequested = true; return; }
        var elapsed = (DateTime.UtcNow - _lastUtc).TotalMilliseconds;
        int target = _opt.MinDelayMs + _rng.Next(Math.Max(1, _opt.MaxDelayMs - _opt.MinDelayMs + 1));
        var wait = target - (int)elapsed;
        if (wait > 0) Task.Delay(wait, ct).GetAwaiter().GetResult();
    }

    private void Backoff(int attempt, CancellationToken ct)
    {
        if (attempt >= _opt.MaxRetry) return;
        var wait = _opt.BackoffMs * (attempt + 1);
        Task.Delay(wait, ct).GetAwaiter().GetResult();
    }

    private static bool LooksLikeRiskControl(string body)
    {
        var trimmed = body.Length > 400 ? body[..400] : body;
        return trimmed.Contains("\"code\":-352", StringComparison.Ordinal)
            || trimmed.Contains("\"code\": -352", StringComparison.Ordinal)
            || trimmed.Contains("\"code\":-401", StringComparison.Ordinal)
            || trimmed.Contains("\"code\":-403", StringComparison.Ordinal);
    }

    private static string ExtractCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c)) return c.ToString();
        }
        catch { /* 非 JSON */ }
        return "?";
    }

    private static string? LoadCookie(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var text = File.ReadAllText(path, Encoding.UTF8).Trim();
        if (text.Length == 0) return null;
        // 允许两种写法：直接粘 Cookie 头；或每行 name=value
        if (text.Contains('=') && !text.Contains('\n')) return text;
        var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).Where(l => l.Contains('='));
        var joined = string.Join("; ", parts);
        return joined.Length > 0 ? joined : null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>带状态码的请求失败（412/429 会被上层退避重试）。</summary>
public sealed class ChainHttpStatusException : Exception
{
    public ChainHttpStatusException(int statusCode, string? reason, string url, string body)
        : base($"HTTP {statusCode} {reason} —— {url}\n  {(body.Length > 160 ? body[..160] : body)}")
    {
        StatusCode = statusCode;
        Url = url;
    }

    public int StatusCode { get; }
    public string Url { get; }
}

/// <summary>审计计时小工具（每步耗时进 chain.json）。</summary>
public readonly struct AuditScope : IDisposable
{
    private readonly Stopwatch _sw;
    private readonly ChainResult _result;
    private readonly ChainAuditEntry _entry;

    public AuditScope(ChainResult result, ChainAuditEntry entry)
    {
        _result = result;
        _entry = entry;
        _sw = Stopwatch.StartNew();
    }

    public void MarkSuccess(string? detail = null)
    {
        _entry.Success = true;
        if (detail is not null) _entry.Detail = detail;
    }

    public void Dispose()
    {
        _entry.ElapsedMs = _sw.ElapsedMilliseconds;
        _result.Audit.Add(_entry);
    }

    public static AuditScope Begin(ChainResult result, string step, WalkDirection dir, FetchMethod method, string detail)
        => new(result, new ChainAuditEntry { Step = step, Direction = dir, Method = method, Detail = detail });
}
