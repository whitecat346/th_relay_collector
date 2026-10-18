#nullable enable
using JieLi.Tui.Data;

namespace JieLi.Tui.Chain;

// ---------------------------------------------------------------------------
// 接力「@ 链」遍历的共享契约。
//
// 为什么需要它：实测接力的相邻棒次**不给出下一棒的链接**（定时发布，链接在发布时
// 还不存在），帖子只 @ 出下一棒是谁。所以要靠「@ 昵称 → uid → 该用户空间里那一时刻
// 的动态」这条链去找，而不是顺着链接爬。
//
// 本文件只放 DTO 与接口，不放实现 —— 便于多路并行开发时接口先冻结。
// ---------------------------------------------------------------------------

/// <summary>帖子的形态。opus 对动态与文章都成立，是统一读取入口。</summary>
public enum PostKind
{
    /// <summary>动态 / 专栏（统一走 https://www.bilibili.com/opus/&lt;id&gt;）。</summary>
    Opus,
    /// <summary>投稿视频（BV 号）。</summary>
    Video,
    /// <summary>话题（只作为入口，不是一根棒）。</summary>
    Topic,
}

/// <summary>遍历方向。BFS 双向推进时每个节点都必须带方向，避免两路查重。</summary>
public enum WalkDirection
{
    None = 0,
    Forward = 1,
    Backward = 2,
    Both = 3,
}

/// <summary>
/// 取得某条信息的**方法名**（阶梯记账用）。
/// 每一级都要记进审计，出问题才能复盘"是哪一级失手"。
/// </summary>
public enum FetchMethod
{
    Unknown = 0,
    /// <summary>用户直接给的入口链接（--link）。</summary>
    StartLink,
    /// <summary>运行中人工补录的链接（--add-link / TUI 的 A 键）。</summary>
    Supplement,
    /// <summary>话题卡片列表（最稳，但有话题才会存在）。</summary>
    TopicCards,
    /// <summary>uid → 空间动态列表（x/polymer/web-dynamic/v1/opus/feed/space）。</summary>
    SpaceOpusFeed,
    /// <summary>文章搜索（x/web-interface/search/type?search_type=article&keyword=）。</summary>
    ArticleSearch,
    /// <summary>视频搜索 / 相关推荐（archive/related）。</summary>
    VideoSearch,
    /// <summary>评论 content.members[] 里的 mid（真正 @ 组件才给）。</summary>
    MembersMid,
    /// <summary>用户搜索（search_type=bili_user）。</summary>
    UserSearch,
    /// <summary>话题作者表。</summary>
    TopicAuthorTable,
    /// <summary>AI 辅助判定（最后手段，需 --ai）。</summary>
    AiAssist,
    /// <summary>人工直接指定（--at-bar / TUI 编辑）。</summary>
    Manual,
}

/// <summary>一条帖子的最小定位信息。</summary>
public sealed class PostRef
{
    public PostKind Kind { get; set; } = PostKind.Opus;
    /// <summary>opus id / BV 号 / topic_id（字符串形式）。</summary>
    public string Id { get; set; } = string.Empty;

    public string Url => Kind switch
    {
        PostKind.Video => $"https://www.bilibili.com/video/{Id}/",
        PostKind.Topic => $"https://www.bilibili.com/v/topic/detail?topic_id={Id}",
        _ => $"https://www.bilibili.com/opus/{Id}",
    };

    public override string ToString() => $"{Kind}:{Id}";

    public static PostRef Opus(string id) => new() { Kind = PostKind.Opus, Id = id };
    public static PostRef Video(string id) => new() { Kind = PostKind.Video, Id = id };
    public static PostRef Topic(string id) => new() { Kind = PostKind.Topic, Id = id };
}

/// <summary>一条 @ 提及（正文或评论里）。</summary>
public sealed class AtMention
{
    public string Name { get; set; } = string.Empty;
    /// <summary>UID；手打 @ 且不是真 @ 组件时为 null（实测这类情况很常见）。</summary>
    public string? Uid { get; set; }
    /// <summary>在本帖正文中的出现序号，用于「上一棒 / 下一棒」的就近关联。</summary>
    public int Ordinal { get; set; }
    /// <summary>该 @ 所在的那一行原文（AI 片段与审计都要用）。</summary>
    public string Context { get; set; } = string.Empty;
}

/// <summary>帖子声明的相邻棒次（上一棒 / 下一棒）。</summary>
public sealed class NeighborLink
{
    public bool IsPrev { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Uid { get; set; }
    /// <summary>原始那句话（审计/报告要原样展示，便于人工核对）。</summary>
    public string RawText { get; set; } = string.Empty;
    /// <summary>该邻居那一棒声明的棒号原文（如 "17"、"EX"）。</summary>
    public string? BarRaw { get; set; }
    /// <summary>该邻居那一棒声明的时段（如 "20:30"）。</summary>
    public string? DeclaredTime { get; set; }
    public int Ordinal { get; set; }
    /// <summary>原文里这一棒是否被标注为「代发」。</summary>
    public bool IsProxy { get; set; }
    public string? ProxyName { get; set; }

    public override string ToString() => $"{(IsPrev ? "上一棒" : "下一棒")}:{Name}({Uid ?? "no-uid"}) {DeclaredTime ?? ""}";
}

/// <summary>一条帖子解析出的接力学事实（单棒内的字段级合并结果）。</summary>
public sealed class PostFacts
{
    public string PostId { get; set; } = string.Empty;
    public PostKind Kind { get; set; } = PostKind.Opus;
    public string Url { get; set; } = string.Empty;

    public long AuthorUid { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    /// <summary>发布时刻（unix 秒）。</summary>
    public long PubTs { get; set; }

    /// <summary>活动名（话题名优先，其次标题推断）。</summary>
    public string? RelayName { get; set; }
    public long? TopicId { get; set; }
    public string? Title { get; set; }

    /// <summary>本条声明的棒号原文（"06"/"第六"/"EX"…）；解析不到为 null。</summary>
    public string? BarRaw { get; set; }
    public int? BarNumber { get; set; }
    public bool IsEx { get; set; }
    /// <summary>本条声明的棒位时段（"20:30"）。</summary>
    public string? DeclaredTime { get; set; }

    public List<NeighborLink> Prevs { get; set; } = new();
    public List<NeighborLink> Nexts { get; set; } = new();
    public List<AtMention> Mentions { get; set; } = new();

    /// <summary>显式末棒标记（最后一棒/完结/收尾/收官/终棒 且指自己）。优先级最高。</summary>
    public bool LastMark { get; set; }
    public string? LastMarkRaw { get; set; }
    /// <summary>显式首棒标记（第一棒/开局/首棒）。</summary>
    public bool FirstMark { get; set; }

    public string BodyText { get; set; } = string.Empty;
    public string CommentsText { get; set; } = string.Empty;
    public string MergedText { get; set; } = string.Empty;
    public int CommentCount { get; set; }

    /// <summary>作品形态标签（插画/视频/文章…），进 CSV 的「类型」列。</summary>
    public string? WorkKindLabel { get; set; }

    /// <summary>字段 → 来源（正文/评论/简介）。单棒内允许字段级合并，但必须记来源。</summary>
    public Dictionary<string, string> FieldSources { get; set; } = new();

    public List<string> ReaderWarnings { get; set; } = new();

    public DateTime PubLocal => PubTs > 0
        ? DateTimeOffset.FromUnixTimeSeconds(PubTs).ToLocalTime().DateTime
        : DateTime.MinValue;

    /// <summary>是否带任何接力学特征（棒号/时段/上下棒/活动名）。匹配时用来防误收。</summary>
    public bool HasRelayFeature =>
        BarNumber is not null || IsEx || LastMark || FirstMark
        || Prevs.Count > 0 || Nexts.Count > 0 || DeclaredTime is not null;
}

/// <summary>空间/搜索里翻到的一条候选帖子（尚未确认是不是那一棒）。</summary>
public sealed class PostCandidate
{
    public PostRef Ref { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public long PubTs { get; set; }
    public FetchMethod Method { get; set; } = FetchMethod.Unknown;
    /// <summary>为什么认为它是候选（审计与报告用）。</summary>
    public string Why { get; set; } = string.Empty;
}

/// <summary>定位到的用户。</summary>
public sealed class UserRef
{
    public long Uid { get; set; }
    public string Name { get; set; } = string.Empty;
    public FetchMethod Method { get; set; } = FetchMethod.Unknown;
    /// <summary>名字是否精确命中（false 表示只拿到近似候选，需要人工当心）。</summary>
    public bool Exact { get; set; } = true;
}

/// <summary>本次要归档的活动查询条件。</summary>
public sealed class RelayQuery
{
    /// <summary>活动名（话题名优先）。用于标题过滤与匹配。</summary>
    public string? RelayName { get; set; }
    public long? TopicId { get; set; }
    /// <summary>活动当天 0:00（本地时间）。候选人只在 [DayStart - 2h, DayEnd + 6h] 里找。</summary>
    public DateTime DayStart { get; set; }
    public DateTime DayEnd { get; set; }
    /// <summary>时间窗（分钟）：接受条件之一。默认 90 = 1h30min（用户定的阈值）。</summary>
    public int WindowMinutes { get; set; } = 90;
    /// <summary>活动是 24h 还是 32h 等，仅用于放宽搜索区间与报告。</summary>
    public int DeclaredHours { get; set; }

    public DateTime SearchStart => DayStart.AddHours(-2);
    public DateTime SearchEnd => DayEnd.AddHours(6);
}

/// <summary>链上的一个节点。</summary>
public sealed class ChainNode
{
    public PostRef Ref { get; set; } = new();
    public PostFacts? Facts { get; set; }
    public WalkDirection Direction { get; set; } = WalkDirection.None;
    public FetchMethod Source { get; set; } = FetchMethod.Unknown;
    /// <summary>相对入口的跳数（入口为 0）。</summary>
    public int Depth { get; set; }
    /// <summary>0..1 置信度：结构证据 0.95 / 时间窗 0.6 / 两者兼有 0.99。</summary>
    public double Confidence { get; set; }
    public List<string> Notes { get; set; } = new();
    public string? MatchedBy { get; set; }
    public bool IsStart { get; set; }
    public bool IsSupplement { get; set; }
}

/// <summary>链路缺口（报告的主体：告诉人"哪里断了、怎么补"）。</summary>
public sealed class GapItem
{
    /// <summary>neighbor / candidate / uid / reader / bar / time。</summary>
    public string Kind { get; set; } = "neighbor";
    public string Reason { get; set; } = string.Empty;
    /// <summary>从哪条帖子断掉的。</summary>
    public string? FromPostId { get; set; }
    public string? FromPostUrl { get; set; }
    public WalkDirection Direction { get; set; } = WalkDirection.None;
    public string? ExpectedName { get; set; }
    public string? ExpectedUid { get; set; }
    public string? ExpectedBar { get; set; }
    public string? ExpectedTime { get; set; }
    /// <summary>已经试过的方法阶梯（按顺序）。</summary>
    public List<string> TriedMethods { get; set; } = new();
    /// <summary>可直接复制执行的补充命令。</summary>
    public string? SuggestedCommand { get; set; }
}

/// <summary>审计行：每一步做了什么、用的哪种方法、成没成。</summary>
public sealed class ChainAuditEntry
{
    public string Step { get; set; } = string.Empty;
    public WalkDirection Direction { get; set; } = WalkDirection.None;
    public FetchMethod Method { get; set; } = FetchMethod.Unknown;
    public string Detail { get; set; } = string.Empty;
    public bool Success { get; set; }
    public long ElapsedMs { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>链路遍历的最终结果。</summary>
public sealed class ChainResult
{
    public List<ChainNode> Nodes { get; set; } = new();
    public List<GapItem> Gaps { get; set; } = new();
    public List<ChainAuditEntry> Audit { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? RelayName { get; set; }
    /// <summary>「work/&lt;slug&gt;」用的目录名（chain_&lt;起始id&gt;）。</summary>
    public string Slug { get; set; } = string.Empty;
    public string StartPostId { get; set; } = string.Empty;

    public IEnumerable<ChainNode> WithFacts => Nodes.Where(n => n.Facts is not null);

    /// <summary>入口链上是否两向都到了头（有末棒标记 / 与首棒相遇）。</summary>
    public bool ForwardComplete => Nodes.Any(n => n.Facts?.LastMark == true);
}

// ---------------------------------------------------------------------------
// 接口（实现分派给不同文件，见各文件头注释）
// ---------------------------------------------------------------------------

/// <summary>读取一条帖子的接力学事实。实现见 <c>Chain/PostReader.cs</c>。</summary>
public interface IPostReader
{
    /// <summary>读一条帖子。读不到应抛 <see cref="ChainFetchException"/>（不要返回半成品）。</summary>
    PostFacts Read(PostRef reference, Action<string>? log, CancellationToken ct);
}

/// <summary>昵称 → uid、uid → 空间里的候选帖子。实现见 <c>Chain/UserResolver.cs</c> 与 <c>Chain/SpaceFinder.cs</c>。</summary>
public interface IUserLocator
{
    /// <summary>
    /// 昵称 → uid 的阶梯定位：
    /// ① 调用方已给的 hint（评论 members.mid / @ 组件 rid）
    /// ② 用户搜索 bili_user 精确命中
    /// ③ 话题作者表
    /// ④ null（失败，由调用方记账为缺口）
    /// </summary>
    UserRef? Resolve(string name, string? hintUid, Action<string>? log, CancellationToken ct);

    /// <summary>在用户空间里找窗口内的候选帖子（不确认是不是那一棒，确认交给 MatchVerifier）。</summary>
    IReadOnlyList<PostCandidate> FindPosts(UserRef user, RelayQuery query, Action<string>? log, CancellationToken ct);
}

/// <summary>AI 辅助判定的请求（只发片段，绝不发整页 HTML）。</summary>
public sealed class AiRequest
{
    /// <summary>用途：uid / neighbor / candidate / bar。</summary>
    public string Purpose { get; set; } = "candidate";
    /// <summary>正文片段（截断到 1500 字以内）。</summary>
    public string Snippet { get; set; } = string.Empty;
    /// <summary>候选 uid/昵称 列表。</summary>
    public List<AtMention> Candidates { get; set; } = new();
    public int? ExpectedBar { get; set; }
    public string? ExpectedTime { get; set; }
    public string? RelayName { get; set; }
    /// <summary>本次要回答的问题（自然语言，写死成模板，避免发散）。</summary>
    public string Question { get; set; } = string.Empty;
}

/// <summary>AI 的判定结果。</summary>
public sealed class AiVerdict
{
    public bool Confident { get; set; }
    public string? PickUid { get; set; }
    public string? PickName { get; set; }
    public int? Bar { get; set; }
    public string? Reason { get; set; }
    /// <summary>原始回复（落 ai-log.jsonl 用）。</summary>
    public string RawReply { get; set; } = string.Empty;
}

/// <summary>AI 辅助。实现见 <c>Chain/AiAssist.cs</c>。默认关闭，只有 --ai 才启用。</summary>
public interface IAiAssist
{
    bool Enabled { get; }
    /// <summary>问一次。调用方负责预算与逐次确认；返回 null 表示没答/答不可用。</summary>
    AiVerdict? Ask(AiRequest request, Action<string>? log, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// 异常
// ---------------------------------------------------------------------------

/// <summary>读取/定位帖子的可恢复失败（进缺口清单，不中止整轮）。</summary>
public sealed class ChainFetchException : Exception
{
    public ChainFetchException(string message) : base(message) { }
    public ChainFetchException(string message, Exception inner) : base(message, inner) { }
}
