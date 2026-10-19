#nullable enable
using System.Collections.Generic;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/SearchClient.cs / Chain/SpaceFinder.cs / Chain/UserCache.cs。
//
// 职责：实现 IUserLocator —— 昵称/uid → 用户，用户 + 活动 → 候选帖子。
// uid 阶梯（先强后弱，弱手段只在前一手段**明确返回空**时才降级，每级写 log）：
//   ① 调用方给的 hintUid（评论 members.mid / @ 组件 rid）→ 直接用，Exact=true
//   ② bili_user 搜索，昵称精确命中 → Exact=true
//   ③ 昵称高度相似（去空格/全半角/常见错字 学园↔学院）→ 返回但 Exact=false
//   ④ 都没有 → null（不猜，交给调用方记缺口；人工 --at-bar 可补）
// 帖子阶梯（Q1）：
//   ① 空间 opus/feed/space 列表 + 标题/时间过滤（**主用**，匿名可用）
//   ② article 搜索（按 mid 过滤）
//   ③ video 搜索（按 mid 过滤；先试「活动名+接力」，再活动名，最后昵称兜底）
//   ④ 都没有 → 空列表（缺口由调用方记账）
// 注意：x/space/wbi/arc/search、x/space/acc/info 匿名会 -352，x/polymer/web-dynamic/v1/feed/space
// 匿名返回 code=0 但 total=0（静默空），**不要用它们**。
// 缓存：ChainWalker 的构造里没有 UserCache，缓存由本类负责查/写（否则探针路径永远不落盘）。
// ===========================================================================

/// <summary>昵称/uid → 用户，用户 + 活动 → 候选帖子。</summary>
public sealed class ChainUserLocator : IUserLocator
{
    /// <summary>Levenshtein 相似度阈值：低于它就不再往下猜（宁可交缺口给人工）。</summary>
    private const double SimilarThreshold = 0.75;

    /// <summary>变体搜索上限：最多再打 4 个关键词（每个搜索都是一次网络请求）。</summary>
    private const int MaxVariantSearches = 4;

    /// <summary>
    /// 常见错字/异体/简繁写法分组。组内任意两种写法在比对时视为同一个名字，
    /// 组内第 0 项是归一化后的规范写法。
    /// 为什么需要：实测「方块学院回到家什」搜不到，改成「方块学园回到家什」立刻命中
    /// （B 站昵称里学园/学院混写非常常见）。
    /// </summary>
    private static readonly string[][] VariantGroups =
    {
        new[] { "学园", "学院" },
        new[] { "灵", "霊" },
        new[] { "梦", "夢" },
        new[] { "龙", "龍" },
        new[] { "樱", "櫻" },
        new[] { "绘", "繪" },
        new[] { "辉", "輝" },
        new[] { "馆", "館" },
        new[] { "华", "華" },
        new[] { "姬", "姫" },
    };

    private readonly SpaceFinder _space;

    /// <summary>全活动视频索引（一次搜索建立，之后本地按 mid / 标题过滤，绕开搜索接口的静默限流）。</summary>
    private List<PostCandidate>? _videoIndex;

    /// <summary>索引对应的核心活动名；换了活动就重建。</summary>
    private string? _videoIndexKey;

    /// <summary>已经作为兜底候选交出去过的帖子 id，避免同一条被反复塞进不同邻居的候选池。</summary>
    private readonly HashSet<string> _offeredRefs = new(StringComparer.Ordinal);

    /// <summary>已建立的话题索引（null 时还没建；空索引表示建过但失败/为空）。</summary>
    private TopicIndex? _topicIndex;

    /// <summary>话题索引对应的 topic_id，用于判断是否需要重建。</summary>
    private long? _topicIndexId;

    public ChainUserLocator(ChainHttp http, ChainSearchClient search, UserCache cache)
    {
        Http = http;
        Search = search;
        Cache = cache;
        _space = new SpaceFinder(http);
    }

    public ChainHttp Http { get; }
    public ChainSearchClient Search { get; }
    public UserCache Cache { get; }

    /// <inheritdoc />
    public void PrimeTopic(long? topicId)
    {
        if (topicId is null or <= 0) return;
        if (_topicIndexId == topicId) return;
        // 换话题就作废旧索引，下次用到时重建（懒加载，避免入口没话题时白拉一次）
        if (_topicIndexId is not null) _topicIndex = null;
        _topicIndexId = topicId;
    }

    /// <summary>
    /// 确保话题索引就绪（懒加载）。索引建立失败只记日志，调用方自然退回搜索路径。
    /// 为什么把它放在阶梯最前面：搜索接口会被风控（v_voucher），而话题卡片接口不会；
    /// 且话题卡片是「这场接力自己的成员表」，比昵称搜索更权威（不会命中同名路人）。
    /// </summary>
    private TopicIndex? EnsureTopicIndex(Action<string>? log, CancellationToken ct)
    {
        if (_topicIndexId is null or <= 0) return null;
        if (_topicIndex is not null) return _topicIndex;
        log?.Invoke($"[话题索引] 建立 topic_id={_topicIndexId} 的名字→uid→帖子索引（不用搜索接口）");
        _topicIndex = TopicIndex.Load(Http, _topicIndexId.Value, log, ct);
        return _topicIndex;
    }

    /// <inheritdoc />
    public UserRef? Resolve(string name, string? hintUid, Action<string>? log, CancellationToken ct)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0 && string.IsNullOrWhiteSpace(hintUid))
        {
            log?.Invoke("[用户] 昵称为空且没有 hintUid，无法定位");
            return null;
        }

        // ---------------- ① hintUid：最强证据，直接用 ----------------
        if (long.TryParse((hintUid ?? "").Trim(), out var hint) && hint > 0)
        {
            log?.Invoke($"[①hint] FetchMethod.Manual 直接用调用方给的 uid={hint}（昵称「{wanted}」），不再搜索");
            RememberResolved(hint, wanted, FetchMethod.Manual, exact: true, log);
            return new UserRef { Uid = hint, Name = wanted, Method = FetchMethod.Manual, Exact = true };
        }
        if (!string.IsNullOrWhiteSpace(hintUid))
            log?.Invoke($"[①hint] hintUid「{hintUid}」不是有效数字，降级到昵称搜索");
        if (wanted.Length == 0)
        {
            log?.Invoke("[用户] 没有可用 hintUid 且昵称为空，无法定位");
            return null;
        }

        // ---------------- ① 话题作者表：优先用「这场接力自己的成员表」 ----------------
        // 顺序刻意放在搜索前面：① 话题卡片接口不受搜索风控影响（v_voucher）；
        // ② 它只包含本次活动成员，不会像 bili_user 搜索那样命中同名路人。
        try
        {
            var topic = EnsureTopicIndex(log, ct);
            var hit = topic?.ResolveByName(wanted, log);
            if (hit is not null)
            {
                log?.Invoke($"[①话题作者表] FetchMethod.TopicAuthorTable 命中 uid={hit.Uid}「{hit.Name}」（Exact={hit.Exact}），不发搜索请求");
                RememberResolved(hit.Uid, hit.Name, FetchMethod.TopicAuthorTable, hit.Exact, log);
                return hit;
            }
            if (topic is { IsBuilt: true })
                log?.Invoke($"[①话题作者表] 话题 {topic.Cards.Count} 张卡片里没有「{wanted}」，降级到用户搜索");

            // 代发棒：作者是代发账号，正文里写着「@这个名字」（实测 17:00 那棒就是
            // 龙胆紫苑 代发 @茶猫祈祷中，按作者名永远找不到）。
            var mentioned = topic?.FindByMention(wanted) ?? Array.Empty<TopicCardEntry>();
            var uniqueAuthors = mentioned.Select(m => m.AuthorUid).Distinct().ToList();
            if (uniqueAuthors.Count == 1)
            {
                var proxy = mentioned[0];
                log?.Invoke($"[①话题作者表]「{wanted}」不是话题作者，但被 {proxy.AuthorName}(uid={proxy.AuthorUid}) 的卡片正文点名"
                    + $"（代发/代理），按代发账号继续找");
                RememberResolved(proxy.AuthorUid, wanted, FetchMethod.TopicAuthorTable, exact: false, log);
                return new UserRef
                {
                    Uid = proxy.AuthorUid,
                    Name = wanted,
                    Method = FetchMethod.TopicAuthorTable,
                    Exact = false,
                };
            }
            if (uniqueAuthors.Count > 1)
                log?.Invoke($"[①话题作者表]「{wanted}」被 {uniqueAuthors.Count} 个不同账号的卡片点名，无法判断谁是代发者，降级到用户搜索");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"[①话题作者表] 失败：{ex.Message}（降级到用户搜索）");
        }

        // ---------------- ② bili_user 搜索，昵称精确命中 ----------------
        log?.Invoke($"[②用户搜索] FetchMethod.UserSearch 搜索昵称「{wanted}」");
        var hits = Search.SearchUsers(wanted, log, ct);
        var exact = hits.FirstOrDefault(u => PlainCanon(u.Name) == PlainCanon(wanted));
        if (exact is not null)
        {
            log?.Invoke($"[②用户搜索] 精确命中 uid={exact.Uid}「{exact.Name}」（FetchMethod.UserSearch，Exact=true）");
            RememberResolved(exact.Uid, exact.Name, FetchMethod.UserSearch, exact: true, log);
            return new UserRef { Uid = exact.Uid, Name = exact.Name, Method = FetchMethod.UserSearch, Exact = true };
        }

        // ② 返回了候选但没有精确命中：在候选里找高度相似（同一手段内评估，不算降级）
        if (hits.Count > 0)
        {
            var best = hits.OrderByDescending(u => Similarity(u.Name, wanted)).First();
            var score = Similarity(best.Name, wanted);
            if (score >= SimilarThreshold)
            {
                log?.Invoke($"[②用户搜索] 未精确命中，取最相似候选 uid={best.Uid}「{best.Name}」（相似度 {score:P0}，FetchMethod.UserSearch，Exact=false）");
                RememberResolved(best.Uid, best.Name, FetchMethod.UserSearch, exact: false, log);
                return new UserRef { Uid = best.Uid, Name = best.Name, Method = FetchMethod.UserSearch, Exact = false };
            }
            log?.Invoke($"[②用户搜索] 未精确命中，候选里最相似的是 uid={best.Uid}「{best.Name}」（相似度 {score:P0} < {SimilarThreshold:P0}），放弃猜测");
            return null;
        }

        // ---------------- ③ 弱手段：错字/全半角/去空格变体再搜 ----------------
        log?.Invoke("[②用户搜索] 明确返回空，降级到昵称变体搜索");
        foreach (var variant in VariantKeywords(wanted).Take(MaxVariantSearches))
        {
            log?.Invoke($"[③变体搜索] FetchMethod.UserSearch 用变体「{variant}」重搜");
            var variantHits = Search.SearchUsers(variant, log, ct);

            // 变体命中要求：归一化后与原始昵称一致（错字/异体容忍），否则再看相似度。
            var normalizedHit = variantHits.FirstOrDefault(u => Canon(u.Name) == Canon(wanted));
            if (normalizedHit is not null)
            {
                log?.Invoke($"[③变体搜索] 变体「{variant}」命中 uid={normalizedHit.Uid}「{normalizedHit.Name}」（FetchMethod.UserSearch，Exact=false：原昵称未精确命中）");
                RememberResolved(normalizedHit.Uid, normalizedHit.Name, FetchMethod.UserSearch, exact: false, log);
                return new UserRef { Uid = normalizedHit.Uid, Name = normalizedHit.Name, Method = FetchMethod.UserSearch, Exact = false };
            }

            var best = variantHits.OrderByDescending(u => Similarity(u.Name, wanted)).FirstOrDefault();
            if (best is not null && Similarity(best.Name, wanted) >= SimilarThreshold)
            {
                log?.Invoke($"[③变体搜索] 变体「{variant}」最相似候选 uid={best.Uid}「{best.Name}」（相似度 {Similarity(best.Name, wanted):P0}，Exact=false）");
                RememberResolved(best.Uid, best.Name, FetchMethod.UserSearch, exact: false, log);
                return new UserRef { Uid = best.Uid, Name = best.Name, Method = FetchMethod.UserSearch, Exact = false };
            }
        }

        log?.Invoke($"[用户] 阶梯全部落空，没有定位到「{wanted}」（返回 null，不猜）");
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PostCandidate> FindPosts(UserRef user, RelayQuery query, Action<string>? log, CancellationToken ct)
    {
        var results = new List<PostCandidate>();
        if (user is null || user.Uid <= 0)
        {
            log?.Invoke("[帖子] uid 无效，返回空候选");
            return results;
        }

        var relay = string.IsNullOrWhiteSpace(query.RelayName) ? null : query.RelayName!.Trim();

        // ---------------- ⓪ 话题卡片：有索引就直接给候选 ----------------
        // 这是「搜索被风控」现实下的第一条腿：话题卡片里本来就带着每一棒的帖 id、
        // 作者 uid、发布时间，还有视频卡的 bvid（第 5 棒那种只在话题里出现的视频也能拿到）。
        try
        {
            var topic = EnsureTopicIndex(log, ct);
            if (topic is { IsBuilt: true })
            {
                var cards = topic.ByUid(user.Uid);
                if (cards.Count > 0)
                {
                    var candidates = new List<PostCandidate>();
                    foreach (var card in cards)
                    {
                        var candidate = new PostCandidate
                        {
                            Ref = card.Kind == PostKind.Video ? PostRef.Video(card.Id) : PostRef.Opus(card.Id),
                            Title = !string.IsNullOrWhiteSpace(card.Text) ? SpaceFinder.FirstLine(card.Text) : card.Title,
                            PubTs = card.PubTs,
                            Method = FetchMethod.TopicCards,
                            AuthorUid = card.AuthorUid,
                            Why = $"话题卡片（topic={topic.TopicId}，作者 {card.AuthorName}）",
                        };
                        candidates.Add(candidate);
                    }
                    var kept = candidates.Where(c => SpaceFinder.InWindow(c.PubTs, query)).ToList();
                    log?.Invoke($"[⓪话题卡片] uid={user.Uid}「{user.Name}」在话题里有 {cards.Count} 条，时间窗内 {kept.Count} 条");
                    if (kept.Count > 0)
                    {
                        foreach (var c in kept) _offeredRefs.Add(c.Ref.Id);
                        Cache.Remember(user.Uid, user.Name, relay, true, FetchMethod.TopicCards,
                            kept.Select(c => c.Ref.Id),
                            $"话题 {topic.TopicId} 卡片直接命中 {kept.Count} 条");
                        Cache.FlushIfDirty(log);
                        return kept;
                    }
                }
                else
                {
                    log?.Invoke($"[⓪话题卡片] uid={user.Uid}「{user.Name}」不在话题成员里，继续走空间/搜索");
                }

                // 代发/代理：作者名对不上、但某张卡片正文点名了这个人（实测 17:00 那棒
                // 「龙胆紫苑 代发 @茶猫祈祷中」，卡片正文只有 17:00 @茶猫祈祷中）。
                if (!string.IsNullOrWhiteSpace(user.Name) && user.Name.Trim() != "?")
                {
                    var mentioned = topic.FindByMention(user.Name);
                    if (mentioned.Count > 0)
                    {
                        var keptMentions = new List<PostCandidate>();
                        foreach (var card in mentioned)
                        {
                            keptMentions.Add(new PostCandidate
                            {
                                Ref = card.Kind == PostKind.Video ? PostRef.Video(card.Id) : PostRef.Opus(card.Id),
                                Title = !string.IsNullOrWhiteSpace(card.Text) ? SpaceFinder.FirstLine(card.Text) : card.Title,
                                PubTs = card.PubTs,
                                Method = FetchMethod.TopicCards,
                                AuthorUid = card.AuthorUid,
                                Why = $"话题卡片正文点名「{user.Name}」（发布者 {card.AuthorName}，可能是代发）",
                            });
                        }
                        var keptByWindow = keptMentions.Where(c => SpaceFinder.InWindow(c.PubTs, query)).ToList();
                        log?.Invoke($"[⓪话题卡片]「{user.Name}」被 {mentioned.Count} 张卡片正文点名，时间窗内 {keptByWindow.Count} 条");
                        if (keptByWindow.Count > 0)
                        {
                            foreach (var c in keptByWindow) _offeredRefs.Add(c.Ref.Id);
                            Cache.Remember(user.Uid, user.Name, relay, true, FetchMethod.TopicCards,
                                keptByWindow.Select(c => c.Ref.Id),
                                $"话题 {topic.TopicId} 正文点名命中 {keptByWindow.Count} 条（可能是代发）");
                            Cache.FlushIfDirty(log);
                            return keptByWindow;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"[⓪话题卡片] 失败：{ex.Message}（继续走空间/搜索）");
        }

        // ---------------- 缓存：同一 (uid, relay) 7 天内不重跑网络 ----------------
        var cached = Cache.Find(user.Uid, relay);
        if (cached is not null && !Cache.IsStale(cached))
        {
            if (cached.Hit && cached.Posts.Count > 0)
            {
                log?.Invoke($"[缓存] (uid={user.Uid}, relay={relay ?? "-"}) 7 天内有正缓存 {cached.Posts.Count} 条"
                    + $"（{cached.Method ?? "?"}，检查于 {cached.LastChecked:yyyy-MM-dd HH:mm}），直接复用，不再发请求");
                var method = ParseMethod(cached.Method);
                foreach (var id in cached.Posts)
                {
                    results.Add(new PostCandidate
                    {
                        Ref = id.StartsWith("BV", StringComparison.OrdinalIgnoreCase) ? PostRef.Video(id) : PostRef.Opus(id),
                        // 缓存只存 id，不存标题/发布时间；需要时由读取端按 id 重新读。
                        Title = "",
                        PubTs = 0,
                        Method = method,
                        Why = $"用户记忆命中（{cached.LastChecked:yyyy-MM-dd} 用 {cached.Method ?? "?"} 找到过）",
                    });
                }
                return results;
            }

            log?.Invoke($"[缓存] (uid={user.Uid}, relay={relay ?? "-"}) 7 天内已确认无帖子（负缓存，{cached.Method ?? "?"}，"
                + $"检查于 {cached.LastChecked:yyyy-MM-dd}），本轮跳过重查；超过 {UserCache.RecheckDays} 天才会自动重试");
            return results;
        }
        if (cached is not null)
            log?.Invoke($"[缓存] (uid={user.Uid}, relay={relay ?? "-"}) 记忆已过期（last_checked={cached.LastChecked:yyyy-MM-dd}，"
                + $"超过 {UserCache.RecheckDays} 天），重新查询");

        var triedMethods = new List<FetchMethod>();
        var strongest = FetchMethod.Unknown;

        // ---------------- ① 空间动态（主用，匿名可用，20 条/页，最多 5 页） ----------------
        log?.Invoke($"[①空间动态] FetchMethod.SpaceOpusFeed uid={user.Uid} 活动「{relay ?? "（未指定）"}」");
        triedMethods.Add(FetchMethod.SpaceOpusFeed);
        try
        {
            var space = _space.FindCandidates(user.Uid, relay, query, log, ct);
            results.AddRange(space);
            if (space.Count > 0) strongest = FetchMethod.SpaceOpusFeed;
            log?.Invoke($"[①空间动态] 候选 {space.Count} 条");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拿不到 ≠ 明确返回空；但在"拿不到"和"可能还有弱手段"之间，继续降级比直接放弃更有用。
            log?.Invoke($"[①空间动态] 失败：{ex.GetType().Name}: {ex.Message}（继续降级尝试弱手段）");
        }

        // ---------------- ② 文章搜索（只有 ① 明确返回空才降级） ----------------
        // 「宽松兜底」候选（空间动态首行没有活动名、按 id 邻近猜的）不算命中：
        // 实测用户把某一棒发成了视频，空间动态里根本没有，于是必须继续降级去搜文章/视频。
        var softOnly = results.Count > 0 && results.All(c => c.Why.Contains("兜底"));
        if (softOnly)
            log?.Invoke("[阶梯] ①只给出宽松兜底候选（未确认活动名），继续降级搜文章/视频");

        if ((results.Count == 0 || softOnly) && relay is not null)
        {
            log?.Invoke($"[②文章搜索] FetchMethod.ArticleSearch keyword=「{relay}」onlyMid={user.Uid}");
            triedMethods.Add(FetchMethod.ArticleSearch);
            try
            {
                var articles = Search.SearchArticles(relay, user.Uid, log, ct);
                var kept = articles.Where(c => SpaceFinder.InWindow(c.PubTs, query)).ToList();
                if (kept.Count != articles.Count)
                    log?.Invoke($"[②文章搜索] 时间窗过滤掉 {articles.Count - kept.Count} 条，保留 {kept.Count} 条");
                results.AddRange(kept);
                if (kept.Count > 0) strongest = FetchMethod.ArticleSearch;
                log?.Invoke($"[②文章搜索] 候选 {kept.Count} 条");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"[②文章搜索] 失败：{ex.GetType().Name}: {ex.Message}（继续降级）");
            }
        }
        else if (results.Count > 0)
        {
            log?.Invoke("[②文章搜索] 跳过：①空间动态已经给出候选（阶梯纪律：有结果不降级）");
        }

        // ---------------- ③ 视频搜索（最后手段） ----------------
        var hasStrict = results.Any(c => !c.Why.Contains("兜底"));
        if (!hasStrict && relay is not null)
        {
            // 实测：视频标题里活动名常带「接力」后缀（如「魅魔日接力」），
            // 但话题名往往还带日期前缀与版本次缀（「8月15日魅魔日接力1.5」），
            // 直接拿去搜视频 0 命中。所以先用「核心活动名」搜，再退到原名。
            var core = CoreActivityName(relay);
            _ = core;

            // 实测：搜索接口在同一进程里只肯痛快回第一次，之后连续调用会「code=0 但 result 为空」
            // （静默限流，退避 5/10 秒也压不过去；同一 URL 用 python 单独打立刻恢复）。
            // 所以这里改成**一次搜索建全活动视频索引、之后按 mid / 标题本地过滤**。
            var index = RelayVideoIndex(core, log, ct);
            if (index.Count > 0)
            {
                var mine = index.Where(c => c.AuthorUid == user.Uid).ToList();
                var kept = mine.Where(c => SpaceFinder.InWindow(c.PubTs, query)).ToList();
                log?.Invoke($"[③视频搜索] 全活动视频索引 {index.Count} 条 → 作者 mid={user.Uid} 命中 {mine.Count} 条，时间窗内 {kept.Count} 条");
                results.AddRange(kept);
                if (kept.Count > 0) strongest = FetchMethod.VideoSearch;
            }

            // 仍然空 → 用索引里「标题含活动名、还没被认领」的条目兜底：
            // 实测改名/换号很常见（帖子里 @ 的 uid=228026390「雾雨梦灵」，真正发视频的是
            // uid=1386969752「霧雨梦灵」），只按 mid 找会把真正那一棒直接漏掉。
            if (results.Count == 0 && !string.IsNullOrWhiteSpace(user.Name) && user.Name.Trim() != "?")
            {
                var relayCanon = SpaceFinder.Canon(relay);
                var kept = index
                    .Where(c => c.AuthorUid != user.Uid)
                    .Where(c => SpaceFinder.InWindow(c.PubTs, query))
                    .Where(c => SpaceFinder.TryMatch(c.Title, relayCanon, out _))
                    .Where(c => _offeredRefs.Add(c.Ref.Id))
                    .ToList();
                if (kept.Count > 0)
                {
                    log?.Invoke($"[④索引兜底]「{user.Name}」未按 mid 命中 → 索引里标题含活动名的未认领条目 {kept.Count} 条（可能改名/换号/代发，交由验收判定）");
                    triedMethods.Add(FetchMethod.VideoSearch);
                    foreach (var c in kept) c.Why += "；按视频索引兜底（可能改过名/换过号/代发，需人工确认）";
                    results.AddRange(kept);
                    if (kept.Count > 0) strongest = FetchMethod.VideoSearch;
                }
            }
        }
        else if (results.Count > 0)
        {
            log?.Invoke("[③视频搜索] 跳过：前面的手段已经给出候选（阶梯纪律：有结果不降级）");
        }

        // 同一帖子可能被多种手段返回，按 kind:id 去重（保留先出现的那条）。
        var deduped = new List<PostCandidate>();
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in results)
        {
            if (candidate?.Ref is null || string.IsNullOrWhiteSpace(candidate.Ref.Id)) continue;
            if (seenRefs.Add(candidate.Ref.ToString())) deduped.Add(candidate);
        }

        // ---------------- 落缓存（正负都记，method + last_checked 供 7 天判定） ----------------
        var hit = deduped.Count > 0;
        Cache.Remember(user.Uid, user.Name, relay, hit, strongest,
            deduped.Select(c => c.Ref.Id),
            hit ? $"定位到 {deduped.Count} 条候选（阶梯：{string.Join("→", triedMethods)}）" : "未找到候选（负缓存）");
        Cache.FlushIfDirty(log);

        log?.Invoke($"[结果] uid={user.Uid}「{relay ?? "-"}」→ {deduped.Count} 条候选（试过：{string.Join("→", triedMethods)}）");
        return deduped;
    }

    // ------------------------------------------------------------------
    // 昵称规范化 / 相似度 / 变体关键词
    // ------------------------------------------------------------------

    /// <summary>基础规范化：去空白与标点、全角转半角、ASCII 小写（不做错字归一）。</summary>
    private static string PlainCanon(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = raw;
            if (c == '\u3000' || char.IsWhiteSpace(c)) continue;
            if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0);
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>规范化并做错字/异体归一（学园→学院 等），用于宽容比对。</summary>
    private static string Canon(string? text)
    {
        var normalized = PlainCanon(text);
        foreach (var group in VariantGroups)
        {
            for (var i = 1; i < group.Length; i++)
                normalized = normalized.Replace(group[i], group[0], StringComparison.Ordinal);
        }
        return normalized;
    }

    /// <summary>两个昵称的相似度（0..1）：完全一致或包含关系给 1；否则用 Levenshtein 比值。</summary>
    private static double Similarity(string? a, string? b)
    {
        var x = Canon(a);
        var y = Canon(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1.0;
        // 昵称带前后缀（「XX的XX」「XXofficial」）时，包含关系比编辑距离更可靠。
        if (x.Length >= 3 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal))) return 1.0;

        var distance = Levenshtein(x, y);
        return 1.0 - (double)distance / Math.Max(x.Length, y.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// 生成"弱手段"要试的关键词：全半角/空格差异的规范写法，以及组内错字互替的写法。
    /// 最多 MaxVariantSearches 个，避免把阶梯变成无限重试。
    /// </summary>
    private static IEnumerable<string> VariantKeywords(string wanted)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { wanted };

        // 1) 规范化写法（去空格、全角转半角）；与原名相同时自动跳过。
        var plain = PlainCanon(wanted);
        if (plain.Length >= 2 && seen.Add(plain)) yield return plain;

        // 2) 错字/异体互替：把名字里的组内写法换成同组其它写法再搜。
        var count = 0;
        foreach (var group in VariantGroups)
        {
            for (var i = 0; i < group.Length; i++)
            {
                if (!wanted.Contains(group[i], StringComparison.Ordinal)) continue;
                for (var j = 0; j < group.Length; j++)
                {
                    if (j == i) continue;
                    var variant = wanted.Replace(group[i], group[j], StringComparison.Ordinal).Trim();
                    if (variant.Length < 2 || !seen.Add(variant)) continue;
                    yield return variant;
                    count++;
                    if (count >= MaxVariantSearches) yield break;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static FetchMethod ParseMethod(string? method)
        => Enum.TryParse<FetchMethod>(method, ignoreCase: true, out var parsed) ? parsed : FetchMethod.Unknown;

    /// <summary>
    /// 建立/复用「全活动视频索引」：整个运行只对搜索接口打 1–2 次，之后按 mid 或标题本地过滤。
    /// 之所以这么做：实测同一进程里搜索接口只肯痛快回第一次，之后连续调用返回
    /// 「code=0 但 result 为空」（静默限流，退避 5/10 秒也压不过去），逐个邻居去搜必然全空。
    /// </summary>
    private List<PostCandidate> RelayVideoIndex(string core, Action<string>? log, CancellationToken ct)
    {
        if (_videoIndex is not null && _videoIndexKey == core) return _videoIndex;

        var keywords = new List<string>();
        if (!core.Contains("接力", StringComparison.Ordinal)) keywords.Add(core + "接力");
        keywords.Add(core);

        var best = new List<PostCandidate>();
        foreach (var keyword in keywords)
        {
            log?.Invoke($"[视频索引] FetchMethod.VideoSearch 建立全活动索引 keyword=「{keyword}」（不限 mid，按发布时间倒序，≤3 页）");
            try
            {
                var got = Search.SearchVideos(keyword, null, log, ct, maxPages: 3);
                if (got.Count > best.Count) best = got.ToList();
                if (got.Count > 0) break; // 一次成功就不再换词，省得撞上静默限流
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"[视频索引]「{keyword}」失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        _videoIndex = best;
        _videoIndexKey = core;
        log?.Invoke($"[视频索引] 活动「{core}」索引就绪：{best.Count} 条（后续按 mid/标题本地过滤，不再打搜索接口）");
        return best;
    }

    /// <summary>昵称定位成功后顺手写进用户记忆（probe 路径没有别的落盘点）。</summary>
    private void RememberResolved(long uid, string name, FetchMethod method, bool exact, Action<string>? log)
    {
        Cache.Remember(uid, name, relay: null, hit: true, method,
            posts: null,
            note: exact ? "昵称精确/调用方指定定位" : "昵称相似定位（Exact=false），使用前建议人工确认");
        Cache.FlushIfDirty(log);
    }

    /// <summary>
    /// 从话题名里抽「核心活动名」，专供视频/文章搜索用（搜索是按标题关键词匹配的）：
    /// 去掉日期前缀（「8月15日」「2025年」）、时长后缀（「24h」「32h」）、
    /// 版本次缀（「1.5」「2.0」）与括注（【】[]（））。
    /// 实测：「8月15日魅魔日接力1.5」→「魅魔日接力」（这样才能搜到那一棒发的视频）。
    /// </summary>
    internal static string CoreActivityName(string relay)
    {
        var text = relay.Trim();
        if (text.Length == 0) return text;

        text = System.Text.RegularExpressions.Regex.Replace(text, @"[【】\[\]（）()「」『』]", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\d{4}年?", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\d{1,2}月\d{1,2}[日号]?", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\d{1,2}\s*[hH]$", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\d+(\.\d+)?$", " ");
        text = text.Trim(' ', '-', '_', '·', '·', '|', '/', '\\');
        return text.Length >= 2 ? text : relay.Trim();
    }
}
