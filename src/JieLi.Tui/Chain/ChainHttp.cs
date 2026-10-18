#nullable enable
using System.Diagnostics;
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
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ChainHttpOptions _opt;
    private readonly Random _rng = new();
    private DateTime _lastUtc = DateTime.MinValue;
    private bool _hasRequested;

    public ChainHttp(ChainHttpOptions? options = null)
    {
        _opt = options ?? new ChainHttpOptions();
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");

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
        return json;
    }

    private string GetOnce(string url, string? referer, CancellationToken ct)
    {
        Throttle(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(referer ?? "https://www.bilibili.com/");
        if (url.Contains("/opus/", StringComparison.Ordinal))
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");

        using var resp = _http.Send(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        _lastUtc = DateTime.UtcNow;
        RequestCount++;

        if (!resp.IsSuccessStatusCode)
            throw new ChainHttpStatusException((int)resp.StatusCode, resp.ReasonPhrase, url, body);
        return body;
    }

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
