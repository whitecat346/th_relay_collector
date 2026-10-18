#nullable enable
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
    /// <summary>读配置并构造；未开启/无配置返回 null。</summary>
    public static AiAssist? Load(ChainHttp http, string configPath, int budget, bool yes, Action<string>? log)
        => null; // 尚未实现（由 Agent「ui+ai」填充）

    /// <inheritdoc />
    public bool Enabled => false;

    /// <summary>ai-log.jsonl 的落盘路径（由调用方按 work/&lt;slug&gt;/ 指定）。</summary>
    public string? LogPath { get; set; }

    /// <inheritdoc />
    public AiVerdict? Ask(AiRequest request, Action<string>? log, CancellationToken ct)
        => null; // 尚未实现
}
