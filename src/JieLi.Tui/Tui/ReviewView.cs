#nullable enable
using System.Text;
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
///   I          只看待确认项（切换过滤）    Esc/Q  返回
/// </summary>
public sealed class ReviewView
{
    private readonly ConsoleHost _c;
    private readonly string _workRoot;
    private readonly long _topicId;
    private readonly string _topicName;

    private List<ReviewItem> _items;
    private int _index;
    private int _top;
    private bool _onlyPending = true;
    private string? _toast;
    private bool _dirty;

    /// <summary>人工改动后重新计算的断言结果（立即反馈）。</summary>
    private (int Attributed, int Dups, List<int> Missing, int Unattributed) _assertions;

    public bool ShouldExit { get; private set; }

    public ReviewView(ConsoleHost c, string workRoot, long topicId, string topicName)
    {
        _c = c;
        _workRoot = workRoot;
        _topicId = topicId;
        _topicName = topicName;
        _items = ReviewStore.Load(workRoot, topicId);

        // 初始光标：只显示待确认项时，必须落在第一个"未确认且未忽略"的条目上。
        // （早期版本用 FindIndex 对全体条目查找，在 onlyPending 过滤下会得到 -1，
        //   于是 Current 恒为 null，所有确认/忽略按键静默失效 —— 由 M4 自检发现。）
        var pending = _items.FindIndex(i => !i.Confirmed && !i.Ignored);
        _index = pending >= 0 ? pending : 0;
        RecomputeAssertions();
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
        _c.Line(_c.Dim + "Enter 确认  X 忽略  R 撤销  B 棒号  T 时段  U 创作者  K 类型  P 代发者  N 备注  D 打开链接  S 保存  I 过滤  Esc 返回" + _c.Reset);
    }

    /// <summary>返回 true 表示需要重新渲染由调用方控制；这里直接处理全部按键。</summary>
    public void HandleKey(ConsoleKeyInfo key)
    {
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
                              cur.TypeLabel = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                              return "类型已改为 " + (cur.TypeLabel ?? "（自动）");
                          });
                break;
        }
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
        int max = numbered.Select(i => i.Bar!.Value).DefaultIfEmpty(0).Max();
        var missing = Enumerable.Range(1, max).Where(n => !numbered.Any(i => i.Bar == n)).ToList();
        _assertions = (numbered.Count + active.Count(i => i.IsEx), dups.Count, missing,
                       active.Count(i => i.Bar is null && !i.IsEx));
    }

    private void Save()
    {
        try
        {
            ReviewStore.Save(_workRoot, _topicId, _items);
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
