#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/SearchClient.cs / Chain/UserResolver.cs / Chain/UserCache.cs。
//
// 职责：给定 uid + 活动 + 期望槽位，找出该用户的候选帖子（不含最终判定，判定归 Agent「chain」）。
// 主端点（已实测匿名可用，20 条/页，has_more + offset 游标）：
//   x/polymer/web-dynamic/v1/opus/feed/space?host_mid=&page=&offset=&type=all
//   item 里 pub_time 为空、无正文、content 只有标题首行 → 候选排序要靠「标题过滤 + 点开确认」，
//   所以这里只负责**产出候选**，不要在这里做棒号判定。
// 兜底：x/web-interface/archive/related?bvid=（同 UP 主其他视频），用于视频帖。
// ===========================================================================

/// <summary>空间帖子候选搜索。</summary>
public sealed class SpaceFinder
{
    private readonly ChainHttp _http;

    public SpaceFinder(ChainHttp http) => _http = http;

    /// <summary>取该用户在某活动时间窗附近的候选帖子（未验证）。</summary>
    public IReadOnlyList<PostCandidate> FindCandidates(long uid, string? relayName, RelayQuery query, Action<string>? log, CancellationToken ct)
        => throw new ChainFetchException("SpaceFinder.FindCandidates 尚未实现（Agent「locator」）。");
}
