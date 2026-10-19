#nullable enable
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>时段网格里的一格 = 一个棒位。</summary>
public sealed class SlotItem
{
    /// <summary>从活动日 00:00 起算的分钟数（24:00 → 1440，次日 00:30 → 1470）。</summary>
    public int Minutes { get; init; }
    /// <summary>棒号（1-based，按槽位时间升序）。</summary>
    public int BarNumber { get; init; }
    /// <summary>时段的展示文本（"00:00" … "24:00"）。</summary>
    public string Time { get; init; } = string.Empty;
    /// <summary>占住这一格的卡片。</summary>
    public ParsedBar? Owner { get; set; }
}

/// <summary>一次「时段 = 棒位」推断的结果。</summary>
public sealed class SlotGridPlan
{
    public bool Ok { get; set; }
    public DateOnly Day { get; set; }
    public List<SlotItem> Slots { get; } = new();
    public int AnchorHits { get; set; }
    public int AnchorTotal { get; set; }
    public string Confidence { get; set; } = "低";
    public List<string> Warnings { get; } = new();

    public string DayText => Day.ToString("yyyy-MM-dd");

    public string Describe()
        => Ok
            ? $"时段网格（{DayText}，{Slots.Count} 个槽位 {Slots[0].Time}–{Slots[^1].Time}，置信度{Confidence}"
              + (AnchorTotal > 0 ? $"，正文声明的第N棒 {AnchorHits}/{AnchorTotal} 与时段对得上" : "，无声明棒号可校准")
              + ")"
            : $"时段网格未建立（{DayText}）";
}

/// <summary>
/// 「时段制」接力的棒号推断。
///
/// 为什么需要它：不少接力的正文只写「24:00 @拒绝神绮99次」「17:00 @茶猫祈祷中」——
/// 写的是**时段**，不是「第N棒」。此时正文的「第N棒 + 总结名册」都不存在，
/// 全部卡片都会变成「无法归属」（实测 topic 1349127：43/46 条）。但时段本身就是棒位：
/// 活动从 00:00 起排满一天，槽位顺序就是棒序。
///
/// 做法（数据驱动，不硬编码任何活动）：
///   ① 活动日 = 卡片发布日期的众数（预热帖、次日 00:00 的收尾帖自动归位）；
///   ② 每个卡片取一个时段：正文声明的时段优先，没声明就用发布时间（必须落在整点/半点）；
///   ③ 时段去重排序 → 第 i 格就是第 i 棒；
///   ④ 用正文里**确实写了**「第N棒」的卡片反过来校准（第4棒 = 03:00、第7棒 = 06:00 都吻合 → 高置信）；
///   ⑤ 校准不上就只给低置信、不覆盖任何已声明棒号。
///
/// 它绝不覆盖正文里已写明的棒号（那是第一手证据）。
/// </summary>
public static class SlotGrid
{
    /// <summary>网格只认整点/半点槽位——实测整点以外的发布（10:08、20:56、22:08）都是插曲/约稿/预告。</summary>
    private const int SlotUnitMinutes = 30;

    /// <summary>次日凌晨多少分钟以内仍算活动日的「24:00 之后」。</summary>
    private const int NextDayGraceMinutes = 180;

    public static SlotGridPlan Infer(IReadOnlyList<ParsedBar> bars, string? topicTag = null)
    {
        var plan = new SlotGridPlan { Ok = false };

        var members = bars.Where(b => !b.IsSummary).ToList();
        if (members.Count == 0) return plan;

        // ---- ① 活动日 = 发布日期的众数（并列取较早的一天） ----
        var dayCount = new Dictionary<DateOnly, int>();
        foreach (var b in members)
        {
            if (b.PubTs <= 0) continue;
            var d = DateOnly.FromDateTime(LocalTime(b.PubTs));
            dayCount[d] = dayCount.GetValueOrDefault(d) + 1;
        }
        if (dayCount.Count == 0) return plan;
        plan.Day = dayCount.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

        // ---- ② 每张卡片定位到一个时段 ----
        var located = new List<(ParsedBar Bar, int Minutes, bool Declared)>();
        foreach (var b in members)
        {
            if (!TrySlotOf(b, plan.Day, out var minutes, out var declared)) continue;
            // 网格只收整点/半点；正文声明的时段也要求落在整点/半点（否则是"17:27"这类笔误或插曲）
            if (minutes % SlotUnitMinutes != 0) continue;
            located.Add((b, minutes, declared));
        }
        if (located.Count == 0)
        {
            plan.Warnings.Add("所有卡片都定位不到整点/半点时段，无法建立时段网格");
            return plan;
        }

        // ---- ③ 去重排序：一格 = 一棒 ----
        foreach (var minutes in located.Select(x => x.Minutes).Distinct().OrderBy(x => x))
        {
            plan.Slots.Add(new SlotItem
            {
                Minutes = minutes,
                BarNumber = plan.Slots.Count + 1,
                Time = FormatMinutes(minutes),
            });
        }

        // ---- ④ 用「正文里真的写了第N棒」的卡片校准 ----
        var anchors = new List<string>();
        foreach (var b in members)
        {
            if (b.BarNumber is not { } num || b.IsEx) continue;
            if (!TrySlotOf(b, plan.Day, out var minutes, out _)) continue;
            var slot = plan.Slots.FirstOrDefault(s => s.Minutes == minutes);
            if (slot is null) continue;
            plan.AnchorTotal++;
            if (slot.BarNumber == num)
            {
                plan.AnchorHits++;
            }
            else
            {
                anchors.Add($"第{num}棒（{b.PosterName}）实际落在 {slot.Time}，按槽位该是第{slot.BarNumber}棒"
                            + $"（差 {slot.BarNumber - num} 格，说明中间有未采集到的槽位）");
            }
        }

        if (plan.AnchorTotal > 0)
        {
            plan.Confidence = plan.AnchorHits == plan.AnchorTotal ? "高" : (plan.AnchorHits > 0 ? "中" : "低");
            plan.Warnings.AddRange(anchors);
        }
        else
        {
            plan.Confidence = plan.Slots.Count >= 3 ? "低" : "低";
            plan.Warnings.Add("没有任何卡片写「第N棒」，时段网格只能按顺序推棒号（低置信，请在 TUI 里核对）");
        }

        // ⑤ 校准不上（有冲突）时不自动赋号：宁可留 N/A，也不给出错位的棒号
        plan.Ok = plan.AnchorTotal == 0 || plan.AnchorHits == plan.AnchorTotal;
        if (!plan.Ok)
            plan.Warnings.Add("时段网格与正文声明的棒号对不上，本轮不按网格赋号（避免错位）");
        return plan;
    }

    /// <summary>把卡片落进网格：同名同槽位的重复投稿只留一条，其余标记为忽略。</summary>
    public static void Assign(IReadOnlyList<ParsedBar> bars, SlotGridPlan plan, ValidationResult result)
    {
        if (!plan.Ok || plan.Slots.Count == 0) return;

        var located = new List<(ParsedBar Bar, SlotItem Slot)>();
        foreach (var bar in bars)
        {
            if (bar.IsSummary || bar.IsDuplicate) continue;
            if (!TrySlotOf(bar, plan.Day, out var minutes, out _)) continue;
            var slot = plan.Slots.FirstOrDefault(s => s.Minutes == minutes);
            if (slot is null) continue;
            located.Add((bar, slot));
        }

        // 每格挑一条「最像本体」的卡：已声明棒号 > 有声明时段 > 发布时间早 > 正文长
        foreach (var group in located.GroupBy(x => x.Slot.Minutes).OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(x => x.Bar.BarNumber.HasValue)
                .ThenByDescending(x => x.Bar.DeclaredTime is { Length: > 0 })
                .ThenBy(x => x.Bar.PubTs)
                .ThenByDescending(x => x.Bar.MergedText.Length)
                .ToList();

            var primary = ordered[0];
            primary.Slot.Owner = primary.Bar;
            if (primary.Bar.BarNumber is { } existing)
            {
                // 正文已写棒号：只做对齐，不改写
                if (existing != primary.Slot.BarNumber && primary.Bar.MatchedStyle is null)
                    primary.Bar.Notes.Add($"时段网格的槽位是 {primary.Slot.Time}（按序应是第{primary.Slot.BarNumber}棒）");
            }
            else
            {
                primary.Bar.BarNumber = primary.Slot.BarNumber;
                primary.Bar.MatchedStyle ??= "时段网格";
                primary.Bar.DeclaredTime ??= primary.Slot.Time;
                primary.Bar.Notes.Add(
                    $"正文只写了时段，按「时段=棒位」推断为第{primary.Slot.BarNumber}棒"
                    + $"（{primary.Slot.Time}，置信度{plan.Confidence}）");
            }

            foreach (var dup in ordered.Skip(1))
            {
                dup.Bar.IsDuplicate = true;
                result.Ignored.Add(new IgnoredCard(
                    dup.Bar.CardId,
                    dup.Bar.ResolvedAuthor ?? dup.Bar.PosterName,
                    primary.Slot.Time,
                    $"与第{primary.Slot.BarNumber}棒同一时段 {primary.Slot.Time} 的重复投稿"
                    + $"（保留 @{primary.Bar.ResolvedAuthor ?? primary.Bar.PosterName} 的那条）"));
            }
        }

        // 网格外的卡片：活动日以外的预热/次日闲聊，或非棒位的插曲/约稿
        foreach (var bar in bars)
        {
            if (bar.IsSummary || bar.IsDuplicate || bar.BarNumber.HasValue || bar.IsEx) continue;
            if (!TrySlotOf(bar, plan.Day, out _, out _))
            {
                bar.Conflicts.Add($"不在活动日 {plan.DayText} 的时段网格上（发布 {LocalDateTimeText(bar.PubTs)}），疑似预热/番外，需人工确认");
                continue;
            }
            // 同一作者已有棒位 → 这条可能是插曲/约稿/bgm 之类的第二帖。
            // 但**带上一棒/下一棒标注的帖子一定是棒次**（它参与了链），绝不能排除，
            // 否则会把真棒位漏掉、让后面的棒号整体错位一格（实测 17:00 那棒就差点被吞掉）。
            var author = bar.ResolvedAuthor ?? bar.PosterName;
            var hasNeighborMarkers = bar.PrevName is { Length: > 0 } || bar.NextName is { Length: > 0 };
            // 「同一作者」按**创作者**或**发布者**任一命中都算：代发帖的发布者（主催）本来就会发很多棒，
            // 所以还要看创作者；反过来一个账号自己发的第二帖（哪怕这次是替人代发）也该按发布者认出来。
            var owner = plan.Slots.FirstOrDefault(s => s.Owner is not null
                                                      && (TextNormalizer.SameName(s.Owner.ResolvedAuthor ?? s.Owner.PosterName, author)
                                                          || TextNormalizer.SameName(s.Owner.PosterName, bar.PosterName)));
            if (owner is not null && !hasNeighborMarkers)
            {
                bar.IsDuplicate = true;
                result.Ignored.Add(new IgnoredCard(
                    bar.CardId, author, LocalTimeText(bar.PubTs),
                    $"同一作者 @{author} 已在第{owner.BarNumber}棒（{owner.Time}）有一帖，这条不在整点/半点槽位上，按插曲/约稿忽略"));
                continue;
            }
            bar.Conflicts.Add($"发布时刻不在整点/半点槽位上（{LocalTimeText(bar.PubTs)}），无法确定是棒次还是插曲，需人工确认");
        }
    }

    /// <summary>取卡片对应的槽位（供断言/复核用）。</summary>
    public static SlotItem? SlotOf(ParsedBar bar, SlotGridPlan plan)
        => TrySlotOf(bar, plan.Day, out var m, out _) ? plan.Slots.FirstOrDefault(s => s.Minutes == m) : null;

    /// <summary>卡片 → 时段分钟数。正文声明的时段优先，其次用发布时间（仅当天、或次日 0–3 点）。</summary>
    private static bool TrySlotOf(ParsedBar bar, DateOnly day, out int minutes, out bool declared)
    {
        minutes = 0;
        declared = false;

        // 声明的时段优先，但**仍要落在活动日里**——
        // 实测有「前一天发的预告贴」自称 15：30 @某人（topic 1349127 的 10-07 那条），
        // 若只看声明时段，它会凭空插进网格、把后面所有棒号推后一格。
        var declaredMinutes = 0;
        var hasDeclared = bar.DeclaredTime is { Length: > 0 } t && TryParseTime(t, out declaredMinutes);

        if (bar.PubTs <= 0)
        {
            // 没有发布时间时只能信声明时段
            if (hasDeclared) { minutes = declaredMinutes; declared = true; return true; }
            return false;
        }

        var local = LocalTime(bar.PubTs);
        var d = DateOnly.FromDateTime(local.Date);
        var m = local.Hour * 60 + local.Minute;

        bool sameDay = d == day;
        bool nextDayGrace = d == day.AddDays(1) && m <= NextDayGraceMinutes;

        if (hasDeclared && (sameDay || nextDayGrace))
        {
            // 次日凌晨的帖子若声明的是「24:00 之后」，按声明的时段算
            minutes = declaredMinutes;
            declared = true;
            return true;
        }

        if (sameDay) { minutes = m; return true; }
        if (nextDayGrace) { minutes = 24 * 60 + m; return true; }
        return false;
    }

    /// <summary>"24:00" 这类写法要能解析成 1440（TimeSpan 不接受 24 小时）。</summary>
    public static bool TryParseTime(string hhmm, out int minutes)
    {
        minutes = 0;
        var parts = (hhmm ?? string.Empty).Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h < 0 || h > 24 || m < 0 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    public static string FormatMinutes(int minutes)
    {
        if (minutes < 0) minutes = 0;
        var h = minutes / 60;
        var m = minutes % 60;
        return $"{h:00}:{m:00}";
    }

    private static DateTime LocalTime(long pubTs) => DateTimeOffset.FromUnixTimeSeconds(pubTs).ToLocalTime().DateTime;

    private static string LocalTimeText(long pubTs)
        => pubTs > 0 ? LocalTime(pubTs).ToString("HH:mm") : "?";

    private static string LocalDateTimeText(long pubTs)
        => pubTs > 0 ? LocalTime(pubTs).ToString("yyyy-MM-dd HH:mm") : "?";
}
