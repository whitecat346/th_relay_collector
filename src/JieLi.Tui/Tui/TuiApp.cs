#nullable enable
using System.Text;
using JieLi.Tui.Data;
using JieLi.Tui.Model;
using JieLi.Tui.Text;

namespace JieLi.Tui.Tui;

/// <summary>
/// M1：只读审阅器。两个视图 —— 活动列表 / 单活动棒链表。
/// 不联网、不写入任何文件（这是 M1 的硬边界）。
/// </summary>
public sealed class TuiApp
{
    private readonly ConsoleHost _c;
    private readonly List<Activity> _activities;
    private readonly string _root;

    private int _view;            // 0 = 活动列表, 1 = 棒链表
    private int _actIndex;
    private int _barIndex;
    private int _barTop;
    private bool _running = true;

    public TuiApp(ConsoleHost c, List<Activity> activities, string root)
    {
        _c = c;
        _activities = activities;
        _root = root;
    }

    public int Run(string? jumpSlug = null)
    {
        if (jumpSlug is not null)
        {
            var idx = _activities.FindIndex(a => a.DirectoryName.Equals(jumpSlug, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { _actIndex = idx; _view = 1; }
        }

        if (!_c.Interactive)
        {
            Render();
            return 0;
        }

        while (_running)
        {
            Render();
            var key = Console.ReadKey(intercept: true);
            HandleKey(key);
        }

        _c.Restore();
        return 0;
    }

    private Activity? Current => _actIndex >= 0 && _actIndex < _activities.Count ? _activities[_actIndex] : null;

    private void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q:
                if (key.Modifiers == ConsoleModifiers.Control || _view == 0) _running = false;
                else { _view = 0; _barTop = 0; _barIndex = 0; }
                break;
            case ConsoleKey.Escape:
                if (_view == 1) { _view = 0; _barTop = 0; _barIndex = 0; }
                else _running = false;
                break;
            case ConsoleKey.UpArrow:
                Move(-1);
                break;
            case ConsoleKey.DownArrow:
                Move(1);
                break;
            case ConsoleKey.PageUp:
                if (_view == 1) { for (int i = 0; i < 10; i++) Move(-1); }
                break;
            case ConsoleKey.PageDown:
                if (_view == 1) { for (int i = 0; i < 10; i++) Move(1); }
                break;
            case ConsoleKey.Home:
                if (_view == 1 && Current is not null && Current.Bars.Count > 0) { _barIndex = 0; _barTop = 0; }
                break;
            case ConsoleKey.End:
                if (_view == 1 && Current is not null && Current.Bars.Count > 0)
                {
                    _barIndex = Current.Bars.Count - 1;
                    ScrollTo(_barIndex);
                }
                break;
            case ConsoleKey.Enter:
                if (_view == 0 && Current is not null && Current.Bars.Count > 0)
                {
                    _view = 1; _barIndex = 0; _barTop = 0;
                }
                break;
        }
    }

    private void Move(int delta)
    {
        if (_view == 0)
        {
            if (_activities.Count == 0) return;
            _actIndex = Math.Clamp(_actIndex + delta, 0, _activities.Count - 1);
        }
        else
        {
            var n = Current?.Bars.Count ?? 0;
            if (n == 0) return;
            _barIndex = Math.Clamp(_barIndex + delta, 0, n - 1);
            ScrollTo(_barIndex);
        }
    }

    private void ScrollTo(int index)
    {
        int viewH = BodyHeight();
        if (index < _barTop) _barTop = index;
        else if (index >= _barTop + viewH) _barTop = index - viewH + 1;
    }

    private int BodyHeight()
    {
        // 保证 body 至少 1 行；棒链表要给详情面板留 4 行
        int reserved = _view == 1 ? 8 : 4;
        return Math.Max(1, ConsoleHost.WindowHeight - reserved);
    }

    // ------------------------------------------------------------------ 渲染

    private void Render()
    {
        int width = ConsoleHost.WindowWidth;
        _c.BeginFrame();

        RenderHeader(width);

        if (_view == 0) RenderActivityList(width);
        else RenderBarTable(width);

        RenderFooter(width);
        _c.EndFrame();
    }

    private void RenderHeader(int width)
    {
        int totalBars = _activities.Sum(a => a.Bars.Count);
        int totalNa = _activities.Sum(a => a.NaCount);
        int badDirs = _activities.Count(a => a.Anomalies.Count > 0);

        var title = new StringBuilder();
        title.Append(_c.Bold).Append("东方Project接力归档 · 只读审阅器 (M1)").Append(_c.Reset);
        title.Append(_c.Dim).Append("   ").Append(_root).Append(_c.Reset);

        _c.Line(title.ToString());
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);

        var stat = new StringBuilder();
        stat.Append($"活动 {_activities.Count}");
        stat.Append(_c.Dim).Append("  ·  ").Append(_c.Reset);
        stat.Append($"棒记录 {totalBars}");
        stat.Append(_c.Dim).Append("  ·  ").Append(_c.Reset).Append(_c.Yellow).Append($"N/A {totalNa}").Append(_c.Reset);
        stat.Append(_c.Dim).Append("  ·  ").Append(_c.Reset);
        stat.Append(badDirs > 0 ? _c.Red + $"异常活动 {badDirs}" + _c.Reset : _c.Green + "无异常" + _c.Reset);
        if (_view == 1 && Current is not null)
        {
            stat.Append(_c.Dim).Append("   ▸   ").Append(_c.Reset);
            stat.Append(_c.Bold).Append(Current.DirectoryName).Append(_c.Reset);
        }
        _c.Line(stat.ToString());
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);
    }

    private void RenderFooter(int width)
    {
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);
        var keys = _view == 0
            ? "↑/↓ 选择活动   Enter 查看棒表   Q 退出"
            : "↑/↓ 选择棒   PgUp/PgDn 翻页   Home/End 首尾   Esc/Q 返回活动列表";
        _c.Line(_c.Dim + keys + _c.Reset);
    }

    // ---- 视图 1：活动列表 ----

    private void RenderActivityList(int width)
    {
        // 列：状态 目录名 棒数 缺 编码 异常
        int wStatus = 2, wBars = 5, wNa = 4, wMax = 5;
        int wName = Math.Clamp(width - (wStatus + wBars + wNa + wMax + 34), 20, 46);
        int wEnc = 16;
        int wAnom = Math.Max(10, width - (wStatus + wName + wBars + wNa + wMax + wEnc + 10));

        var head = new StringBuilder();
        head.Append(' ').Append(TextWidth.Pad("", wStatus)).Append(' ');
        head.Append(TextWidth.Pad("活动目录", wName)).Append(' ');
        head.Append(TextWidth.PadLeft("棒数", wBars)).Append(' ');
        head.Append(TextWidth.PadLeft("缺", wNa)).Append(' ');
        head.Append(TextWidth.PadLeft("最高", wMax)).Append(' ');
        head.Append(TextWidth.Pad("编码", wEnc)).Append(' ');
        head.Append("异常");
        _c.Line(_c.Bold + head.ToString() + _c.Reset);

        int viewH = BodyHeight();
        int top = Math.Clamp(_actIndex - viewH / 2, 0, Math.Max(0, _activities.Count - viewH));
        if (_actIndex < top) top = _actIndex;
        if (_actIndex >= top + viewH) top = _actIndex - viewH + 1;

        for (int i = top; i < Math.Min(_activities.Count, top + viewH); i++)
        {
            var a = _activities[i];
            bool sel = i == _actIndex;

            var sb = new StringBuilder();
            sb.Append(sel ? _c.Inverse : string.Empty);
            sb.Append(' ');
            sb.Append(a.Anomalies.Count > 0 ? _c.Red + "!" + _c.Reset : " ");
            sb.Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(a.DirectoryName, wName), wName)).Append(' ');
            sb.Append(TextWidth.PadLeft(a.Bars.Count.ToString(), wBars)).Append(' ');
            sb.Append(TextWidth.PadLeft(a.NaCount.ToString(), wNa)).Append(' ');
            sb.Append(TextWidth.PadLeft(a.MaxBar?.ToString() ?? "-", wMax)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(a.SourceEncoding, wEnc), wEnc)).Append(' ');
            sb.Append(TextWidth.Truncate(a.Anomalies.Count == 0 ? "-" : string.Join("；", a.Anomalies), wAnom));
            sb.Append(sel ? _c.Reset : string.Empty);
            _c.Line(sb.ToString());
        }

        // 选中活动的异常明细（在列表下方直接展开，省得进二级页）
        if (Current is not null && Current.Anomalies.Count > 0)
        {
            _c.Line();
            _c.Line(_c.Yellow + "▸ " + Current.DirectoryName + " 的异常明细：" + _c.Reset);
            foreach (var an in Current.Anomalies.Take(6))
                _c.Line("   " + _c.Dim + "· " + _c.Reset + an);
            if (Current.Anomalies.Count > 6)
                _c.Line("   " + _c.Dim + $"… 另有 {Current.Anomalies.Count - 6} 条" + _c.Reset);
        }
    }

    // ---- 视图 2：棒链表 ----

    private void RenderBarTable(int width)
    {
        var act = Current;
        if (act is null) { _c.Line("（无活动）"); return; }
        if (act.Bars.Count == 0) { _c.Line(_c.Yellow + "该活动没有可显示的棒记录。" + _c.Reset); return; }

        int wBar = 6, wTime = 6, wType = 10, wStat = 10;
        int wLink = 30, wALink = 26;
        int wAuthor = Math.Clamp(width - (wBar + wTime + wType + wStat + wLink + wALink + 12), 12, 40);

        var head = new StringBuilder();
        head.Append(TextWidth.PadLeft("棒", wBar)).Append(' ');
        head.Append(TextWidth.Pad("时间", wTime)).Append(' ');
        head.Append(TextWidth.Pad("作者", wAuthor)).Append(' ');
        head.Append(TextWidth.Pad("类型", wType)).Append(' ');
        head.Append(TextWidth.Pad("作者链接", wALink)).Append(' ');
        head.Append(TextWidth.Pad("作品链接", wLink)).Append(' ');
        head.Append("状态");
        _c.Line(_c.Bold + head.ToString() + _c.Reset);

        int viewH = BodyHeight();
        for (int i = _barTop; i < Math.Min(act.Bars.Count, _barTop + viewH); i++)
        {
            var b = act.Bars[i];
            bool sel = i == _barIndex;

            var sb = new StringBuilder();
            if (sel) sb.Append(_c.Inverse);
            sb.Append(TextWidth.PadLeft(TextWidth.Truncate(b.Bar, wBar), wBar)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(b.Time, wTime), wTime)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(b.Author, wAuthor), wAuthor)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(b.Type, wType), wType)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(Show(b.AuthorLink), wALink), wALink)).Append(' ');
            sb.Append(TextWidth.Pad(TextWidth.Truncate(Show(b.WorkLink), wLink), wLink)).Append(' ');
            sb.Append(b.HasGap ? "!" : " ");
            if (sel) sb.Append(_c.Reset);
            _c.Line(sb.ToString());
        }

        // 详情面板
        _c.Line(_c.Dim + ConsoleHost.Rule(width) + _c.Reset);
        var cur = act.Bars[_barIndex];
        _c.Line($"{_c.Bold}第 {cur.Bar} 棒{_c.Reset}  {cur.Time}   {_c.Cyan}{cur.Author}{_c.Reset}   {cur.Type}");
        _c.Line($"{_c.Dim}作者主页{_c.Reset} {Show(cur.AuthorLink)}");
        _c.Line($"{_c.Dim}作品链接{_c.Reset} {Show(cur.WorkLink)}");
        var gap = cur.GapSummary();
        _c.Line(gap.Length > 0
            ? $"{_c.Yellow}⚠ {gap}{_c.Reset}   {_c.Dim}（CSV 第 {cur.LineNumber} 行）{_c.Reset}"
            : $"{_c.Green}✓ 字段完整{_c.Reset}   {_c.Dim}（CSV 第 {cur.LineNumber} 行）{_c.Reset}");
    }

    private static string Show(string v) => BarRecord.IsNa(v) ? "N/A" : v;
}
