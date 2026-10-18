#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」
// 只允许改本文件与 Chain/ChainWalker.cs / Chain/ChainWriter.cs。
//
// 职责：候选验收 —— 判定「这条候选帖子是不是上一棒/下一棒要找的那一棒」。
//
// 验收规则（Q2 / Q11 / Q15，已定，勿改语义）：
//   (反向 @uid 结构证据) OR (时间窗 ≤ WindowMinutes，默认 90 分钟)
//   两者全中 → 高置信；仅时间命中 → 中置信；仅时间且超窗 → 收下但标「近邻超窗(可能一人多棒)」并降置信；
//   只有结构证据 → 高置信（结构证据权重最高，因为一人可多棒）。
//   棒号**低可信**（错标漏标常见），不能作为主判据。
// 时间锚点（Q11）：源帖声明的时间优先 → pub_ts + 槽位估算兜底。
// ===========================================================================

/// <summary>一次验收的结论。</summary>
public sealed class MatchOutcome
{
    public bool Accepted { get; set; }
    public double Confidence { get; set; }
    /// <summary>判据描述，进 audit 与 report（如 "反向@uid 命中" / "时间差 12 分钟"）。</summary>
    public string Reason { get; set; } = "";
    public string MatchedBy { get; set; } = "";
}

/// <summary>候选验收。</summary>
public sealed class MatchVerifier
{
    private readonly ChainHttp _http;

    public MatchVerifier(ChainHttp http) => _http = http;

    /// <summary>
    /// 用源帖子（<paramref name="from"/>）里那条邻居指认（<paramref name="link"/>）去验收候选帖子。
    /// </summary>
    public MatchOutcome Verify(PostFacts from, NeighborLink link, PostFacts candidate, RelayQuery query, WalkDirection dir)
        => throw new ChainFetchException("MatchVerifier.Verify 尚未实现（Agent「chain」）。");
}
