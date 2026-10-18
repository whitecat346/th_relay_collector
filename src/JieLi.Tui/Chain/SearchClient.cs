#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/UserResolver.cs / Chain/SpaceFinder.cs / Chain/UserCache.cs。
//
// 职责：搜索类端点（匿名可用，已实测）：
//   x/web-interface/search/type?search_type=bili_user&keyword=  → uname/mid/fans
//   x/web-interface/search/type?search_type=article&keyword=   → id/author/mid/title/pub_time
//   x/web-interface/search/type?search_type=video&keyword=     → bvid/author/mid/title/pubdate
//   x/polymer/web-dynamic/v1/opus/feed/space?host_mid=&page=&offset=&type=all → 空间帖子列表
//   x/web-interface/archive/related?bvid=                      → 同 UP 其他视频（空间视频列表被封时的替代）
// 搜索结果是 HTML 高亮片段（含 <em> 标签），取文本前要清理。
// ===========================================================================

/// <summary>搜索端点封装。</summary>
public sealed class ChainSearchClient
{
    private readonly ChainHttp _http;

    public ChainSearchClient(ChainHttp http) => _http = http;

    /// <summary>用户搜索（bili_user）。返回可能为空的候选列表。</summary>
    public IReadOnlyList<UserRef> SearchUsers(string keyword, Action<string>? log, CancellationToken ct)
        => throw new ChainFetchException("ChainSearchClient.SearchUsers 尚未实现（Agent「locator」）。");

    /// <summary>专栏搜索（article）。</summary>
    public IReadOnlyList<PostCandidate> SearchArticles(string keyword, long? onlyMid, Action<string>? log, CancellationToken ct)
        => throw new ChainFetchException("ChainSearchClient.SearchArticles 尚未实现（Agent「locator」）。");

    /// <summary>视频搜索（video）。</summary>
    public IReadOnlyList<PostCandidate> SearchVideos(string keyword, long? onlyMid, Action<string>? log, CancellationToken ct)
        => throw new ChainFetchException("ChainSearchClient.SearchVideos 尚未实现（Agent「locator」）。");
}
