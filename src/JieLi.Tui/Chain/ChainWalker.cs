#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」
// 只允许改本文件与 Chain/MatchVerifier.cs / Chain/ChainWriter.cs。
//
// 职责：BFS 双向遍历整条接力链。**下文签名已冻结，Program.cs 直接调用，勿改。**
//
// 遍历规则（Q3 / Q5，已定，勿改语义）：
//   * 从入口帖出发，同时向「下一棒(Forward)」和「上一棒(Backward)」走；
//   * visited 以 **postId** 为键（一人可多棒，绝不能用 uid 去重）；
//   * 相遇即停；某方向连续 2 次失败即停该方向；末棒显式标记立即停该方向；
//   * 节点带方向；交叉冲突「先到者胜 + 审计告警」，并在 Note 里留下痕迹供 review 视图显示；
//   * 补充链接（用户用 --add-link 给的）优先于自动查找：先用它当节点，再从它继续双向走（不重走已知节点）；
//   * 单棒内部允许字段级合并（正文/评论/简介，每字段记来源），跨棒绝不合并。
// ===========================================================================

/// <summary>接力链遍历器。</summary>
public sealed class ChainWalker
{
    private readonly ChainHttp _http;
    private readonly IPostReader _reader;
    private readonly IUserLocator _locator;
    private readonly IAiAssist? _ai;

    public ChainWalker(ChainHttp http, IPostReader reader, IUserLocator locator, IAiAssist? ai = null)
    {
        _http = http;
        _reader = reader;
        _locator = locator;
        _ai = ai;
    }

    /// <summary>最多遍历多少个节点（防跑飞）。</summary>
    public int MaxNodes { get; init; } = 80;

    /// <summary>某方向连续失败多少次后停止该方向（Q3：2）。</summary>
    public int MaxConsecutiveFailures { get; init; } = 2;

    /// <summary>
    /// 从 <paramref name="start"/> 出发走完整条链。
    /// </summary>
    /// <param name="supplements">人工补录的链接（可为空）。ForcedBar 非空表示人工指定棒号。</param>
    public ChainResult Walk(
        PostRef start,
        RelayQuery query,
        IReadOnlyList<(PostRef Ref, int? ForcedBar)>? supplements,
        Action<string>? log,
        CancellationToken ct = default)
        => throw new ChainFetchException("ChainWalker.Walk 尚未实现（Agent「chain」）。");
}
