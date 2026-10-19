#nullable enable
using System.Globalization;
using JieLi.Tui.Data;

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
//
// 实现说明（为什么这么做）：
//   * 反向 @ 结构证据分「uid 精确」和「按名字」两档：实测大量接力帖是手打 @（昵称只是文本，
//     拿不到 uid），若只认 uid 结构证据会几乎全部落空，只能靠时间窗，反而更容易把
//     同一用户的无关投稿收进来。按名字命中也算结构证据，但判据文案里写清是「@名」。
//   * 时间锚点三级：
//       ① 源帖这条邻居指认里明确写了时段（如「下一棒 @X 21:00」）→ 直接用它；
//       ② 源帖自己声明了时段 → 相邻棒的槽位 ≈ 本棒时段 ± 1 小时（整点/半小时接力，
//          90 分钟窗口正好吸收发布延迟）；
//       ③ 都没有 → 用源帖发布时间当槽位估计，同样 ± 1 小时。
//     锚点选「相邻棒的期望时段」而不是「源帖发布时间」，是为了让 90 分钟窗口围绕
//     「应该什么时候发」而不是围绕「上一棒什么时候发」，超窗判定才说得通。
//   * 超窗兜底（0.35）只给**接力特征**（棒号/时段/上下棒/EX），不给「名字对得上」，
//     因为名字对得上是搜索的入场券，不是相邻证据；否则同一用户的任意投稿都会被收进来。
// ===========================================================================

/// <summary>一次验收的结论。</summary>
public sealed class MatchOutcome
{
    public bool Accepted { get; set; }
    public double Confidence { get; set; }
    /// <summary>判据描述，进 audit 与 report（如 "反向@uid 命中" / "时间差 12 分钟"）。</summary>
    public string Reason { get; set; } = "";
    public string MatchedBy { get; set; } = "";

    /// <summary>本次比对用的时间锚点（相邻棒的期望发布时段）；null = 没有可用时间信息。</summary>
    public DateTime? ExpectedSlot { get; set; }

    /// <summary>候选发布时间相对锚点的分钟差（正 = 晚于锚点）。</summary>
    public int? DiffMinutes { get; set; }
}

/// <summary>候选验收。</summary>
public sealed class MatchVerifier
{
    /// <summary>发布时间离期望时段超过这么多天，就直接判「不是同一场活动」（同话题往年帖子）。</summary>
    private const double YearGateDays = 3.0;

    private readonly ChainHttp _http;

    public MatchVerifier(ChainHttp http) => _http = http;

    /// <summary>
    /// 验收本身不联网；保留注入的 http 是为了与遍历器共用同一个实例（退避节流、请求计数统一）。
    /// 将来若要「反查评论 members.mid」补 uid，也应从这里走，不要另建连接。
    /// </summary>
    public ChainHttp Http => _http;

    /// <summary>
    /// 用源帖子（<paramref name="from"/>）里那条邻居指认（<paramref name="link"/>）去验收候选帖子。
    /// </summary>
    public MatchOutcome Verify(PostFacts from, NeighborLink link, PostFacts candidate, RelayQuery query, WalkDirection dir)
    {
        var outcome = new MatchOutcome();

        if (ReferenceEquals(from, candidate) || (!string.IsNullOrEmpty(candidate.PostId) && candidate.PostId == from.PostId))
        {
            outcome.Reason = "候选就是源帖自身，跳过";
            return outcome;
        }

        // ---- 结构证据：候选有没有反向 @ 回源帖作者（uid 优先，名字次之） ----
        var structural = FindReverseReference(from, candidate);

        // ---- 时间证据：候选发布时间 vs 相邻棒的期望时段 ----
        var slot = ExpectedSlot(from, link, dir);
        outcome.ExpectedSlot = slot;

        int? diffMinutes = null;
        if (slot is { } anchor && candidate.PubLocal != DateTime.MinValue)
            diffMinutes = (int)Math.Round((candidate.PubLocal - anchor).TotalMinutes, MidpointRounding.AwayFromZero);
        outcome.DiffMinutes = diffMinutes;

        // ---- 年份闸门：同一个话题每年复用（「7月26日卡娜日」这类年度活动天天有人发，
        //      往年的棒次帖跟今年写着同样的钟点），只看「声明时段」会把 2024/2025 的帖子
        //      收进 2026 那场（实测：2026 卡娜日混进了 2024、2025 各十几棒）。
        //      用「发布时间离期望时段的绝对距离」兜底：活动当天 ±3 天以外一律不采信
        //      （实测提前发布最多 1 天，留足余量；否则会误伤跨年零点那几棒）。
        if (slot is { } gate && candidate.PubLocal != DateTime.MinValue)
        {
            var skewDays = (candidate.PubLocal - gate).TotalDays;
            if (Math.Abs(skewDays) > YearGateDays)
            {
                outcome.Reason =
                    $"发布时间（{candidate.PubLocal:yyyy-MM-dd HH:mm}）与期望时段（{gate:yyyy-MM-dd HH:mm}）"
                    + $"相差 {Math.Abs(skewDays):F1} 天，超出本场活动范围（同话题往年的帖子），不采信";
                return outcome;
            }
        }

        // ---- 时间证据之二：候选自己声明的时段（真实语料里这条比发布时间可靠）----
        // 为什么必须看它：实测有棒次提前一天发布（15:30 那棒 10-07 14:57 就占了位），
        // 发布时间离活动当天差 20 小时，纯按 PubTs 判会被当成「超窗」扔掉；
        // 而帖子里明明白白写着「15：30@某人」。人工整理时也是按声明时段归位的。
        int? declaredDiff = null;
        DateTime? declaredSlot = null;
        if (slot is { } anchor2 && TryParseHourMinute(candidate.DeclaredTime, out var candMinutes))
        {
            declaredSlot = PickSlotDay(anchor2, candMinutes, dir);
            declaredDiff = (int)Math.Round((declaredSlot.Value - anchor2).TotalMinutes, MidpointRounding.AwayFromZero);
        }

        var window = query.WindowMinutes > 0 ? query.WindowMinutes : 90;
        var pubInWindow = diffMinutes is { } d && Math.Abs(d) <= window;

        // 声明时段的容差单独收紧：它是「帖子里写死的钟点」，不是「可能延误的发布时间」。
        // 用整个窗口（默认 ±90）会让相邻棒/附加帖冒充目标棒——实测那张 22:08 发布的
        // 「美丽与思念之旅SP」声明 18:30，被当成 19:00 那棒收下，把真正的 19:00 挤掉了。
        // 一个棒次间隔通常是 30 分钟，所以 35 分钟就够容忍「四舍五入 + 半格误差」。
        var declaredTolerance = Math.Min(window, 35);
        var declaredInWindow = declaredDiff is { } dd && Math.Abs(dd) <= declaredTolerance;

        // 方向一致性：正向找「下一棒」时，候选不该自称比期望时段更早的棒次
        // （那说明它是刚走过的那一棒的另一稿/附加帖）；反向同理。
        // 允许 15 分钟以内的偏差，避免整点/半点附近的取整把正常棒次误杀。
        var declaredDirectionConflict = declaredDiff is { } ddd &&
            (dir == WalkDirection.Forward ? ddd < -15 : dir == WalkDirection.Backward && ddd > 15);
        if (declaredDirectionConflict) declaredInWindow = false;

        var inWindow = pubInWindow || declaredInWindow;

        var parts = new List<string>(4);
        if (structural.Matched) parts.Add(structural.Description);
        if (diffMinutes is { } diff)
            parts.Add($"发布时间与期望时段（{slot!.Value:yyyy-MM-dd HH:mm}）相差 {diff} 分钟，窗口 ±{window}");
        else
            parts.Add("没有可用的时间锚点（源帖发布时间/声明时段都拿不到）");
        if (declaredDiff is { } dj)
        {
            var verdict = declaredDirectionConflict
                ? "，但方向不符（找下一棒/上一棒时它自称更早/更晚的棒次，可能是同一棒的另一稿），不采信"
                : declaredInWindow ? $"，在 ±{declaredTolerance} 分钟内" : $"，超出 ±{declaredTolerance} 分钟";
            parts.Add($"候选声明时段「{candidate.DeclaredTime}」与期望时段相差 {dj} 分钟{verdict}");
        }

        // ① 结构证据（权重最高：一人可多棒，发布时间说明不了是不是同一棒）
        if (structural.Matched)
        {
            outcome.Accepted = true;
            outcome.Confidence = inWindow ? 0.99 : 0.95;
            outcome.MatchedBy = structural.UidExact
                ? (inWindow ? "反向@uid+时间窗" : "反向@uid")
                : (inWindow ? "反向@名+时间窗" : "反向@名");
            outcome.Reason = string.Join("；", parts);
            return outcome;
        }

        // ② 时间窗内（没有结构证据也能收，但置信中等：可能是同一用户的别的稿）
        if (inWindow)
        {
            outcome.Accepted = true;
            outcome.Confidence = 0.6;
            outcome.MatchedBy = declaredInWindow && !pubInWindow ? "声明时段" : "时间窗";
            outcome.Reason = string.Join("；", parts);
            return outcome;
        }

        // ③ 超窗但仍有接力特征 → 收下并降置信（用户明确要求：可能一人多棒/发布延误）
        var (weak, weakWhy) = FindWeakEvidence(from, link, candidate, query);
        if (weak)
        {
            outcome.Accepted = true;
            outcome.Confidence = 0.35;
            outcome.MatchedBy = "弱证据";
            outcome.Reason = string.Join("；", parts) + "；" + weakWhy + "；近邻超窗(可能一人多棒)";
            return outcome;
        }

        outcome.Reason = string.Join("；", parts) + "；无反向 @、超出时间窗、也没有接力特征 → 拒收";
        return outcome;
    }

    // ------------------------------------------------------------------ 结构证据

    private readonly struct ReverseEvidence
    {
        public bool Matched { get; init; }
        /// <summary>true = 命中 uid（更硬），false = 只按名字命中。</summary>
        public bool UidExact { get; init; }
        public string Description { get; init; }
    }

    private static ReverseEvidence FindReverseReference(PostFacts from, PostFacts candidate)
    {
        // 源帖一侧可能被反向 @ 到的名字：发布者、正文声明的创作者、代发者（代发时邻居可能 @ 的是代发人）。
        var names = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(from.AuthorName)) names.Add(from.AuthorName);
        if (!string.IsNullOrWhiteSpace(from.CreatorInText)) names.Add(from.CreatorInText!);
        if (!string.IsNullOrWhiteSpace(from.ProxyName)) names.Add(from.ProxyName!);
        var uidText = from.AuthorUid > 0 ? from.AuthorUid.ToString(CultureInfo.InvariantCulture) : null;

        // 先看候选的「上一棒/下一棒」显式指认（结构最强），再退到正文 @ 列表。
        static IEnumerable<(NeighborLink Link, string Where)> Links(PostFacts f)
        {
            foreach (var l in f.Prevs) yield return (l, "上一棒");
            foreach (var l in f.Nexts) yield return (l, "下一棒");
        }

        foreach (var (l, where) in Links(candidate))
        {
            if (UidEquals(l.Uid, uidText))
                return new ReverseEvidence { Matched = true, UidExact = true, Description = $"反向@uid 命中：候选的「{where}」指向源帖作者 uid={uidText}" };
            var hit = names.FirstOrDefault(n => TextNormalizer.SameName(l.Name, n));
            if (hit is not null)
                return new ReverseEvidence { Matched = true, UidExact = false, Description = $"反向@名 命中：候选的「{where}」指向「{l.Name}」（与源帖作者「{hit}」同名；源帖没给出 uid，按名字算结构证据）" };
        }

        foreach (var m in candidate.Mentions)
        {
            if (UidEquals(m.Uid, uidText))
                return new ReverseEvidence { Matched = true, UidExact = true, Description = $"反向@uid 命中：候选正文 @ 列表含 uid={uidText}" };
            var hit = names.FirstOrDefault(n => TextNormalizer.SameName(m.Name, n));
            if (hit is not null)
                return new ReverseEvidence { Matched = true, UidExact = false, Description = $"反向@名 命中：候选正文 @ 了「{m.Name}」（与源帖作者「{hit}」同名；按名字算结构证据）" };
        }

        return default;
    }

    private static bool UidEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (long.TryParse(a.Trim(), out var ua) && long.TryParse(b.Trim(), out var ub)) return ua == ub;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 时间锚点

    /// <summary>
    /// 相邻棒的**期望发布时段**（时间锚点的核心）。
    /// 供遍历器给候选排序、给报告解释「为什么这么判」共用，所以是 public static。
    /// </summary>
    public static DateTime? ExpectedSlot(PostFacts from, NeighborLink link, WalkDirection dir)
    {
        var fromLocal = from.PubLocal;
        if (fromLocal == DateTime.MinValue) return null;

        // ① 源帖对这条邻居明确写出的时段（最直接）
        if (TryParseHourMinute(link.DeclaredTime, out var linkMinutes))
            return PickSlotDay(fromLocal, linkMinutes, dir);

        // ② 源帖自己声明了时段 → 相邻棒的槽位 = 本棒槽位 ± 1 小时（窗口吸收发布延迟）
        if (TryParseHourMinute(from.DeclaredTime, out var selfMinutes))
            return PickSlotDay(fromLocal, selfMinutes, dir).AddMinutes(IsForward(dir) ? 60 : -60);

        // ③ 兜底：源帖发布时间当槽位估计，同样按槽位间隔推一格
        return fromLocal.AddMinutes(IsForward(dir) ? 60 : -60);
    }

    /// <summary>把「HH:mm」安放到离 <paramref name="baseLocal"/> 最近的某一天（处理跨午夜轮次）。</summary>
    private static DateTime PickSlotDay(DateTime baseLocal, int minutes, WalkDirection dir)
    {
        var day = baseLocal.Date;
        var best = day.AddMinutes(minutes);
        var bestDist = Math.Abs((best - baseLocal).Ticks);

        foreach (var offset in new[] { -1, 1 })
        {
            var cand = day.AddDays(offset).AddMinutes(minutes);
            var dist = Math.Abs((cand - baseLocal).Ticks);
            var tiePreferred = dist == bestDist && ((IsForward(dir) && offset == 1) || (!IsForward(dir) && offset == -1));
            if (dist < bestDist || tiePreferred)
            {
                best = cand;
                bestDist = dist;
            }
        }
        return best;
    }

    private static bool IsForward(WalkDirection dir) => dir is WalkDirection.Forward or WalkDirection.Both;

    /// <summary>解析 HH:mm / H：mm；24 小时制跨日活动允许 24..47 点（32h 接力的「26:00」）。</summary>
    internal static bool TryParseHourMinute(string? raw, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = TextNormalizer.Normalize(raw).Trim().Replace('：', ':');
        var parts = s.Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0].Trim(), out var h) || !int.TryParse(parts[1].Trim(), out var m)) return false;
        if (h is < 0 or > 47 || m is < 0 or > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    // ------------------------------------------------------------------ 候选排序

    /// <summary>
    /// 行首的「HH:MM」时段（允许前导空格/全角空格；小时 0..24）。
    /// 只认行首：正文里随口提到的时间（「我写到 23:00 才睡」）不能当棒位声明。
    /// </summary>
    internal static bool TrySlotAtLineStart(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim(' ', '\t', '\u3000', '\r');
            if (line.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{1,2})[:：](\d{2})(?!\d)");
            if (!m.Success) return false; // 只看第一行有内容的行
            if (!int.TryParse(m.Groups[1].Value, out var h) || !int.TryParse(m.Groups[2].Value, out var mi)) return false;
            if (h is < 0 or > 24 || mi is < 0 or > 59) return false;
            normalized = $"{h:00}:{mi:00}";
            return true;
        }
        return false;
    }

    /// <summary>
    /// 候选（还没读正文）的排序分：越可能「就是那一棒」越靠前 —— 读详情最贵，要先读对的。
    /// 实测依据：同一作者同一时间段常见两帖（文章版 + 动态版；21:30 动态 + 21:35 视频），
    /// 人工整理时选的是「正文/首行声明了那个时段、非专栏」的那张。
    /// </summary>
    public static int CandidateScore(PostCandidate cand, DateTime? slot, WalkDirection dir)
    {
        var score = 0;

        var hasSlot = TrySlotAtLineStart(cand.Title, out var candSlot);
        if (hasSlot && slot is not null && TryParseHourMinute(candSlot, out var cm))
        {
            var expected = PickSlotDay(slot.Value, cm, dir);
            var diff = Math.Abs((expected - slot.Value).TotalMinutes);
            score += diff <= 30 ? 60 : diff <= 90 ? 20 : 0;
        }
        else if (hasSlot)
        {
            score += 15; // 至少声明了时段（活动通常有），比什么都没有的强
        }

        if (cand.Title.Contains('棒')) score += 15;
        if (cand.Title.Contains("上一棒") || cand.Title.Contains("下一棒")) score += 15;

        if (cand.PubTs > 0 && slot is DateTime anchor)
        {
            var pub = DateTimeOffset.FromUnixTimeSeconds(cand.PubTs).ToLocalTime().DateTime;
            var mins = Math.Abs((pub - anchor).TotalMinutes);
            score += mins <= 90 ? 20 : mins <= 180 ? 5 : 0;
        }

        // 专栏（文章）与动态常常是同一棒的两种稿；人工整理选动态，所以文章只作兜底
        if (cand.Method == FetchMethod.ArticleSearch) score -= 15;
        return score;
    }

    /// <summary>
    /// 读过正文之后的排序分：在通过验收的候选里挑「最像那一棒」的那张。
    /// 与 <see cref="CandidateScore"/> 同一套价值取向（声明时段 &gt; 非文章 &gt; 有上下棒 &gt; 有正文），
    /// 只是因为拿到了 <see cref="PostFacts"/>，判定可以更硬。
    /// </summary>
    public static int RankScore(PostFacts facts, MatchOutcome outcome, DateTime? slot, WalkDirection dir)
    {
        var score = (int)Math.Round(outcome.Confidence * 10, MidpointRounding.AwayFromZero);

        if (outcome.MatchedBy.Contains("声明时段", StringComparison.Ordinal)) score += 60;

        if (slot is DateTime anchor && TryParseHourMinute(facts.DeclaredTime, out var mins))
        {
            var declared = PickSlotDay(anchor, mins, dir);
            var diff = Math.Abs((declared - anchor).TotalMinutes);
            score += diff <= 30 ? 30 : diff <= 60 ? 20 : 0;
        }

        var isArticle = facts.WorkKindLabel is not null && facts.WorkKindLabel.Contains("文章", StringComparison.Ordinal);
        if (!isArticle) score += 14;

        if (facts.Prevs.Count + facts.Nexts.Count > 0) score += 8;
        if (facts.BodyText.Length >= 10) score += 6;

        if (outcome.DiffMinutes is { } d) score -= Math.Min(Math.Abs(d), 1000) / 20;
        return score;
    }

    // ------------------------------------------------------------------ 弱证据

    private static (bool Has, string Description) FindWeakEvidence(PostFacts from, NeighborLink link, PostFacts candidate, RelayQuery query)
    {
        var why = new List<string>(4);

        if (candidate.HasRelayFeature) why.Add("候选自身带接力特征（棒号/时段/上下棒）");

        var expectedBar = ParseBar(link.BarRaw);
        if (expectedBar is { } eb && candidate.BarNumber == eb)
            why.Add($"棒号辅助命中（第 {eb} 棒，仅辅助判据）");

        if (candidate.IsEx) why.Add("候选带显式 EX 标记");

        // 活动名一致只能当"补充说明"，不能单独当证据：同一活动里几乎每个帖子都带活动名，
        // 拿它当弱证据会把"同一活动的任意一帖"都收成邻居，精度直接崩掉。
        var decisive = why.Count > 0;
        var relay = query.RelayName ?? from.RelayName;
        if (decisive && relay is not null && candidate.RelayName is { } crn && TextNormalizer.SameName(crn, relay))
            why.Add($"活动名一致（{relay}）");

        return (decisive, string.Join("；", why));
    }

    // ------------------------------------------------------------------ 小工具

    /// <summary>解析棒号原文（"第 7 棒"/"07"/"七棒"）。小数棒号不采纳（用户规则）。</summary>
    public static int? ParseBar(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = TextNormalizer.Normalize(raw).Trim()
            .Replace("第", string.Empty)
            .Replace("棒", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("#", string.Empty);
        if (s.Length == 0) return null;
        if (s.Contains('.') || s.Contains('．')) return null; // 小数棒号不采纳
        var n = TextNormalizer.ChineseToInt(s);
        return n is > 0 and <= 999 ? n : null;
    }
}
