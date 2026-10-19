#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JieLi.Tui.Data;
using JieLi.Tui.Model;

namespace JieLi.Tui.Chain;

/// <summary>
/// 把双向遍历的结果落盘成 <c>work/&lt;slug&gt;/</c> 下的三件产物。
///
/// 为什么是三个文件：
///   chain.json   —— 机器读的完整证据（节点/缺口/审计/告警），排错和二次处理都靠它；
///   review.jsonl —— 人工确认的单元，**复用既有 ReviewStore 格式**（不另造格式，
///                   这样能直接进 TUI 的复核流程，也不会出现两套 CSV/MD 契约）；
///   report.md    —— 人读的只读报告，缺口清单直接给可复制的 --add-link 命令，
///                   人工补链时不用自己拼 URL。
///
/// 棒号在报告里必须写清来源（声明/推算/人工指定/默认 1）：棒号只是辅助判据，
/// 只有把"这个数字是算出来的"标出来，人工才知道哪几棒需要核对。
/// </summary>
public static class ChainWriter
{
    private const int MergedTextMax = 4000;
    private const int NotesMax = 12;

    private static readonly JsonSerializerOptions ChainJsonOptions = BuildChainJsonOptions();

    /// <summary>写出全部产物，返回产物目录（绝对/相对取决于 workRoot 传入形式）。</summary>
    public static string Write(string workRoot, string slug, ChainResult result, RelayQuery query, string? manualDate, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(workRoot)) throw new ArgumentException("workRoot 不能为空", nameof(workRoot));
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(query);

        // slug 参数优先（--as 手工指定目录名时不能被我算出来的覆盖）；只有在没给时才回落。
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = !string.IsNullOrWhiteSpace(result.Slug)
                ? result.Slug
                : "chain_" + (string.IsNullOrWhiteSpace(result.StartPostId) ? "unknown" : result.StartPostId);
        }

        slug = slug.Trim();
        var dir = Path.Combine(workRoot, slug);
        Directory.CreateDirectory(dir);

        var views = BuildViews(result, query);
        WriteChainJson(dir, result);
        WriteReview(workRoot, slug, result, views);
        WriteReport(dir, result, query, manualDate, slug, views);

        log?.Invoke($"已写出 {dir}（chain.json / review.jsonl / report.md）");
        return dir;
    }

    // ================================================================ chain.json

    private static void WriteChainJson(string dir, ChainResult result)
    {
        var json = JsonSerializer.Serialize(result, ChainJsonOptions);
        File.WriteAllText(Path.Combine(dir, "chain.json"), json, new UTF8Encoding(false));
    }

    private static JsonSerializerOptions BuildChainJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 中文不转义，否则 report 里看不出作者名、人工没法直接读
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static info =>
        {
            if (info.Type != typeof(ChainResult)) return;
            for (var i = info.Properties.Count - 1; i >= 0; i--)
            {
                // WithFacts 是计算属性（Nodes.Where(...)）。直接序列化会把整份节点列表
                // 再抄一遍，chain.json 体积翻倍且两份数据容易看岔，所以剔除掉。
                if (!string.Equals(info.Properties[i].Name, nameof(ChainResult.WithFacts), StringComparison.Ordinal)) continue;
                info.Properties.RemoveAt(i);
            }
        });
        options.TypeInfoResolver = resolver;
        return options;
    }

    // ================================================================ review.jsonl

    private static void WriteReview(string workRoot, string slug, ChainResult result, List<NodeView> views)
    {
        var items = new List<ReviewItem>(views.Count);
        foreach (var view in views)
        {
            var facts = view.Facts;
            var item = new ReviewItem
            {
                CardId = view.CardId,
                Bar = view.Bar,
                BarRaw = Blank(view.BarRaw) ? null : view.BarRaw,
                IsEx = view.IsEx,
                DeclaredTime = Blank(view.DeclaredTime) ? null : view.DeclaredTime,
                PubTs = facts.PubTs,
                PubTimeText = facts.PubLocal == DateTime.MinValue ? string.Empty : facts.PubLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                DeviationMinutes = view.DeviationMinutes,
                PosterName = facts.AuthorName,
                PosterMid = facts.AuthorUid,
                CreatorInText = Blank(facts.CreatorInText) ? null : facts.CreatorInText,
                ResolvedAuthor = view.ResolvedAuthor,
                ResolvedAuthorLink = view.ResolvedAuthorLink,
                PrevName = Blank(view.PrevName) ? null : view.PrevName,
                NextName = Blank(view.NextName) ? null : view.NextName,
                IsProxy = facts.IsProxy,
                ProxyName = Blank(facts.ProxyName) ? null : facts.ProxyName,
                OpusUrl = Blank(facts.Url) ? view.Node.Ref.Url : facts.Url,
                Type = view.Type,
                TypeLabel = view.TypeLabel,
                Conflicts = view.Node.Notes.Where(n => n.Contains("方向冲突", StringComparison.Ordinal)).ToList(),
                Notes = BuildReviewNotes(view),
                MergedText = Truncate(facts.MergedText, MergedTextMax),
                // 自动遍历只产出"候选记录"，确认必须由人来做（CLI 也拒绝未经确认的数据）
                Confirmed = false,
                Unattributable = view.Unattributable,
            };

            items.Add(item);
        }

        ReviewStore.Save(workRoot, slug, items);
    }

    private static List<string> BuildReviewNotes(NodeView view)
    {
        var notes = new List<string>
        {
            $"来源：{ChainWalker.MethodLabel(view.Node.Source)}（方向 {ChainWalker.DirLabel(view.Node.Direction)}，深度 {view.Node.Depth}，置信 {view.Node.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}）",
        };

        if (!Blank(view.Node.MatchedBy)) notes.Add($"匹配依据：{view.Node.MatchedBy}");

        foreach (var note in view.Node.Notes)
        {
            if (Blank(note) || notes.Contains(note)) continue;
            notes.Add(note);
        }

        if (view.Node.Source == FetchMethod.AiAssist) notes.Add("AI 判定，需人工复核");
        if (view.Node.IsSupplement) notes.Add("补充链接节点（--add-link 人工加入）");
        if (view.Node.IsStart) notes.Add("入口帖（双向起点，棒号来自声明或默认）");
        if (view.Unattributable) notes.Add("本条有未定位的缺口，见 report.md 缺口清单");

        if (notes.Count > NotesMax) notes = notes.Take(NotesMax).ToList();
        return notes;
    }

    // ================================================================ 视图构建

    /// <summary>
    /// 把节点整理成"按时间排序 + 棒号来源标注 + 认定创作者"的行视图。
    /// 无事实的节点（读取失败）不写进 review/report 的明细，只在告警里提一句。
    /// </summary>
    private static List<NodeView> BuildViews(ChainResult result, RelayQuery query)
    {
        var allFacts = result.Nodes.Where(n => n.Facts is not null).Select(n => n.Facts!).ToList();
        var views = new List<NodeView>(result.Nodes.Count);

        foreach (var node in result.Nodes)
        {
            if (node.Facts is null)
            {
                result.Warnings.Add($"节点 {node.Ref.Url} 没有读到内容（无事实），未写入明细");
                continue;
            }

            var facts = node.Facts;
            var view = new NodeView
            {
                Node = node,
                Facts = facts,
                CardId = Blank(facts.PostId) ? node.Ref.Id : facts.PostId,
                PosterMid = facts.AuthorUid,
                PosterName = facts.AuthorName,
                BarRaw = facts.BarRaw ?? string.Empty,
                IsEx = facts.IsEx,
                DeclaredTime = facts.DeclaredTime ?? string.Empty,
                SortTime = facts.PubLocal,
                Type = KindOf(facts.Kind),
                Bar = facts.BarNumber,
            };

            view.TypeLabel = !Blank(facts.WorkKindLabel)
                ? facts.WorkKindLabel!
                : BarParser.InferWorkKind(view.Type, facts.MergedText);
            // BarParser.InferWorkKind 只认 DRAW/AV/ARTICLE/FORWARD/WORD 这些卡片类型；
            // opus/topic 这类入口在没有更多线索时会返回空串。空的 TypeLabel 会让下游
            // 审阅表与 CSV 的"类型"列变成空白，所以这里兜一个"其他"，宁可粗也不留空。
            if (Blank(view.TypeLabel)) view.TypeLabel = "其他";
            else view.TypeLabel = LegacyWorkKind(view.TypeLabel!);

            // 棒号来源：声明值优先；小数棒号不采纳；EX 只认显式标记。
            if (!Blank(view.BarRaw) && IsDecimalBar(view.BarRaw))
            {
                view.Bar = null;
                view.BarInferred = false;
                view.Node.Notes.Add($"棒号原文「{view.BarRaw}」含小数，按 N/A 处理（小数棒号不采纳）");
            }

            view.BarManual = node.Notes.Any(n => n.Contains(ChainWalker.BarManualNote, StringComparison.Ordinal));
            view.BarInferred = node.Notes.Any(n => n.StartsWith(ChainWalker.BarInferredPrefix, StringComparison.Ordinal));

            var (author, link) = ResolveCreator(facts, allFacts);
            view.ResolvedAuthor = author;
            view.ResolvedAuthorLink = link;

            view.PrevName = node.Facts!.Prevs.Count > 0 ? Clean(node.Facts.Prevs[0].Name) : string.Empty;
            view.NextName = node.Facts!.Nexts.Count > 0 ? Clean(node.Facts.Nexts[0].Name) : string.Empty;

            if (facts.PubLocal != DateTime.MinValue && MatchVerifier.TryParseHourMinute(facts.DeclaredTime, out var declaredMinutes))
                view.DeviationMinutes = (int)Math.Round((facts.PubLocal - NearestSlot(facts.PubLocal, declaredMinutes)).TotalMinutes);

            // 注意：这里**不**把「本条有未定位的邻居（缺口）」算成无法归属。
            // 缺口是链条层面的事（写在 report.md 的缺口清单里），和这一棒的作者/作品能否归属无关。
            // 早先把它算进来的后果：第 6、14 棒作者与链接都齐，TUI 里却显示「无法归属」，极易误判。
            view.Unattributable = facts.AuthorUid <= 0;

            views.Add(view);
        }

        // 用户规则：输出按时间排序。读不到发布时间的排最后（避免它们挤在表头）。
        views.Sort(static (a, b) =>
        {
            var ta = a.SortTime == DateTime.MinValue ? DateTime.MaxValue : a.SortTime;
            var tb = b.SortTime == DateTime.MinValue ? DateTime.MaxValue : b.SortTime;
            var cmp = ta.CompareTo(tb);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.CardId, b.CardId);
        });

        FillNeighborNamesFromChain(views);
        ApplyDefaultStartBar(views);
        return views;
    }

    /// <summary>
    /// 全链没有任何棒号时，入口按"起点默认 1"处理并在报告里标注请核对。
    /// 放在最后做，是因为必须先知道"整条链真的一根棒号都没有"。
    /// </summary>
    private static void ApplyDefaultStartBar(List<NodeView> views)
    {
        if (views.Count == 0) return;
        if (views.Any(v => v.Bar is not null || v.IsEx)) return;

        var start = views.FirstOrDefault(v => v.Node.IsStart) ?? views[0];
        start.Bar = 1;
        start.BarDefaultStart = true;
        if (Blank(start.BarRaw)) start.BarRaw = "1";
        start.Node.Notes.Add("起点默认1，请核对（整条链没有任何棒号声明）");
    }

    /// <summary>
    /// 帖子里没写上一棒/下一棒时，用"链序相邻"补一个名字，方便人工顺着链读。
    /// 链序：有棒号按棒号（EX 排最后），没有棒号就按时间 —— 和 ArchiveRenderer 的输出顺序一致。
    /// </summary>
    private static void FillNeighborNamesFromChain(List<NodeView> views)
    {
        if (views.Count < 2) return;

        var hasBars = views.Count(v => v.Bar is not null || v.IsEx) >= 2;
        var chain = hasBars
            ? views.OrderBy(v => v.IsEx ? int.MaxValue : (v.Bar ?? int.MaxValue)).ThenBy(v => v.SortTime).ToList()
            : views.ToList();

        for (var i = 0; i < chain.Count; i++)
        {
            if (Blank(chain[i].PrevName) && i > 0) chain[i].PrevName = chain[i - 1].ResolvedAuthor;
            if (Blank(chain[i].NextName) && i + 1 < chain.Count) chain[i].NextName = chain[i + 1].ResolvedAuthor;
        }
    }

    /// <summary>
    /// 把"平台形态"折进既有 CSV 的类型词表。
    /// 既有 683 行里「类型」写的是**作品形态**（插画 436 / 文章 78 / 视频 42 / 漫画 / MMD / 音乐…），
    /// 没有「动态」这个词；链遍历里一大批帖是图片动态，人工归档时一律写「插画」。
    /// 所以这里把 动态 → 插画，其余（文章/视频）原样保留；更细的类型（音乐/手书/视觉小说…）
    /// 仍然只能靠 TUI 的 K 键人工改。
    /// </summary>
    private static string LegacyWorkKind(string label)
        => label switch
        {
            "动态" => "插画",
            "图文" => "插画",
            _ => label,
        };

    /// <summary>
    /// 认定创作者：优先"链上其他帖子 @ 回来的人"（反指最可信，说明对方认了这棒），
    /// 其次帖子里自己写的 creator_in_text（代发场景），最后才是发布者本人。
    /// </summary>
    private static (string Author, string? Link) ResolveCreator(PostFacts facts, List<PostFacts> all)
    {
        // 零轮：帖子里自己写明了创作者（代发场景）→ 以它为准。
        // 为什么放在反指之前：邻居的 @ 通常指向**代发的发布账号**（因为大家看到的就是这个号在发），
        // 拿它当创作者会把代发者写成作者。实测第 6 棒「5:00 代发：@龙胆紫苑 作者：@Yolandabaka」
        // 曾被反指判成 龙胆紫苑，而人工 CSV 写的是 Yolandabaka。
        if (!Blank(facts.CreatorInText))
        {
            var creator = facts.CreatorInText!;
            var uid = 0L;
            foreach (var m in facts.Mentions)
            {
                if (Blank(m.Uid) || !TextNormalizer.SameName(m.Name, creator)) continue;
                if (long.TryParse(m.Uid!.Trim(), out var parsed) && parsed > 0) { uid = parsed; break; }
            }
            // 创作者 uid 拿不到时用发布账号的空间链接兜底（人工归档也是这么写的，
            // 例如 18:30「阿雯」在话题里没有自己的号，就用代发号的链接）。
            return (creator, UidLink(uid) ?? UidLink(facts.AuthorUid));
        }

        // 第一轮只认 uid 反指（最强证据），第二轮才按名字（可能有重名，也可能一人多棒）。
        foreach (var byUid in new[] { true, false })
        {
            foreach (var other in all)
            {
                if (ReferenceEquals(other, facts)) continue;
                if (!Blank(facts.PostId) && other.PostId == facts.PostId) continue;

                foreach (var link in other.Prevs.Concat(other.Nexts))
                {
                    if (!LinkPointsTo(link, facts, byUid)) continue;
                    return (Clean(link.Name), UidLink(link.Uid) ?? UidLink(facts.AuthorUid));
                }
            }
        }

        return (facts.AuthorName, UidLink(facts.AuthorUid));
    }

    private static bool LinkPointsTo(NeighborLink link, PostFacts facts, bool byUid)
    {
        var linkHasUid = !Blank(link.Uid) && long.TryParse(link.Uid!.Trim(), out var uid) && uid > 0;
        if (byUid)
        {
            if (!linkHasUid) return false;
            long.TryParse(link.Uid!.Trim(), out var linkUid);
            return facts.AuthorUid > 0 && linkUid == facts.AuthorUid;
        }

        if (linkHasUid) return false; // 有 uid 的留给第一轮，避免名字撞车时误判
        var name = Clean(link.Name);
        if (Blank(name)) return false;
        return TextNormalizer.SameName(name, facts.AuthorName)
            || (!Blank(facts.CreatorInText) && TextNormalizer.SameName(name, facts.CreatorInText!));
    }

    private static string? UidLink(string? uid)
        => !Blank(uid) && long.TryParse(uid!.Trim(), out var value) && value > 0
            ? $"https://space.bilibili.com/{value.ToString(CultureInfo.InvariantCulture)}"
            : null;

    private static string? UidLink(long uid)
        => uid > 0 ? $"https://space.bilibili.com/{uid.ToString(CultureInfo.InvariantCulture)}" : null;

    // ================================================================ report.md

    private static void WriteReport(string dir, ChainResult result, RelayQuery query, string? manualDate, string slug, List<NodeView> views)
    {
        var sb = new StringBuilder(16 * 1024);
        var entry = result.Nodes.FirstOrDefault(n => n.IsStart) ?? result.Nodes.FirstOrDefault();
        var entryUrl = entry?.Facts?.Url ?? entry?.Ref.Url ?? string.Empty;
        if (Blank(entryUrl)) entryUrl = "<入口帖链接>";

        var relayName = result.RelayName ?? query.RelayName ?? "未识别活动";
        var window = query.WindowMinutes > 0 ? query.WindowMinutes : 90;

        sb.AppendLine($"# 接力链报告 · {relayName}");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- 产物目录：`work/{slug}/`");
        sb.AppendLine($"- 入口帖：{entryUrl}");
        sb.AppendLine($"- 日期依据：{(Blank(manualDate) ? "入口帖发布日（未给 --date）" : $"--date {manualDate}")}");
        sb.AppendLine($"- 活动时段：{Fmt(query.DayStart)} ~ {Fmt(query.DayEnd)}，时间窗 ±{window} 分钟");
        sb.AppendLine();

        // ---- 总览 ----
        var declared = views.Count(v => v.Bar is not null && !v.BarInferred && !v.BarManual && !v.BarDefaultStart);
        var inferred = views.Count(v => v.BarInferred);
        var manual = views.Count(v => v.BarManual);
        var exCount = views.Count(v => v.IsEx);
        var none = views.Count(v => v.Bar is null && !v.IsEx);
        var requests = result.Audit.FirstOrDefault(a => a.Step == ChainWalker.AuditStepRequestCount)?.Detail ?? "-";
        var cookie = result.Audit.FirstOrDefault(a => a.Step == ChainWalker.AuditStepCookie)?.Detail ?? "-";
        var conflicts = result.Warnings.Count(w => w.Contains("方向冲突", StringComparison.Ordinal));

        sb.AppendLine("## 总览");
        sb.AppendLine();
        sb.AppendLine($"- 链上节点：{result.Nodes.Count}（有事实/已定位 {views.Count}）");
        sb.AppendLine($"- 缺口：{result.Gaps.Count}");
        sb.AppendLine($"- 棒号：声明 {declared}、推算 {inferred}、人工指定 {manual}、EX {exCount}、N/A {none}");
        sb.AppendLine($"- 请求数：{requests}");
        sb.AppendLine($"- 登录态：{cookie}");
        sb.AppendLine($"- 末棒标记：{(result.ForwardComplete ? "有（Forward 已到末棒）" : "没有（可能漏标，链路可能不完整）")}");
        sb.AppendLine($"- 告警：{result.Warnings.Count} 条，其中方向冲突 {conflicts} 条");
        sb.AppendLine();

        // ---- 明细 ----
        sb.AppendLine("## 明细（按发布时间排序）");
        sb.AppendLine();
        sb.AppendLine("| # | 棒号 | 声明时段 | 发布时间 | 发布者(uid) | 认定创作者 | 方向 | 置信 | 来源 | 链接 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        var index = 0;
        foreach (var v in views)
        {
            index++;
            var pubTime = v.Facts.PubLocal == DateTime.MinValue ? "（未知）" : v.Facts.PubLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            var poster = Cell(v.PosterName) + (v.PosterMid > 0 ? $"({v.PosterMid.ToString(CultureInfo.InvariantCulture)})" : "(未知)");
            sb.AppendLine($"| {index} | {Cell(BarDisplay(v))} | {Cell(Blank(v.DeclaredTime) ? "-" : v.DeclaredTime)} | {pubTime} | {poster} | {Cell(v.ResolvedAuthor)} | {ChainWalker.DirLabel(v.Node.Direction)} | {v.Node.Confidence.ToString("0.00", CultureInfo.InvariantCulture)} | {ChainWalker.MethodLabel(v.Node.Source)} | {Cell(v.Node.Ref.Url)} |");
        }
        if (views.Count == 0) sb.AppendLine("| - | - | - | - | - | - | - | - | - | - |");
        sb.AppendLine();

        // ---- 缺口清单（这一步是人工补链的入口，必须给可直接复制的命令）----
        sb.AppendLine("## 缺口清单");
        sb.AppendLine();
        if (result.Gaps.Count == 0)
        {
            sb.AppendLine("没有缺口。");
            sb.AppendLine();
        }
        else
        {
            var gapIndex = 0;
            foreach (var gap in result.Gaps)
            {
                gapIndex++;
                sb.AppendLine($"### 缺口 {gapIndex} · {GapKindLabel(gap.Kind)}");
                sb.AppendLine();
                sb.AppendLine($"- 方向：{ChainWalker.DirLabel(gap.Direction)}");
                sb.AppendLine($"- 来源帖：{(!Blank(gap.FromPostUrl) ? gap.FromPostUrl : (Blank(gap.FromPostId) ? "（未知）" : "postId=" + gap.FromPostId))}");

                var expected = new List<string>();
                if (!Blank(gap.ExpectedName)) expected.Add($"名字「{gap.ExpectedName}」");
                if (!Blank(gap.ExpectedBar)) expected.Add($"棒号 {gap.ExpectedBar}");
                if (!Blank(gap.ExpectedTime)) expected.Add($"时段 {gap.ExpectedTime}");
                sb.AppendLine($"- 期望：{(expected.Count > 0 ? string.Join("，", expected) : "（未知）")}");
                sb.AppendLine($"- 原因：{gap.Reason}");
                if (gap.TriedMethods.Count > 0) sb.AppendLine($"- 试过：{string.Join("；", gap.TriedMethods)}");

                var cmd = Blank(gap.SuggestedCommand) ? "--add-link <缺失那一棒的帖子链接>" : gap.SuggestedCommand!;
                sb.AppendLine($"- 可直接复制：`jieli --link {entryUrl} {cmd}`");
                sb.AppendLine();
            }
        }

        // ---- 棒号标注 ----
        sb.AppendLine("## 棒号标注（推算/人工/默认/N-A）");
        sb.AppendLine();
        var flagged = views.Where(v => v.BarDefaultStart || v.BarInferred || v.BarManual || (v.Bar is null && !v.IsEx)).ToList();
        if (flagged.Count == 0)
        {
            sb.AppendLine("全部棒号都来自帖内声明，没有推算值。");
        }
        else
        {
            foreach (var v in flagged)
            {
                string why;
                if (v.BarDefaultStart) why = "整条链没有任何棒号声明，起点按 1 处理，请核对";
                else if (v.BarManual) why = "棒号由 --add-link 人工指定";
                else if (v.BarInferred) why = "由源帖/相邻帖的棒号标注推算，非帖内直接声明";
                else why = "没有可用的棒号声明，也没有可推算的邻居，输出 N/A";
                sb.AppendLine($"- {v.CardId}（{Cell(v.PosterName)}）：{BarDisplay(v)} —— {why}");
            }
        }
        sb.AppendLine();

        // ---- 审计告警 ----
        sb.AppendLine("## 审计告警");
        sb.AppendLine();
        if (result.Warnings.Count == 0)
        {
            sb.AppendLine("- 无方向冲突或流程告警。");
        }
        else
        {
            foreach (var warning in result.Warnings) sb.AppendLine($"- ⚠ {warning}");
        }

        // "验收候选"失败是正常的拒收记录，不算告警；只列流程性失败（读帖/定位/找候选）。
        var failed = result.Audit.Where(a => !a.Success && a.Step != "验收候选").ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"流程失败步骤（共 {failed.Count} 条，取前 15）：");
            foreach (var entryAudit in failed.Take(15))
                sb.AppendLine($"- {entryAudit.Step}（{ChainWalker.DirLabel(entryAudit.Direction)}）：{Truncate(entryAudit.Detail ?? string.Empty, 160)}");
        }
        sb.AppendLine();

        // ---- 产物说明 ----
        sb.AppendLine("## 产物说明");
        sb.AppendLine();
        sb.AppendLine("- `chain.json`：完整遍历结果（节点/缺口/审计/告警），机器读，用于排错与二次处理。");
        sb.AppendLine("- `review.jsonl`：一棒一条的复核单元（与 ReviewStore 同格式），用 `jieli --review " + slug + "` 打开人工确认。");
        sb.AppendLine("- `report.md`：本报告（只读）。确认后的数据才能进入 CSV/Markdown 产物。");

        File.WriteAllText(Path.Combine(dir, "report.md"), sb.ToString(), new UTF8Encoding(false));
    }

    // ================================================================ 小工具

    /// <summary>
    /// 时间锚点：帖子里声明的时段若和发布日隔了一天（跨午夜），挑最近的那一天。
    /// 与 MatchVerifier 的槽位算法保持一致，报告的"声明时段"栏才不会和验收结论打架。
    /// </summary>
    private static DateTime NearestSlot(DateTime local, int minutes)
    {
        var day = local.Date;
        var best = day.AddMinutes(minutes);
        var bestDiff = Math.Abs((best - local).TotalMinutes);
        foreach (var offset in new[] { -1, 1 })
        {
            var candidate = day.AddDays(offset).AddMinutes(minutes);
            var diff = Math.Abs((candidate - local).TotalMinutes);
            if (diff < bestDiff) { best = candidate; bestDiff = diff; }
        }
        return best;
    }

    private static string BarDisplay(NodeView view)
    {
        if (view.IsEx) return "EX";
        if (view.Bar is not { } bar) return "N/A";
        if (view.BarDefaultStart) return $"{bar.ToString(CultureInfo.InvariantCulture)}（起点默认1，请核对）";
        if (view.BarManual) return $"{bar.ToString(CultureInfo.InvariantCulture)}（人工指定）";
        if (view.BarInferred) return $"{bar.ToString(CultureInfo.InvariantCulture)}（推算）";
        return bar.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>小数棒号（3.5 之类）不采纳 —— 既有的 BarParser 也不认，混进产物只会让排序错乱。</summary>
    private static bool IsDecimalBar(string raw)
    {
        var text = TextNormalizer.Normalize(raw);
        return text.Contains('.') || text.Contains('．');
    }

    private static string KindOf(PostKind kind) => kind switch
    {
        PostKind.Video => "video",
        PostKind.Topic => "topic",
        _ => "opus",
    };

    private static string GapKindLabel(string kind) => kind switch
    {
        "neighbor" => "邻居缺失（帖内 @ 的名字没找到对应帖子）",
        "candidate" => "候选缺失（定位到人，但空间里没有窗口内的帖子）",
        "uid" => "定位失败（名字 → uid 失败）",
        "reader" => "读取失败（帖子读不到）",
        "bar" => "棒号缺失",
        "time" => "时间异常",
        _ => kind,
    };

    internal static string Clean(string? raw) => TextNormalizer.CleanMention(raw ?? string.Empty);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Cell(string? value)
    {
        if (Blank(value)) return "-";
        return value!.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }

    internal static string Truncate(string? raw, int max)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        if (raw.Length <= max) return raw;
        return raw[..Math.Max(0, max - 1)] + "…";
    }

    private static string Fmt(DateTime value)
        => value == DateTime.MinValue ? "（未指定）" : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ================================================================ 行视图

    private sealed class NodeView
    {
        public required ChainNode Node { get; init; }
        public required PostFacts Facts { get; init; }

        public string CardId { get; set; } = string.Empty;
        public string PosterName { get; set; } = string.Empty;
        public long PosterMid { get; set; }

        public int? Bar { get; set; }
        public string BarRaw { get; set; } = string.Empty;
        public bool IsEx { get; set; }
        public bool BarInferred { get; set; }
        public bool BarManual { get; set; }
        public bool BarDefaultStart { get; set; }

        public string DeclaredTime { get; set; } = string.Empty;
        public int? DeviationMinutes { get; set; }
        public DateTime SortTime { get; set; }

        public string ResolvedAuthor { get; set; } = string.Empty;
        public string? ResolvedAuthorLink { get; set; }
        public string PrevName { get; set; } = string.Empty;
        public string NextName { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string TypeLabel { get; set; } = string.Empty;
        public bool Unattributable { get; set; }
    }
}
