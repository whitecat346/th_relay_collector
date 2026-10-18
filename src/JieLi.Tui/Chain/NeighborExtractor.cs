#nullable enable
namespace JieLi.Tui.Chain;

// ===========================================================================
// 文件所有者：Agent「reader」
// 目标：从一段帖子文本（正文/评论/简介合并后）里抽出「上一棒 / 下一棒 / 棒号 / 时段 / 末棒标记」。
// 只允许改本文件与 Chain/PostReader.cs。
//
// 硬性要求（来自 m01437 用户指正 + Q3）：
//   1. 接力是「@ 下一个人的名字」驱动，**不是**「下一个人的链接」——定时发布时链接还不存在，
//      所以从 NeighborLink 里只能拿 Name/Uid，绝不要指望 RawText 里有可用链接。
//   2. 末棒显式标记（最后一棒/完结/收尾/收官/终棒 且指自己）优先于任何「下一棒」文字。
//   3. 棒号低可信：接力学里错标漏标很常见，抽不到就留空，不要猜。
//   4. 视频的棒信息常在评论里（简介只给棒数），所以输入是 MergedText 而不是 BodyText。
// ===========================================================================

/// <summary>把帖子文本解析成邻居与棒事实（纯函数，不发请求）。</summary>
public static class NeighborExtractor
{
    /// <summary>
    /// 就地填充 <paramref name="facts"/> 的 Prevs / Nexts / LastMark / FirstMark /
    /// BarRaw / BarNumber / DeclaredTime / IsEx / SlotTimes / Mentions 等字段。
    /// </summary>
    public static void Extract(PostFacts facts)
    {
        // 尚未实现（由 Agent「reader」填充）。
    }
}
