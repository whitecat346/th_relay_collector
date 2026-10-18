#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」
// 只允许改本文件与 Chain/MatchVerifier.cs / Chain/ChainWalker.cs / Chain/ChainWriter.cs。
//
// 职责：遍历器 + 落盘的**离线**自检用例（`jieli --selftest-chain` 会在链接嗅探用例之后调用它）。
// 硬性要求：
//   * 不联网：用假的 IPostReader / IUserLocator 驱动 ChainWalker（接口在 ChainModels.cs，
//     ChainHttp 可以构造但不发请求）。
//   * 每条用例都必须能独立失败并打印原因，不要吞异常。
//   * 至少覆盖：首棒无上一棒、末棒标记终止、两向相遇即停、连续 2 次失败停方向、
//     一人多棒（同 uid 两个 postId 都要在链上）、补充链接优先、方向冲突先到者胜。
// ===========================================================================

/// <summary>自检结果计数。</summary>
public sealed class ChainSelfTestResult
{
    public int Passed { get; set; }
    public int Failed { get; set; }
}

/// <summary>遍历器与落盘的离线自检。</summary>
public static class ChainSelfTest
{
    /// <summary>跑全部用例；返回通过/失败数。</summary>
    public static ChainSelfTestResult Run(Action<string> log)
    {
        var result = new ChainSelfTestResult();
        log("  · 遍历器用例尚未实现（Agent「chain」）。");
        return result;
    }
}
