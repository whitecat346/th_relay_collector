#nullable enable
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
//   * 负缓存不得阻断人工补充（--add-link）
// 文件格式自己定，但必须：UTF-8、可读、向后兼容（缺字段时有默认值）、损坏时降级为"空缓存"并警告而不是崩。
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
}

/// <summary>work/users.json 缓存。</summary>
public sealed class UserCache
{
    public const int RecheckDays = 7;

    public UserCache(string path) => Path = path;

    public string Path { get; }
    public List<UserMemory> Items { get; } = new();
    public bool Loaded { get; private set; }

    /// <summary>读文件；不存在/损坏时留空并记警告。</summary>
    public void Load(Action<string>? log)
        => throw new ChainFetchException("UserCache.Load 尚未实现（Agent「locator」）。");

    /// <summary>保存（UTF-8，缩进）。</summary>
    public void Save(Action<string>? log)
        => throw new ChainFetchException("UserCache.Save 尚未实现（Agent「locator」）。");

    /// <summary>查记忆：命中返回记录；负缓存未过期也返回（由调用方判断是否重试）。</summary>
    public UserMemory? Find(long uid, string? relay)
        => throw new ChainFetchException("UserCache.Find 尚未实现（Agent「locator」）。");

    /// <summary>写入/更新一条记忆。</summary>
    public void Remember(long uid, string name, string? relay, bool hit, FetchMethod method, IEnumerable<string>? posts = null, string? note = null)
        => throw new ChainFetchException("UserCache.Remember 尚未实现（Agent「locator」）。");
}
