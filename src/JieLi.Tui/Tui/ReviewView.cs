#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using JieLi.Tui.Chain;
using JieLi.Tui.Data;
using JieLi.Tui.Model;
using JieLi.Tui.Text;

namespace JieLi.Tui.Tui;

/// <summary>
/// M4：采集结果的人工确认视图。
/// 这是设计阶段定下的唯一合法确认入口 —— 非交互入口只产出 review.jsonl 草稿，
/// 最终 CSV/md 必须经过这里确认。
///
/// 按键：
///   ↑/↓        选择条目          PgUp/PgDn  翻页        Home/End  首尾
///   Enter      确认本条          X          忽略本条（判定不是一根棒）
///   R          重开本条（撤销确认/忽略）
///   B          改棒号            T          改时段      U  改创作者
///   P          改代发者          N          写备注
///   D          在浏览器打开作品链接（跳转取证）
///   S          保存到 review.jsonl 并重跑断言
///   A          补录：粘贴一棒帖子链接（opus / 动态 / 视频），读成一条待确认记录
///   G          缺口视图（读 work/&lt;目录&gt;/chain.json 的 gaps，只读）
///   C          链条视图（只读，棒号/时段/创作者/来源/方向/置信/@反指）
///   I          只看待确认项（切换过滤）    Esc/Q  返回
/// </summary>
public sealed class ReviewView
{
    private enum Panel
    {
        None,
        Gaps,
        Chain,
    }

    private readonly ConsoleHost _c;
    private readonly string _workRoot;
    private readonly long _topicId;
    private readonly string _topicName;
    /// <summary>目录名。话题模式为 null（用 topicId）；单链接模式为 `opus_&lt;id&gt;`。</summary>
    private readonly string? _slug;

    private List<ReviewItem> _items;
    private int _index;
    private int _top;
    /// <summary>默认显示全部（按棒序通读更直观，问题项高亮）；按 I 可切换为"只看待确认"。</summary>
    private bool _onlyPending;
    private string? _toast;
    private bool _dirty;

    /// <summary>人工改动后重新计算的断言结果（立即反馈）。</summary>
    private (int Attributed, int Dups, List<int> Missing, int Unattributed) _assertions;

    // ---- 只读面板（G 缺口 / C 链条）：与确认列表互斥，打开期间不响应任何会改列表的键 ----
    private Panel _panel = Panel.None;
    private ChainView? _chain;
    private string? _panelError;

    public bool ShouldExit { get; private set; }

    public ReviewView(ConsoleHost c, string workRoot, long topicId, string topicName, string? slug = null)
    {
        _c = c;
        _workRoot = workRoot;
        _topicId = topicId;
        _topicName = topicName;
        _slug = slug;
        // 单链接模式的目录名是 opus_<id>，不能按数字 topicId 去读
        _items = slug is { Length: > 0 }
            ? ReviewStore.Load(workRoot, slug)
            : ReviewStore.Load(workRoot, topicId);

        SortItems();

        // 初始光标：只显示待确认项时落在第一个"未确认且未忽略"的条目上。
        // （早期版本用 FindIndex 对全体条目查找，在 onlyPending 过滤下会得到 -1，
        //   于是 Current 恒为 null，所有确认/忽略按键静默失效 —— 由 M4 自检发现。）
        var pending = _items.FindIndex(i => !i.Confirmed && !i.Ignored);
        _index = pending >= 0 ? pending : 0;
        RecomputeAssertions();
    }

    private static int TimeKeyOf(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm)) return int.MaxValue;
        var p = hhmm.Split(':');
        if (p.Length != 2) return int.MaxValue;
        return (int.TryParse(p[0], out var h) ? h : 0) * 60 + (int.TryParse(p[1], out var m) ? m : 0);
    }

    /// <summary>排序：按棒号升序（无棒号者按时段排到后面），便于按接力顺序通读。</summary>
    private void SortItems()
    {
        _items = _items
            .OrderBy(i => i.Bar.HasValue ? 0 : (i.IsEx ? 1 : 2))
            .ThenBy(i => i.Bar ?? int.MaxValue)
            .ThenBy(i => TimeKeyOf(i.DeclaredTime))
            .ToList();
    }

    private List<ReviewItem> Visible =>
        _onlyPending
            ? _items.Where(i => !i.Confirmed && !i.Ignored).ToList()
            : _items;

    private ReviewItem? Current
    {
        get
        {
            var v = Visible;
            return _index >= 0 && _index < v.Count ? v[_index] : null;
        }
    }

    public void Render(int width, int height)
    {
        // G / C 是只读面板：与确认列表互斥，Enter/X 等按键在面板里一律不生效。
        if (_panel != Panel.None)
        {
            RenderPanel(width, height);
            return;
        }

        var visible = Visible;
        if (_index >= visible.Count) _index = Math.Max(0, visible.Count - 1);

        // ---- 头部统计 ----
        int pending = _items.Count(i => !i.Confirmed && !i.Ignored);
        int confirmed = _items.Count(i => i.Confirmed);
        int ignored = _items.Count(i => i.Ignored);
        var head = new StringBuilder();
        head.Append(_c.Bold).Append("人工确认 · ").Append(_topicName).Append(_c.Reset);
        head.Append(_c.Dim).Append($"   topic {_topicId}").Append(_c.Reset);
        _c.Line(head.ToString());
        _c.Line($"{_c.Dim}待确认 {_c.Reset}{_c.Yellow}{pending}{_c.Reset}{_c.Dim}  "
                + $"已确认 {_c.Reset}{_c.Green}{confirmed}{_c.Reset}{_c.Dim}  "
                + $"已忽略 {_c.Reset}{ignored}{_c.Dim}  共 {_items.Count} 条"
                + (_onlyPending ? "  [只看待确认]" : "  [显示全部]")
                + (_dirty ? "   " + _c.Reset + _c.Yellow + "● 有未保存改动（按 S 保存）" + _c.Reset : "")
                + _c.Reset);
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);

        // ---- 断言回执 ----
        var a = _assertions;
        var assertLine = new StringBuilder();
        assertLine.Append(_c.Dim).Append("断言  ").Append(_c.Reset);
        assertLine.Append($"有棒号 {_c.Bold}{a.Attributed}{_c.Reset}");
        if (a.Dups > 0) assertLine.Append($"   {_c.Red}棒号重复 {a.Dups}{_c.Reset}");
        if (a.Missing.Count > 0) assertLine.Append($"   {_c.Red}缺棒 {a.Missing.Count}{_c.Reset}");
        else assertLine.Append($"   {_c.Green}无缺棒{_c.Reset}");
        if (a.Unattributed > 0) assertLine.Append($"   {_c.Yellow}未归属 {a.Unattributed}{_c.Reset}");
        _c.Line(assertLine.ToString());
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);

        // ---- 列表 ----
        int reserved = 8;
        int viewH = Math.Max(1, height - reserved);
        if (_index < _top) _top = _index;
        if (_index >= _top + viewH) _top = _index - viewH + 1;

        int wBar = 5, wTime = 6, wStatus = 10, wAuthor = 18;
        int wText = Math.Max(12, width - (wBar + wTime + wStatus + wAuthor + 8));

        _c.Line(_c.Bold + TextWidth.PadLeft("棒", wBar) + " " + TextWidth.Pad("时段", wTime) + " "
                + TextWidth.Pad("创作者", wAuthor) + " " + TextWidth.Pad("状态", wStatus) + " 依据" + _c.Reset);

        for (int i = _top; i < Math.Min(visible.Count, _top + viewH); i++)
        {
            var it = visible[i];
            bool sel = i == _index;
            var sb = new StringBuilder();
            if (sel) sb.Append(_c.Inverse);

            string bar = it.IsEx ? "EX" : (it.Bar?.ToString() ?? "?");
            var styleColor = it.Confirmed ? _c.Green : (it.NeedsAttention ? _c.Yellow : "");
            sb.Append(TextWidth.PadLeft(bar, wBar)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(it.DeclaredTime ?? "-", wTime), wTime)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(it.ResolvedAuthor ?? it.PosterName, wAuthor), wAuthor)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(it.StatusLabel, wStatus), wStatus)).Append(' ');
            sb.Append(TextWidth.Truncate(it.MatchedStyle ?? it.Conflicts.FirstOrDefault() ?? it.Notes.FirstOrDefault() ?? "", wText));
            if (sel) sb.Append(_c.Reset);
            _c.Line(sb.ToString());
        }

        // ---- 详情面板 ----
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);
        var cur = Current;
        if (cur is null)
        {
            _c.Line(_c.Green + "（没有待确认项）" + _c.Reset + _c.Dim + "  按 I 切换显示全部" + _c.Reset);
        }
        else
        {
            _c.Line($"{_c.Bold}第 {cur.Bar?.ToString() ?? "?"} 棒{_c.Reset}  {cur.DeclaredTime ?? "-"}  "
                    + $"{_c.Cyan}{cur.ResolvedAuthor ?? cur.PosterName}{_c.Reset}"
                    + (cur.IsProxy ? $"  {_c.Magenta}（代发：{cur.ProxyName}）{_c.Reset}" : ""));
            _c.Line($"{_c.Dim}发布者{_c.Reset} {cur.PosterName}  {_c.Dim}mid{_c.Reset} {cur.PosterMid}  "
                    + $"{_c.Dim}发布{_c.Reset} {cur.PubTimeText}"
                    + (cur.DeviationMinutes is { } d ? $"  {_c.Dim}偏差{_c.Reset} {(d >= 0 ? "+" : "")}{d}m" : ""));
            _c.Line($"{_c.Dim}作品{_c.Reset} {cur.OpusUrl}");
            if (cur.Conflicts.Count > 0)
                _c.Line(_c.Yellow + "⚠ " + string.Join("；", cur.Conflicts.Take(2)) + _c.Reset);
            else if (cur.Notes.Count > 0)
                _c.Line(_c.Dim + "· " + string.Join("；", cur.Notes.Take(2)) + _c.Reset);
            _c.Line(_c.Dim + "正文 " + _c.Reset + TextWidth.Truncate(
                cur.MergedText.Replace('\n', ' '), width - 6));
        }

        // ---- 页脚 ----
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);
        if (_toast is not null) _c.Line(_c.Yellow + _toast + _c.Reset);
        _c.Line(_c.Dim + "Enter 确认  X 忽略  R 撤销  B 棒号  T 时段  U 创作者  K 类型  P 代发者  N 备注  A 补录  G 缺口  C 链条  D 打开链接  S 保存  I 过滤  Esc 返回" + _c.Reset);
    }

    /// <summary>返回 true 表示需要重新渲染由调用方控制；这里直接处理全部按键。</summary>
    public void HandleKey(ConsoleKeyInfo key)
    {
        if (_panel != Panel.None)
        {
            HandlePanelKey(key);
            return;
        }

        var cur = Current;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                if (_dirty) Save();
                ShouldExit = true;
                return;
            case ConsoleKey.UpArrow: Move(-1); return;
            case ConsoleKey.DownArrow: Move(1); return;
            case ConsoleKey.PageUp: for (int i = 0; i < 10; i++) Move(-1); return;
            case ConsoleKey.PageDown: for (int i = 0; i < 10; i++) Move(1); return;
            case ConsoleKey.Home: _index = 0; return;
            case ConsoleKey.End: _index = Math.Max(0, Visible.Count - 1); return;
            case ConsoleKey.I:
                _onlyPending = !_onlyPending;
                _index = 0;
                _toast = _onlyPending ? "只显示待确认项" : "显示全部条目";
                return;
            case ConsoleKey.S:
                Save();
                return;
            case ConsoleKey.D:
                if (cur is not null) OpenInBrowser(cur.OpusUrl);
                return;
            // A/G/C 是新增的纯展示/补录入口：不进入 CSV 契约，也不改变上面任何既有键的行为。
            case ConsoleKey.A:
                AddByLink();
                return;
            case ConsoleKey.G:
                OpenPanel(Panel.Gaps);
                return;
            case ConsoleKey.C:
                OpenPanel(Panel.Chain);
                return;
        }

        if (cur is null) { _toast = "没有可操作的条目"; return; }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                cur.Confirmed = true;
                cur.Ignored = false;
                _dirty = true;
                _toast = $"已确认：第 {cur.Bar?.ToString() ?? "?"} 棒 {cur.ResolvedAuthor ?? cur.PosterName}";
                AdvanceIfFiltered();
                break;
            case ConsoleKey.X:
                cur.Ignored = true;
                cur.Confirmed = false;
                _dirty = true;
                _toast = "已忽略（判定不是一根棒）";
                AdvanceIfFiltered();
                break;
            case ConsoleKey.R:
                cur.Confirmed = false;
                cur.Ignored = false;
                _dirty = true;
                _toast = "已撤销本条状态";
                break;
            case ConsoleKey.B:
                EditField("棒号（留空=未知）", cur.Bar?.ToString() ?? "", v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) { cur.Bar = null; return "棒号已清空"; }
                    if (int.TryParse(v.Trim(), out var n)) { cur.Bar = n; return $"棒号改为 {n}"; }
                    return "无效数字，未修改";
                });
                break;
            case ConsoleKey.T:
                EditField("时段（如 20:30）", cur.DeclaredTime ?? "", v =>
                {
                    cur.DeclaredTime = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    return "时段已改为 " + (cur.DeclaredTime ?? "空");
                });
                break;
            case ConsoleKey.U:
                EditField("创作者昵称", cur.ResolvedAuthor ?? cur.PosterName, v =>
                {
                    cur.ResolvedAuthor = string.IsNullOrWhiteSpace(v) ? cur.PosterName : v.Trim();
                    return "创作者已改为 " + cur.ResolvedAuthor;
                });
                break;
            case ConsoleKey.P:
                EditField("代发者（留空=非代发）", cur.ProxyName ?? "", v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) { cur.IsProxy = false; cur.ProxyName = null; return "已标记为非代发"; }
                    cur.IsProxy = true;
                    cur.ProxyName = v.Trim();
                    return "代发者已改为 " + cur.ProxyName;
                });
                break;
            case ConsoleKey.N:
                EditField("备注", cur.ManualNote ?? "", v =>
                {
                    cur.ManualNote = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    return "备注已保存";
                });
                break;
            case ConsoleKey.K:
                EditField("作品类型（插画/文章/视频/MMD/手书/COS/漫画/音乐…）",
                          string.IsNullOrWhiteSpace(cur.TypeLabel) ? BarParser.InferWorkKind(cur.Type, cur.MergedText) : cur.TypeLabel,
                            v =>
                            {
                                cur.TypeLabel = string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim();
                                return "类型已改为 " + (cur.TypeLabel.Length > 0 ? cur.TypeLabel : "（自动）");
                            });
                break;
        }
    }

    // -----------------------------------------------------------------------
    // 新增三键：A 补录 / G 缺口 / C 链条。全部是"只读展示或补录"，
    // 不进 CSV/MD 契约；既有按键语义保持不变。
    // -----------------------------------------------------------------------

    /// <summary>当前 work 下的目录名。单链接模式是 opus_&lt;id&gt;；话题模式退回 topicId。</summary>
    private string ChainSlug => _slug is { Length: > 0 } ? _slug : _topicId.ToString();

    private void OpenPanel(Panel panel)
    {
        _panel = panel;
        _toast = null;
        try
        {
            // 每次打开都重新读：chain.json 可能在另一路命令里刚生成/更新。
            _chain = ChainView.TryLoad(_workRoot, ChainSlug, out var error);
            _panelError = error;
        }
        catch (Exception ex)
        {
            _chain = null;
            _panelError = "读取 chain.json 失败：" + ex.Message;
        }
    }

    private void HandlePanelKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.Q:
                _panel = Panel.None;
                _toast = null;
                return;
            case ConsoleKey.G:
                OpenPanel(Panel.Gaps);
                return;
            case ConsoleKey.C:
                OpenPanel(Panel.Chain);
                return;
            default:
                // 面板内只读：忽略其它按键，避免"按了 Enter 却改了列表"的意外。
                return;
        }
    }

    private void RenderPanel(int width, int height)
    {
        if (_chain is null)
        {
            var title = _panel == Panel.Gaps ? "缺口清单" : "链条视图";
            ChainView.RenderUnavailable(_c, title, _panelError,
                $"jieli --link <某一棒的帖子链接> [--date 2026-9-6] [--as {ChainSlug}]");
        }
        else if (_panel == Panel.Gaps)
        {
            _chain.RenderGaps(_c, width, height);
        }
        else
        {
            _chain.RenderChain(_c, width, height);
        }

        if (_toast is not null) _c.Line(_c.Yellow + _toast + _c.Reset);
    }

    /// <summary>
    /// A 键：粘链接补录。用 Console.ReadLine 读一行 —— 用户本来就是在"粘一行 URL"，
    /// 这比调用剪贴板 API 稳，也避免为一个按键引入 WinForms 依赖。
    /// 读不到/解析失败/重复都只提示，绝不改动当前列表。
    /// </summary>
    private void AddByLink()
    {
        _c.Restore();
        Console.WriteLine();
        Console.Write("粘贴一棒帖子的链接（opus / 动态 / 视频 BV·av）> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            _toast = "没有读到链接（剪贴板为空或输入被取消），列表未改动。";
            HideCursor();
            return;
        }

        var reference = LinkResolver.Resolve(input, out var reason);
        if (reference is null)
        {
            _toast = "补录失败：" + (reason ?? "无法识别该链接") + " 列表未改动。";
            HideCursor();
            return;
        }
        if (reference.Kind == PostKind.Topic)
        {
            _toast = "补录失败：这是话题链接，不是一根棒。请粘某一棒帖子的链接。列表未改动。";
            HideCursor();
            return;
        }
        if (FindDuplicate(reference.Id) is { } dup)
        {
            _toast = $"补录失败：第 {BarTextOf(dup)} 棒 {dup.ResolvedAuthor ?? dup.PosterName} 已在列表里，未重复添加。";
            HideCursor();
            return;
        }

        var logs = new List<string>();
        try
        {
            using var http = new ChainHttp();
            var reader = new ChainPostReader(http);
            var facts = reader.Read(reference, m => logs.Add(m), CancellationToken.None);
            if (facts is null)
            {
                _toast = "补录失败：读取器没有返回内容，列表未改动。";
                HideCursor();
                return;
            }

            var item = ToReviewItem(facts, reference);
            _items.Add(item);
            SortItems();
            _index = Visible.IndexOf(item);
            if (_index < 0)
            {
                // 当前正在"只看待确认"以外的过滤状态时，强制回到全部视图，保证新条目可见。
                _onlyPending = false;
                _index = Math.Max(0, _items.IndexOf(item));
            }
            RecomputeAssertions();
            _dirty = true;
            _toast = $"已补录：第 {BarTextOf(item)} 棒 {item.ResolvedAuthor ?? item.PosterName}"
                     + "（待确认；Enter 确认、S 保存）";
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            if (logs.Count > 0 && !detail.Contains(logs[^1], StringComparison.Ordinal))
                detail += "（" + logs[^1] + "）";
            _toast = "补录失败：" + detail + " 列表未改动。";
        }
        HideCursor();
    }

    private ReviewItem? FindDuplicate(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var it in _items)
        {
            if (string.Equals(it.CardId, id, StringComparison.OrdinalIgnoreCase)) return it;
            if (IdFromUrl(it.OpusUrl) is { } cardId
                && string.Equals(cardId, id, StringComparison.OrdinalIgnoreCase)) return it;
        }
        return null;
    }

    private static string? IdFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url, @"/(?:opus|dynamic)/(\d{6,})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"(BV[0-9A-Za-z]{10})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"/video/av(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) return "av" + m.Groups[1].Value;
        return null;
    }

    /// <summary>把读到的一棒事实转成 M4 的待确认记录。Notes 留"来源=补录"，Confirmed 保持 false。</summary>
    private static ReviewItem ToReviewItem(PostFacts facts, PostRef reference)
    {
        var item = new ReviewItem
        {
            CardId = string.IsNullOrWhiteSpace(facts.PostId) ? reference.Id : facts.PostId,
            Bar = facts.BarNumber,
            BarRaw = facts.BarRaw,
            IsEx = facts.IsEx,
            DeclaredTime = facts.DeclaredTime,
            PubTs = facts.PubTs,
            PubTimeText = facts.PubLocal == DateTime.MinValue ? "-" : facts.PubLocal.ToString("yyyy-MM-dd HH:mm:ss"),
            PosterName = facts.AuthorName,
            PosterMid = facts.AuthorUid,
            CreatorInText = facts.CreatorInText,
            ResolvedAuthor = string.IsNullOrWhiteSpace(facts.CreatorInText) ? facts.AuthorName : facts.CreatorInText,
            PrevName = facts.Prevs.FirstOrDefault()?.Name,
            NextName = facts.Nexts.FirstOrDefault()?.Name,
            IsProxy = facts.IsProxy,
            ProxyName = facts.ProxyName,
            OpusUrl = string.IsNullOrWhiteSpace(facts.Url) ? reference.Url : facts.Url,
            Type = facts.Kind.ToString().ToLowerInvariant(),
            TypeLabel = facts.WorkKindLabel ?? string.Empty,
            MatchedStyle = "补录",
            MergedText = string.IsNullOrWhiteSpace(facts.MergedText) ? facts.BodyText : facts.MergedText,
            Confirmed = false,
            Ignored = false,
        };

        item.Notes.Add("来源=补录");
        if (!string.IsNullOrWhiteSpace(facts.RelayName)) item.Notes.Add("活动=" + facts.RelayName);
        item.Notes.Add("链接=" + reference.Url);
        foreach (var w in facts.ReaderWarnings.Take(3)) item.Notes.Add("读取提示：" + w);
        return item;
    }

    private static string BarTextOf(ReviewItem item) => item.IsEx ? "EX" : (item.Bar?.ToString() ?? "?");

    private static void HideCursor()
    {
        try { Console.CursorVisible = false; } catch { }
    }

    private void Move(int delta)
    {
        var v = Visible;
        if (v.Count == 0) return;
        _index = Math.Clamp(_index + delta, 0, v.Count - 1);
    }

    private void AdvanceIfFiltered()
    {
        if (_onlyPending) RecomputeAssertions();
    }

    private void RecomputeAssertions()
    {
        var active = _items.Where(i => !i.Ignored && !i.IsSummary).ToList();
        var numbered = active.Where(i => i.Bar.HasValue).ToList();
        var dups = numbered.GroupBy(i => i.Bar!.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        // 缺棒只统计**已知棒位之间**的空隙：单链接模式只覆盖链上的少数帖子，
        // 从 1 数到 max 会把两端之外都算成缺失（实测只给 1 条第27棒时误报"缺棒 26"）。
        var missing = new List<int>();
        if (numbered.Count >= 2)
        {
            int min = numbered.Min(i => i.Bar!.Value);
            int max = numbered.Max(i => i.Bar!.Value);
            missing = Enumerable.Range(min, max - min + 1)
                                 .Where(n => !numbered.Any(i => i.Bar == n))
                                 .ToList();
        }

        _assertions = (numbered.Count + active.Count(i => i.IsEx), dups.Count, missing,
                       active.Count(i => i.Bar is null && !i.IsEx));
    }

    private void Save()
    {
        try
        {
            if (_slug is { Length: > 0 }) ReviewStore.Save(_workRoot, _slug, _items);
            else ReviewStore.Save(_workRoot, _topicId, _items);
            _dirty = false;
            _toast = $"已保存 review.jsonl（{_items.Count} 条）";
        }
        catch (Exception ex)
        {
            _toast = "保存失败：" + ex.Message;
        }
    }

    /// <summary>单行输入（就地编辑）。用 Console.ReadLine 最简单可靠，编辑后返回原视图。</summary>
    private void EditField(string label, string current, Func<string, string> apply)
    {
        _c.Restore();
        Console.WriteLine();
        Console.Write($"{label}（当前：{current}）> ");
        var input = Console.ReadLine() ?? string.Empty;
        _toast = apply(input);
        _dirty = true;
        try { Console.CursorVisible = false; } catch { }
    }

    private static void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { /* 打开失败不影响 TUI */ }
    }
}
