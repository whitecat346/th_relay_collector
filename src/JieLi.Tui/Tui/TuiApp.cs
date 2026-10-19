#nullable enable
using System.Text;
using JieLi.Tui.Data;
using JieLi.Tui.Model;
using JieLi.Tui.Tui.Rendering;

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
    private readonly Theme _theme;

    private int _view;            // 0 = 活动列表, 1 = 棒链表
    private int _actIndex;
    private int _barIndex;
    private int _barTop;
    private bool _running = true;

    public TuiApp(ConsoleHost c, List<Activity> activities, string root, Theme? theme = null)
    {
        _c = c;
        _activities = activities;
        _root = root;
        _theme = theme ?? (c.Interactive ? Theme.Detect() : Theme.None);
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

        try
        {
            while (_running)
            {
                Render();
                var key = Console.ReadKey(intercept: true);
                HandleKey(key);
            }
        }
        finally
        {
            // 无论正常退出还是异常，都要回到主缓冲区并恢复光标。
            _c.Restore();
        }
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

    /// <summary>
    /// 表格可用的行数：头部 3（标题 / 统计 / 分隔）+ 页脚 3（分隔 / toast / 键位）之外，
    /// 再让出当前视图的详情块；保证至少 1 行。
    /// </summary>
    private int BodyHeight()
    {
        const int chrome = 6;
        return Math.Max(1, _c.Height - chrome - DetailHeight());
    }

    /// <summary>详情块占用的行数（含块上方那条分隔线）。</summary>
    private int DetailHeight()
    {
        if (_view == 1) return 5;   // 分隔 + 4 行详情

        var act = Current;
        if (act is null || act.Anomalies.Count == 0) return 0;
        return 2 + Math.Min(6, act.Anomalies.Count) + (act.Anomalies.Count > 6 ? 1 : 0);
    }

    // ------------------------------------------------------------------ 渲染

    private void Render()
    {
        _c.BeginFrame();
        var f = new FrameBuilder(_c, _theme, _c.Width, _c.Height);
        f.Header(BuildTitle(), BuildStats());

        if (_view == 0) RenderActivityList(f);
        else RenderBarTable(f);

        f.Keys(_view == 0
            ? new[] { ("↑/↓", "选择活动"), ("Enter", "查看棒表"), ("Q", "退出") }
            : new[] { ("↑/↓", "选择棒"), ("PgUp/PgDn", "翻页"), ("Home/End", "首尾"), ("Esc/Q", "返回") });

        f.Render();
        _c.EndFrame();
    }

    private string BuildTitle() =>
        _theme.Title("东方Project接力归档 · 只读审阅器 (M1)") + "   " + _theme.Muted(_root);

    private string BuildStats()
    {
        int totalBars = _activities.Sum(a => a.Bars.Count);
        int totalNa = _activities.Sum(a => a.NaCount);
        int badDirs = _activities.Count(a => a.Anomalies.Count > 0);

        var stat = new StringBuilder();
        stat.Append("活动 ").Append(_activities.Count);
        stat.Append(_theme.Muted("  ·  "));
        stat.Append("棒记录 ").Append(totalBars);
        stat.Append(_theme.Muted("  ·  ")).Append(_theme.Warning($"N/A {totalNa}"));
        stat.Append(_theme.Muted("  ·  "));
        stat.Append(badDirs > 0 ? _theme.Error($"异常活动 {badDirs}") : _theme.Success("无异常"));
        if (_view == 1 && Current is not null)
        {
            stat.Append(_theme.Muted("   ▸   ")).Append(_theme.Bold(Current.DirectoryName));
        }
        return stat.ToString();
    }

    // ---- 视图 1：活动列表 ----

    private static readonly TableBuilder ActivityTable = new(
        new TableColumn { Header = "", Width = 2, Priority = Priority.P0 },
        new TableColumn { Header = "活动目录", Flexible = true, Min = 10, Priority = Priority.P0 },
        new TableColumn { Header = "棒数", Width = 5, RightAlign = true, Priority = Priority.P1 },
        new TableColumn { Header = "缺", Width = 4, RightAlign = true, Priority = Priority.P2, ShowFromWidth = 70 },
        new TableColumn { Header = "最高", Width = 5, RightAlign = true, Priority = Priority.P2, ShowFromWidth = 80 },
        new TableColumn { Header = "编码", Width = 16, Priority = Priority.P3, ShowFromWidth = 110 },
        new TableColumn { Header = "异常", Width = 24, Priority = Priority.P3, ShowFromWidth = 100 });

    private void RenderActivityList(FrameBuilder f)
    {
        int viewH = Math.Max(1, f.BodyHeight - DetailHeight());
        int top = Math.Clamp(_actIndex - viewH / 2, 0, Math.Max(0, _activities.Count - viewH));
        if (_actIndex < top) top = _actIndex;
        if (_actIndex >= top + viewH) top = _actIndex - viewH + 1;

        var rows = new List<string[]>();
        for (int i = top; i < Math.Min(_activities.Count, top + viewH); i++)
        {
            var a = _activities[i];
            rows.Add(new[]
            {
                a.Anomalies.Count > 0 ? "!" : " ",
                a.DirectoryName,
                a.Bars.Count.ToString(),
                a.NaCount.ToString(),
                a.MaxBar?.ToString() ?? "-",
                a.SourceEncoding,
                a.Anomalies.Count == 0 ? "-" : string.Join("；", a.Anomalies),
            });
        }

        f.Body(ActivityTable.Render(f.Width, rows, i =>
        {
            if (top + i == _actIndex) return Role.Selected;
            return _activities[top + i].Anomalies.Count > 0 ? Role.Warning : null;
        }, _theme));

        // 选中活动的异常明细（在列表下方直接展开，省得进二级页）
        if (Current is not null && Current.Anomalies.Count > 0)
        {
            f.Body(string.Empty);
            f.Body(_theme.Warning("▸ " + Current.DirectoryName + " 的异常明细："));
            foreach (var an in Current.Anomalies.Take(6))
                f.Body("   " + _theme.Muted("· ") + an);
            if (Current.Anomalies.Count > 6)
                f.Body("   " + _theme.Muted($"… 另有 {Current.Anomalies.Count - 6} 条"));
        }
    }

    // ---- 视图 2：棒链表 ----

    private static readonly TableBuilder BarTable = new(
        new TableColumn { Header = "棒", Width = 6, RightAlign = true, Priority = Priority.P0 },
        new TableColumn { Header = "时间", Width = 6, Priority = Priority.P0 },
        new TableColumn { Header = "作者", Flexible = true, Min = 8, Priority = Priority.P0 },
        new TableColumn { Header = "类型", Width = 10, Priority = Priority.P1 },
        new TableColumn { Header = "状态", Width = 4, RightAlign = true, Priority = Priority.P2, ShowFromWidth = 80 },
        new TableColumn { Header = "作者链接", Width = 26, Priority = Priority.P3, ShowFromWidth = 110 },
        new TableColumn { Header = "作品链接", Width = 30, Priority = Priority.P3, ShowFromWidth = 110 });

    private void RenderBarTable(FrameBuilder f)
    {
        var act = Current;
        if (act is null)
        {
            f.Body(_theme.Warning("（无活动）"));
            return;
        }
        if (act.Bars.Count == 0)
        {
            f.Body(_theme.Warning("该活动没有可显示的棒记录。"));
            return;
        }

        int viewH = Math.Max(1, f.BodyHeight - DetailHeight());
        _barTop = Math.Clamp(_barTop, 0, Math.Max(0, act.Bars.Count - viewH));
        if (_barIndex >= _barTop + viewH) _barTop = _barIndex - viewH + 1;

        var rows = new List<string[]>();
        for (int i = _barTop; i < Math.Min(act.Bars.Count, _barTop + viewH); i++)
        {
            var b = act.Bars[i];
            rows.Add(new[]
            {
                b.Bar,
                b.Time,
                b.Author,
                b.Type,
                b.HasGap ? "!" : " ",
                Show(b.AuthorLink),
                Show(b.WorkLink),
            });
        }

        f.Body(BarTable.Render(f.Width, rows, i =>
        {
            if (_barTop + i == _barIndex) return Role.Selected;
            return act.Bars[_barTop + i].HasGap ? Role.Warning : null;
        }, _theme));

        // 详情面板
        f.Body(_theme.Muted(ConsoleHost.Rule(f.Width)));
        var cur = act.Bars[Math.Clamp(_barIndex, 0, act.Bars.Count - 1)];
        f.Body($"{_theme.Bold($"第 {cur.Bar} 棒")}  {cur.Time}   {_theme.Accent(cur.Author)}   {cur.Type}");
        f.Body($"{_theme.Muted("作者主页")} {Show(cur.AuthorLink)}");
        f.Body($"{_theme.Muted("作品链接")} {Show(cur.WorkLink)}");
        var gap = cur.GapSummary();
        f.Body(gap.Length > 0
            ? $"{_theme.Warning("⚠ " + gap)}   {_theme.Muted($"（CSV 第 {cur.LineNumber} 行）")}"
            : $"{_theme.Success("✓ 字段完整")}   {_theme.Muted($"（CSV 第 {cur.LineNumber} 行）")}");
    }

    private static string Show(string v) => BarRecord.IsNa(v) ? "N/A" : v;
}
