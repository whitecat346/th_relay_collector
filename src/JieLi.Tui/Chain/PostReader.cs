#nullable enable
using System.Text.RegularExpressions;
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「reader」
// 目标：把一条帖子（opus 动态/专栏、视频）读成 PostFacts（含上下棒 @、棒号、时段、末棒标记）。
// 只允许改本文件与 Chain/NeighborExtractor.cs。签名已冻结，勿改。
//
// 可用原料（既有代码，直接复用，不要复制粘贴实现）：
//   Data/OpusContent.cs      → OpusContentExtractor.FromHtml（括号配平提取 __INITIAL_STATE__）、ParseComments
//   Data/SingleLinkCollector.cs → 现成的 opus 读取流程（HTML + 评论 + spans）
//   Data/BarParser.cs        → BarParser.Parse(TopicCard, topicTag)、ComputeDeviation
//   Data/SummaryExtractor.cs → InferTopicTag / IsSummaryCard
//   Data/TextNormalizer.cs   → 中文数字、时段归一、昵称比对
//   Chain/ChainHttp.cs       → GetText / GetApiJson（含限速与 412/-352 退避）
// ===========================================================================

/// <summary>读取一条帖子并产出接力学事实。</summary>
public sealed class ChainPostReader : IPostReader
{
    private readonly ChainHttp _http;

    public ChainPostReader(ChainHttp http) => _http = http;

    /// <inheritdoc />
    public PostFacts Read(PostRef reference, Action<string>? log, CancellationToken ct)
    {
        throw new ChainFetchException("ChainPostReader 尚未实现（由 Agent「reader」填充）。");
    }
}
