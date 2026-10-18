#nullable enable
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「locator」
// 只允许改本文件与 Chain/SearchClient.cs / Chain/UserResolver.cs / Chain/SpaceFinder.cs。
//
// 职责：work/users.json 的读写 —— 全局用户/帖子记忆库（Q4）。
//   * 正负都缓存；负缓存带 lastChecked + 命中方法名（FetchMethod）
//   * 同一 (uid, relay) 运行内复用、跨运行复用；负缓存 7 天后允许重试
//   * 负缓存不得阻断人工补充（--add-link 直接指定，不经过本缓存）
// 文件格式：JSON 数组（每条一个 UserMemory），UTF-8 无 BOM、带缩进、中文不转义。
//   * 缺字段有默认值（System.Text.Json 自动处理）
//   * 损坏/不存在 → 空缓存 + 警告，绝不抛：缓存是"省请求"的优化，坏了不能挡住归档
// ===========================================================================

/// <summary>一条用户记忆。</summary>
public sealed class UserMemory
{
    [JsonPropertyName("uid")] public long Uid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("relay")] public string? Relay { get; set; }
    /// <summary>已确认属于该接力的帖子（opus id / BV 号）。</summary>
    [JsonPropertyName("posts")] public List<string> Posts { get; set; } = new();
    /// <summary>是否确认参加过该接力；false 表示负缓存。</summary>
    [JsonPropertyName("hit")] public bool Hit { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("last_checked")] public DateTime LastChecked { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("note")] public string? Note { get; set; }

    public override string ToString() => $"{Uid}/{Name}/{Relay ?? "-"} hit={Hit} posts={Posts.Count}";
}

/// <summary>work/users.json 缓存。</summary>
public sealed class UserCache
{
    /// <summary>负缓存重试间隔：7 天内不再为同一个 (uid, relay) 白跑请求。</summary>
    public const int RecheckDays = 7;

    /// <summary>
    /// 写文件用：缩进便于人肉查看/手改；UnsafeRelaxedJsonEscaping 让中文昵称保持原样，
    /// 否则会被序列化成 \uXXXX，工作目录里的 users.json 就没法看了。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private bool _dirty;

    public UserCache(string path) => Path = path;

    public string Path { get; }
    public List<UserMemory> Items { get; } = new();
    public bool Loaded { get; private set; }

    /// <summary>Load 之后是否有过修改（供调用方决定要不要 Save，避免每轮都重写文件）。</summary>
    public bool IsDirty => _dirty;

    /// <summary>今天之前 RecheckDays 天的时刻；last_checked 早于它就允许重查（正/负缓存都适用）。</summary>
    public static DateTime StaleBefore => DateTime.UtcNow.AddDays(-RecheckDays);

    /// <summary>读文件；不存在/损坏时留空并记警告，不抛。</summary>
    public void Load(Action<string>? log)
    {
        Items.Clear();
        _dirty = false;
        Loaded = false;

        try
        {
            if (!File.Exists(Path))
            {
                Loaded = true;
                log?.Invoke($"[缓存] 用户记忆不存在（{Path}），按空缓存开始");
                return;
            }

            var text = File.ReadAllText(Path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                Loaded = true;
                log?.Invoke($"[缓存] ⚠ 用户记忆文件为空（{Path}），按空缓存继续");
                return;
            }

            var items = JsonSerializer.Deserialize<List<UserMemory>>(text, ReadOptions);
            if (items is null)
            {
                Loaded = true;
                log?.Invoke($"[缓存] ⚠ 用户记忆内容不是数组（{Path}），按空缓存继续");
                return;
            }

            // 手改/合并过的文件可能带重复 (uid, relay)：只留 last_checked 最新的那条。
            foreach (var group in items.Where(i => i is not null).GroupBy(i => (i.Uid, NormalizeRelay(i.Relay))))
                Items.Add(group.OrderByDescending(i => i.LastChecked).First());

            Loaded = true;
            log?.Invoke($"[缓存] 用户记忆 {Path}：{Items.Count} 条");
        }
        catch (Exception ex)
        {
            Items.Clear();
            Loaded = true;
            log?.Invoke($"[缓存] ⚠ 用户记忆损坏（{Path}），按空缓存继续：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>保存（UTF-8 无 BOM、缩进）。先写 .tmp 再原子替换，避免中途崩溃留下半个 JSON。</summary>
    public void Save(Action<string>? log)
    {
        var tmp = Path + ".tmp";
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Items, WriteOptions);
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // File.Move(overwrite: true) 在同一卷上是原子替换；比"先删再写"安全得多。
            File.Move(tmp, Path, overwrite: true);
            _dirty = false;
            log?.Invoke($"[缓存] 用户记忆已保存：{Path}（{Items.Count} 条）");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[缓存] ⚠ 用户记忆保存失败（{Path}）：{ex.GetType().Name}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清临时文件失败不值得再报 */ }
        }
    }

    /// <summary>查记忆：命中返回记录；负缓存未过期也返回（由调用方判断是否重试）。</summary>
    public UserMemory? Find(long uid, string? relay)
    {
        if (uid <= 0) return null;
        var key = NormalizeRelay(relay);
        UserMemory? best = null;
        foreach (var item in Items)
        {
            if (item.Uid != uid || NormalizeRelay(item.Relay) != key) continue;
            if (best is null || item.LastChecked > best.LastChecked) best = item;
        }
        return best;
    }

    /// <summary>判断一条记忆是否过期：7 天内直接复用，过期后允许重新查（负缓存也一样）。</summary>
    public bool IsStale(UserMemory memory)
    {
        if (memory is null) return true;
        var last = memory.LastChecked;
        // 手改过的文件可能没带时区（Kind=Unspecified），按本地时间理解再转 UTC。
        if (last.Kind == DateTimeKind.Unspecified) last = DateTime.SpecifyKind(last, DateTimeKind.Local);
        return last.ToUniversalTime() < StaleBefore;
    }

    /// <summary>写入/更新一条记忆。</summary>
    public void Remember(long uid, string name, string? relay, bool hit, FetchMethod method, IEnumerable<string>? posts = null, string? note = null)
    {
        if (uid <= 0) return;

        var key = NormalizeRelay(relay);
        var memory = Items.FirstOrDefault(i => i.Uid == uid && NormalizeRelay(i.Relay) == key);
        if (memory is null)
        {
            memory = new UserMemory
            {
                Uid = uid,
                Relay = string.IsNullOrWhiteSpace(relay) ? null : relay.Trim(),
            };
            Items.Add(memory);
        }

        // name 传 "?" 这类占位（--probe-space 就是这样）时不要覆盖已知昵称。
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != "?") memory.Name = name.Trim();
        memory.Hit = hit;
        memory.Method = method.ToString();
        memory.LastChecked = DateTime.UtcNow;

        if (posts is not null)
        {
            memory.Posts = posts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (note is not null) memory.Note = note;

        _dirty = true;
    }

    /// <summary>
    /// 直接落盘（等价于 Save(log)）。给"查完顺手记住"的调用方省一次判空。
    /// </summary>
    public void FlushIfDirty(Action<string>? log)
    {
        if (_dirty) Save(log);
    }

    /// <summary>relay 归一：null/空串/全空白视为"未指定"，比较时不做大小写/空白区分。</summary>
    internal static string NormalizeRelay(string? relay)
        => string.IsNullOrWhiteSpace(relay) ? "" : relay.Trim();
}
