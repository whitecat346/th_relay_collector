#nullable enable
using JieLi.Tui.Data;
using JieLi.Tui.Model;

namespace JieLi.Tui.Tui;

/// <summary>work/ 下已采集的话题。</summary>
public sealed class WorkTopic
{
    public long TopicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int CardCount { get; init; }
    public int DynamicsReported { get; init; }
    public bool HasReview { get; init; }
}

/// <summary>扫描 relay-tool/work/ 下已采集的话题（离线，不联网）。</summary>
public static class WorkScanner
{
    public static List<WorkTopic> Scan(string workRoot)
    {
        var list = new List<WorkTopic>();
        if (!Directory.Exists(workRoot)) return list;

        foreach (var dir in Directory.EnumerateDirectories(workRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (!long.TryParse(name, out var id)) continue;

            var manifest = TopicCollector.LoadManifest(workRoot, id);
            list.Add(new WorkTopic
            {
                TopicId = id,
                Name = manifest?.Name ?? "(未采集完成)",
                CardCount = manifest?.FetchedCards ?? 0,
                DynamicsReported = manifest?.DynamicsReported ?? 0,
                HasReview = ReviewStore.Exists(workRoot, id),
            });
        }
        return list;
    }
}
