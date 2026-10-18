#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/SearchClient.cs / Chain/SpaceFinder.cs / Chain/UserCache.cs。
//
// 职责：实现 IUserLocator —— 昵称/uid → 用户，用户 + 活动 → 候选帖子。
// uid 阶梯（Q1，先强后弱，弱手段只在前一手段**明确返回空**时才降级，每级写审计）：
//   1. 帖子自身 @ 组件里的 members.mid（由 reader 提供，不在本文件）
//   2. bili_user 搜索精确命中昵称
//   3. 话题作者表（话题卡片里出现过的该作者的 mid）
//   4. N/A（返回 null，不要猜）
// 帖子阶梯（Q1）：
//   1. 空间 opus/feed/space 列表 + 标题/时间过滤（**主用**，匿名可用）
//   2. article 搜索（按 mid 过滤）
//   3. video 搜索 / archive related（按 mid 过滤）
//   4. N/A
// 注意：x/space/wbi/arc/search、x/space/acc/info 匿名会 -352，x/polymer/web-dynamic/v1/feed/space
// 匿名返回 code=0 但 total=0（静默空），**不要用它们**。
// ===========================================================================

/// <summary>昵称/uid → 用户，用户 + 活动 → 候选帖子。</summary>
public sealed class ChainUserLocator : IUserLocator
{
    public ChainUserLocator(ChainHttp http, ChainSearchClient search, UserCache cache)
    {
        Http = http;
        Search = search;
        Cache = cache;
    }

    public ChainHttp Http { get; }
    public ChainSearchClient Search { get; }
    public UserCache Cache { get; }

    /// <inheritdoc />
    public UserRef? Resolve(string name, string? hintUid, Action<string>? log, CancellationToken ct)
        => throw new ChainFetchException("ChainUserLocator.Resolve 尚未实现（Agent「locator」）。");

    /// <inheritdoc />
    public IReadOnlyList<PostCandidate> FindPosts(UserRef user, RelayQuery query, Action<string>? log, CancellationToken ct)
        => throw new ChainFetchException("ChainUserLocator.FindPosts 尚未实现（Agent「locator」）。");
}
