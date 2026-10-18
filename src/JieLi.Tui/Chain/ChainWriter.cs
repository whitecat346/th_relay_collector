#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「chain」
// 只允许改本文件与 Chain/MatchVerifier.cs / Chain/ChainWalker.cs。
//
// 职责：把遍历结果落盘。**下文签名已冻结，Program.cs 直接调用，勿改。**
// 产出（都在 work/<slug>/ 下）：
//   chain.json    完整 ChainResult（含 audit）
//   review.jsonl  人工确认条目（复用 Model/ReviewItem.cs + ReviewStore.Save，不要另造格式）
//   report.md     只读报告：链条总览、来源/方向/置信、缺口清单（含可直接复制的 --add-link 模板）、
//                 推算棒号标注、审计告警、以及「本次使用了登录态」提示（如果用了 cookie）
// 棒数规则（Q12）：
//   声明值优先 → 推算值（report 里必须标"推算"）→ 皆无才 N/A；
//   起点 0/1 只从活动自身声明取，全无则默认 1 并标"起点默认1，请核对"；
//   小数棒号不采纳；EX 只认显式标记（EX/额外棒/彩蛋棒）；输出按时间排序。
// 缺口清单（Q18）：每条缺口给出 FromPostUrl + Direction + ExpectedName/ExpectedBar，
// 并生成形如 `--add-link <url>` 的可复制命令。
// ===========================================================================

/// <summary>结果落盘。</summary>
public static class ChainWriter
{
    /// <summary>写出 chain.json / review.jsonl / report.md，返回 work 目录路径。</summary>
    public static string Write(
        string workRoot,
        string slug,
        ChainResult result,
        RelayQuery query,
        string? manualDate,
        Action<string>? log)
        => throw new ChainFetchException("ChainWriter.Write 尚未实现（Agent「chain」）。");
}
