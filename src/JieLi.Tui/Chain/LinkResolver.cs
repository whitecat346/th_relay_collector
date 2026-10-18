#nullable enable
using System.Text.RegularExpressions;

namespace JieLi.Tui.Chain;

/// <summary>
/// 入口链接嗅探：把一个用户粘进来的东西解析成可读取的帖子定位。
///
/// 支持（全部实测过或与既有解析器同源）：
///   https://www.bilibili.com/opus/&lt;id&gt;                → opus（动态与专栏都走它）
///   https://t.bilibili.com/&lt;id&gt;                        → opus（三级子域）
///   https://m.bilibili.com/dynamic/&lt;id&gt;                 → opus（移动端）
///   https://www.bilibili.com/video/BV1xxxxxxxxx          → 视频
///   https://www.bilibili.com/video/av&lt;digits&gt;            → 视频
///   https://www.bilibili.com/v/topic/detail?topic_id=&lt;id&gt; → 话题
///   裸 BV 号 / 裸长数字 / topic_id=…                      → 按长度与前缀判定
///
/// 刻意**不**支持 read/cv&lt;id&gt;：cv 号与 opus id 不是同一套编号，
/// 需要先经过一次接口转换，属另一件事；遇到时给明确提示而不是猜。
/// </summary>
public static class LinkResolver
{
    /// <summary>解析入口链接。失败返回 null，并通过 <paramref name="reason"/> 说明原因。</summary>
    public static PostRef? Resolve(string raw, out string? reason)
    {
        reason = null;
        var s = (raw ?? string.Empty).Trim().Trim('"', '\'', '<', '>', ' ');
        if (s.Length == 0) { reason = "输入为空。"; return null; }

        // 用户可能粘的是整段分享文案（"【标题】 https://b23.tv/xxx"），先抽出第一个 http 链接
        var urlMatch = Regex.Match(s, @"https?://[^\s，。、）)】]+", RegexOptions.IgnoreCase);
        if (urlMatch.Success) s = urlMatch.Value;

        // 短链（b23.tv）需要一次网络跳转才能拿到真实地址 —— 明确告知，不做静默猜测
        if (Regex.IsMatch(s, @"(?:b23\.tv|bili2233\.cn)/", RegexOptions.IgnoreCase))
        {
            reason = "这是短链（b23.tv），需要先解析跳转。请在浏览器里打开一次，把跳转后的 www.bilibili.com 链接粘回来。";
            return null;
        }

        // 话题
        var topic = Regex.Match(s, @"topic_id=(\d{4,})", RegexOptions.IgnoreCase);
        if (topic.Success) return PostRef.Topic(topic.Groups[1].Value);
        var topicPath = Regex.Match(s, @"/v/topic/(?:detail|name)/[^/]*/?(\d{4,})", RegexOptions.IgnoreCase);
        if (topicPath.Success) return PostRef.Topic(topicPath.Groups[1].Value);

        // 视频
        var bv = Regex.Match(s, @"(BV[0-9A-Za-z]{10})");
        if (bv.Success) return PostRef.Video(bv.Groups[1].Value);
        var av = Regex.Match(s, @"/video/av(\d{4,})", RegexOptions.IgnoreCase);
        if (av.Success) return PostRef.Video("av" + av.Groups[1].Value);

        // opus / 动态（含 t.bilibili.com 与 m.bilibili.com/dynamic）
        var opus = Regex.Match(s, @"/opus/(\d{6,})", RegexOptions.IgnoreCase);
        if (opus.Success) return PostRef.Opus(opus.Groups[1].Value);
        var dyn = Regex.Match(s, @"(?:t\.bilibili\.com|/dynamic)/(\d{6,})", RegexOptions.IgnoreCase);
        if (dyn.Success) return PostRef.Opus(dyn.Groups[1].Value);

        // read/cv：编号体系不同，不猜
        if (Regex.IsMatch(s, @"/read/cv\d+", RegexOptions.IgnoreCase))
        {
            reason = "专栏的 read/cv 编号与 opus id 不是同一套编号。请在浏览器打开该专栏，把地址栏换成 opus 形式的链接再粘回来。";
            return null;
        }

        // 裸串：纯数字 / 裸链接无路径
        var digits = Regex.Match(s, @"^(\d{4,})$");
        if (digits.Success)
        {
            var n = digits.Groups[1].Value;
            // 实测 opus id 是 19 位左右的雪花号；话题 id 是 7 位左右
            if (n.Length >= 15) return PostRef.Opus(n);
            if (n.Length <= 9) return PostRef.Topic(n);
            reason = $"「{n}」既不像 opus id（19 位）也不像话题 id（7 位）。请直接粘完整链接。";
            return null;
        }

        reason = $"无法识别的输入：「{Truncate(s, 80)}」。支持的形态见 jieli --help 的「入口链接」一节。";
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
