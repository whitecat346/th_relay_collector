#nullable enable
using System.Globalization;
using JieLi.Tui.Model;

namespace JieLi.Tui.Data;

/// <summary>一次「按届过滤」的结果。</summary>
public sealed class YearSelection
{
    /// <summary>保留下来的卡片（同一届）。</summary>
    public List<TopicCard> Kept { get; } = new();
    /// <summary>被排除掉的卡片（其它年份/其它届）。</summary>
    public List<TopicCard> Dropped { get; } = new();
    /// <summary>被选中的那一届的活动年份。</summary>
    public int Year { get; set; }
    /// <summary>这一届的时间跨度（最早/最晚卡片）。</summary>
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    /// <summary>被排除的各届（年份 → 条数），用于日志。</summary>
    public List<(int Year, int Count)> DroppedEditions { get; } = new();
    /// <summary>人读的一句话说明（写进日志/报告）。</summary>
    public string Note { get; set; } = string.Empty;

    public bool Filtered => Dropped.Count > 0;
}

/// <summary>
/// 话题的「按届过滤」。
///
/// 为什么需要它：一个话题会被同一场年度接力**反复复用**（实测 topic 1101507 里
/// 同时装着 2023/2024/2025/2026 四届，共 88 条卡片）。整锅端会把四届混成一条链，
/// 棒号、时段、卡数全乱——必须只留用户要的那一届。
///
/// 怎样认「一届」：按发布时间排序后，相邻卡片间隔超过 <see cref="EditionGapDays"/> 天
/// 就切开（同一届通常连着发完，跨届至少隔一年）。选哪一届：
///   ① --date 给了具体日期 → 含那天的这一届；
///   ② --year 给了年份 → 该年最新的一届；
///   ③ 都没给 → 含**今年**的那一届；没有今年的话取最新的一届。
/// 这样默认行为是「看今年的这一届」，历史届要看就显式 --year/--date。
/// </summary>
public static class TopicYearFilter
{
    /// <summary>相邻卡片间隔超过这么多天就算「另一届」。</summary>
    public const int EditionGapDays = 45;

    public static YearSelection Select(
        IReadOnlyList<TopicCard> cards,
        int? yearOverride = null,
        DateTime? dateOverride = null,
        int? currentYear = null)
    {
        var selection = new YearSelection();
        if (cards.Count == 0) return selection;

        var dated = cards.Where(c => c.PubTs > 0)
                         .OrderBy(c => c.PubTs)
                         .ToList();
        if (dated.Count == 0)
        {
            // 一条发布时间都没有：不筛（宁可全留，也不误删）
            selection.Kept.AddRange(cards);
            selection.Note = "所有卡片都没有发布时间，跳过按届过滤";
            return selection;
        }

        // ---- ① 按时间切届 ----
        var editions = new List<List<TopicCard>>();
        var current = new List<TopicCard> { dated[0] };
        for (var i = 1; i < dated.Count; i++)
        {
            var prev = LocalTime(dated[i - 1].PubTs);
            var next = LocalTime(dated[i].PubTs);
            if ((next - prev).TotalDays > EditionGapDays)
            {
                editions.Add(current);
                current = new List<TopicCard>();
            }
            current.Add(dated[i]);
        }
        editions.Add(current);

        // ---- ② 选一届 ----
        List<TopicCard>? picked = null;
        if (dateOverride is { } day)
        {
            picked = editions.FirstOrDefault(e => e.Any(c => LocalTime(c.PubTs).Date == day.Date));
            if (picked is null)
                picked = editions.OrderBy(e => Math.Abs((LocalTime(e[0].PubTs).Date - day.Date).TotalDays)).First();
        }
        else if (yearOverride is { } year)
        {
            picked = editions.Where(e => e.Any(c => LocalTime(c.PubTs).Year == year))
                             .OrderByDescending(e => LocalTime(e[^1].PubTs))
                             .FirstOrDefault();
        }

        if (picked is null)
        {
            var thisYear = currentYear ?? DateTime.Now.Year;
            picked = editions.Where(e => e.Any(c => LocalTime(c.PubTs).Year == thisYear))
                             .OrderByDescending(e => LocalTime(e[^1].PubTs))
                             .FirstOrDefault()
                     ?? editions[^1];
        }

        var pickedSet = new HashSet<TopicCard>(picked);
        foreach (var card in cards)
        {
            if (pickedSet.Contains(card)) selection.Kept.Add(card);
            else selection.Dropped.Add(card);
        }

        selection.Year = LocalTime(picked[0].PubTs).Year;
        selection.First = LocalTime(picked[0].PubTs);
        selection.Last = LocalTime(picked[^1].PubTs);

        foreach (var group in selection.Dropped
                     .Where(c => c.PubTs > 0)
                     .GroupBy(c => LocalTime(c.PubTs).Year)
                     .OrderBy(g => g.Key))
            selection.DroppedEditions.Add((group.Key, group.Count()));

        var droppedText = selection.DroppedEditions.Count == 0
            ? "没有其它届"
            : string.Join("/", selection.DroppedEditions.Select(d => $"{d.Year}×{d.Count}"));
        selection.Note =
            $"按届过滤：保留 {selection.Year} 这一届（{selection.First:yyyy-MM-dd}～{selection.Last:yyyy-MM-dd}，{selection.Kept.Count} 条）"
            + $"，排除其它届 {selection.Dropped.Count} 条（{droppedText}）";

        return selection;
    }

    private static DateTime LocalTime(long pubTs)
        => DateTimeOffset.FromUnixTimeSeconds(pubTs).ToLocalTime().DateTime;
}
