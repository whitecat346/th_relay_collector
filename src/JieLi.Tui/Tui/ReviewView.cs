#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using JieLi.Tui.Chain;
using JieLi.Tui.Data;
using JieLi.Tui.Model;
using JieLi.Tui.Text;
using JieLi.Tui.Tui.Rendering;

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
    private readonly Theme _theme;

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

    // ---- 叠加层：? 帮助 / V 全文查看器。叠加层优先吃键，Esc 只关它 ----
    private enum Overlay
    {
        None,
        Help,
        Reader,
    }

    private Overlay _overlay;
    private int _readerScroll;
    private string _readerTitle = string.Empty;
    private List<string> _readerLines = new();

    // ---- 帧内输入：所有可编辑字段（含备注、补录链接）都在帧内输入，
    //      不再 Restore() 退出备用屏去 ReadLine —— 那是「按键后整帧错位/不刷新」的根因。
    //      中文 IME 的候选窗在备用屏里可能贴边；若遇到错位可用 --no-alt-screen 回内联模式。
    private bool _inputActive;
    private string _inputLabel = string.Empty;
    private string _inputValue = string.Empty;
    private Func<string, string>? _inputCommit;

    /// <summary>Q 第一次在「有未保存改动」时只进入待确认态，再按一次才真正丢弃退出。</summary>
    private bool _quitArmed;

    // 最近一帧的尺寸（打开查看器时要按当前宽度预折行）与查看器可视行数。
    private int _frameWidth = 80;
    private int _frameHeight = 24;
    private int _readerPageH = 10;

    public bool ShouldExit { get; private set; }

    public ReviewView(ConsoleHost c, string workRoot, long topicId, string topicName, string? slug = null, Theme? theme = null)
    {
        _c = c;
        _workRoot = workRoot;
        _topicId = topicId;
        _topicName = topicName;
        _slug = slug;
        _theme = theme ?? (c.Interactive ? Theme.Detect() : Theme.None);
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

    private static readonly TableBuilder ReviewTable = new(
        new TableColumn { Header = "棒", Width = 5, RightAlign = true, Priority = Priority.P0 },
        new TableColumn { Header = "时段", Width = 6, Priority = Priority.P0 },
        new TableColumn { Header = "创作者", Width = 18, Priority = Priority.P0 },
        new TableColumn { Header = "状态", Width = 10, Priority = Priority.P1 },
        new TableColumn { Header = "依据", Flexible = true, Min = 10, Priority = Priority.P1 });

    /// <summary>全部键位；页脚放不下时渲染层会自动截断并追加「? 帮助」。</summary>
    private static readonly (string Key, string Label)[] KeyHints =
    {
        ("Enter", "确认"), ("X", "忽略"), ("V", "全文"), ("B", "棒号"), ("T", "时段"),
        ("U", "创作者"), ("K", "类型"), ("N", "备注"), ("G", "缺口"), ("C", "链条"),
        ("S", "保存"), ("?", "帮助"), ("Esc", "上级"), ("Q", "退出"),
    };

    public void Render(int width, int height)
    {
        _frameWidth = width;
        _frameHeight = height;
        var f = new FrameBuilder(_c, _theme, width, height);

        // 叠加层优先：帮助 / 全文查看器打开时，底下那一屏不再参与渲染。
        if (_overlay == Overlay.Help) { RenderHelp(f); return; }
        if (_overlay == Overlay.Reader) { RenderReader(f); return; }

        // G / C 是只读面板：与确认列表互斥，Enter/X 等按键在面板里一律不生效。
        if (_panel != Panel.None)
        {
            RenderPanel(f);
            return;
        }

        var visible = Visible;
        if (_index >= visible.Count) _index = Math.Max(0, visible.Count - 1);

        f.Header(BuildTitle(f.Width), BuildStatsLine(), BuildAssertLine());

        var cur = Current;
        int detailH = cur is null ? 2 : 6;   // 分隔 + 详情行（不足时仍占位，帧高不抖）
        int viewH = Math.Max(1, f.BodyHeight - detailH);
        if (_index < _top) _top = _index;
        if (_index >= _top + viewH) _top = _index - viewH + 1;
        if (_top > Math.Max(0, visible.Count - viewH)) _top = Math.Max(0, visible.Count - viewH);

        var rows = new List<string[]>();
        for (int i = _top; i < Math.Min(visible.Count, _top + viewH); i++)
        {
            var it = visible[i];
            rows.Add(new[]
            {
                it.IsEx ? "EX" : (it.Bar?.ToString() ?? "?"),
                it.DeclaredTime ?? "-",
                it.ResolvedAuthor ?? it.PosterName,
                it.StatusLabel,
                it.MatchedStyle ?? it.Conflicts.FirstOrDefault() ?? it.Notes.FirstOrDefault() ?? string.Empty,
            });
        }

        f.Body(ReviewTable.Render(f.Width, rows, i =>
        {
            if (_top + i == _index) return Role.Selected;
            return visible[_top + i].NeedsAttention ? Role.Warning : Role.Normal;
        }, _theme));

        // ---- 详情面板 ----
        f.Body(_theme.Muted(ConsoleHost.Rule(f.Width)));
        if (cur is null)
        {
            f.Body(_theme.Success("（没有待确认项）") + _theme.Muted("  按 I 切换显示全部"));
        }
        else
        {
            f.Body($"{_theme.Bold($"第 {cur.Bar?.ToString() ?? "?"} 棒")}  {cur.DeclaredTime ?? "-"}  "
                   + _theme.Accent(cur.ResolvedAuthor ?? cur.PosterName)
                   + (cur.IsProxy ? _theme.Muted($"  （代发：{cur.ProxyName}）") : string.Empty));
            f.Body($"{_theme.Muted("发布者")} {cur.PosterName}  {_theme.Muted("mid")} {cur.PosterMid}  "
                   + $"{_theme.Muted("发布")} {cur.PubTimeText}"
                   + (cur.DeviationMinutes is { } d ? $"  {_theme.Muted("偏差")} {(d >= 0 ? "+" : "")}{d}m" : string.Empty));
            f.Body($"{_theme.Muted("作品")} {cur.OpusUrl}");
            if (cur.Conflicts.Count > 0)
                f.Body(_theme.Warning("⚠ " + string.Join("；", cur.Conflicts.Take(2))));
            else if (cur.Notes.Count > 0)
                f.Body(_theme.Muted("· " + string.Join("；", cur.Notes.Take(2))));
            else
                f.Body(string.Empty);
            f.Body(_theme.Muted("正文 ") + TextWidth.Truncate(cur.MergedText.Replace('\n', ' '), Math.Max(10, f.Width - 6)));
        }

        f.Toast(_inputActive ? InputPrompt(width) : _toast);
        f.Keys(_inputActive
            ? new[] { ("Enter", "确定"), ("Esc", "取消"), ("⌫", "删除") }
            : KeyHints);
        f.Render();
    }

    // -----------------------------------------------------------------------
    // 叠加层：? 帮助 / V 全文查看器。只在帧内渲染，Esc 关掉即回列表。
    // -----------------------------------------------------------------------

    private void OpenHelp()
    {
        _overlay = Overlay.Help;
        _toast = null;
    }

    private void HandleOverlayKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape) { _overlay = Overlay.None; return; }
        if (key.Key == ConsoleKey.Oem2 && key.KeyChar == '?') { _overlay = Overlay.None; return; }
        if (_overlay != Overlay.Reader) return;
        if (key.Key == ConsoleKey.V) { _overlay = Overlay.None; return; }

        int page = Math.Max(1, _readerPageH - 1);
        int max = Math.Max(0, _readerLines.Count - _readerPageH);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: _readerScroll = Math.Max(0, _readerScroll - 1); break;
            case ConsoleKey.DownArrow: _readerScroll = Math.Min(max, _readerScroll + 1); break;
            case ConsoleKey.PageUp: _readerScroll = Math.Max(0, _readerScroll - page); break;
            case ConsoleKey.PageDown: _readerScroll = Math.Min(max, _readerScroll + page); break;
            case ConsoleKey.Home: _readerScroll = 0; break;
            case ConsoleKey.End: _readerScroll = max; break;
        }
    }

    private void RenderHelp(FrameBuilder f)
    {
        f.Header(_theme.Bold("键位帮助"),
                 _theme.Muted($"视口 {_frameWidth}×{_frameHeight} · 宽度按显示列计算（中日韩=2 列）"));

        int wrapW = Math.Max(10, f.Width - 2);
        void Section(string title) => f.Body(_theme.Accent(title));

        // 按「键 + 说明」成组排：放不下就整组换行（续行缩进 4），不会把一个键拆成两行。
        void Items(params string[] items)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string item in items)
            {
                if (sb.Length == 0)
                {
                    sb.Append("  ").Append(item);
                }
                else if (TextWidth.Measure(sb.ToString()) + 4 + TextWidth.Measure(item) > f.Width)
                {
                    f.Body(sb.ToString());
                    sb.Clear();
                    sb.Append("    ").Append(item);
                }
                else
                {
                    sb.Append("    ").Append(item);
                }
            }
            if (sb.Length > 0) f.Body(sb.ToString());
        }

        Section("确认 / 编辑");
        Items("Enter 确认", "X 忽略", "R 撤销", "V 全文", "S 保存", "I 过滤");
        Section("改字段（帧内输入：Enter 确定，Esc 取消）");
        Items("B 棒号", "T 时段", "U 创作者", "K 类型", "P 代发者", "A 粘贴链接补录");
        foreach (string l in TextWidth.Wrap(
                     "N 备注 —— 帧内输入；中文长文本建议先粘到别处再补（帧内输入不走 IME 候选窗）。",
                     wrapW))
            f.Body("  " + l);
        Section("只读视图");
        Items("G 缺口清单", "C 证据链", "D 浏览器打开", "Esc 返回上一级", "Q 退出（有改动先拦一次）");
        Section("滚动");
        Items("↑↓ 逐行", "PgUp/PgDn 十行", "Home/End 首尾", "统计行右侧显示「行 n/m」");
        foreach (string l in TextWidth.Wrap(
                     "窗口尺寸变化会在下一帧自动重排；颜色按终端能力自动降级，NO_COLOR / TERM=dumb 时自动关闭。",
                     Math.Max(10, f.Width)))
            f.Body(_theme.Muted(l));
        f.Toast(null);
        f.Keys(("Esc", "关闭"));
        f.Render();
    }

    private void OpenReader(ReviewItem cur)
    {
        _readerTitle = $"全文 · 第 {cur.Bar?.ToString() ?? "?"} 棒 {cur.ResolvedAuthor ?? cur.PosterName}";
        var lines = new List<string>();
        int w = Math.Max(20, _frameWidth - 2);

        void Add(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) lines.AddRange(TextWidth.Wrap(text, w));
        }

        Add($"状态：{cur.StatusLabel}（{(cur.Confirmed ? "已确认" : cur.Ignored ? "已忽略" : "待确认")}）"
            + $"   时段：{cur.DeclaredTime ?? "-"}   发布：{cur.PubTimeText}"
            + (cur.DeviationMinutes is { } d ? $"   偏差：{(d >= 0 ? "+" : "")}{d}m" : string.Empty));
        Add($"作者：{cur.PosterName}   mid：{cur.PosterMid}");
        Add($"作品：{cur.OpusUrl}");
        if (cur.IsProxy) Add($"代发者：{cur.ProxyName}");
        if (cur.Conflicts.Count > 0)
        {
            lines.Add(_theme.Warning("⚠ 冲突"));
            foreach (var c in cur.Conflicts) Add("  · " + c);
        }
        if (cur.Notes.Count > 0)
        {
            lines.Add(_theme.Muted("· 推断 / 备注"));
            foreach (var n in cur.Notes) Add("  · " + n);
        }
        if (!string.IsNullOrWhiteSpace(cur.ManualNote)) { lines.Add("手记"); Add(cur.ManualNote); }
        if (!string.IsNullOrWhiteSpace(cur.MatchedStyle)) { lines.Add("匹配风格"); Add(cur.MatchedStyle); }
        lines.Add(string.Empty);
        lines.Add(_theme.Muted("── 正文 ──"));
        Add(cur.MergedText);

        _readerLines = lines;
        _readerScroll = 0;
        _overlay = Overlay.Reader;
        _toast = null;
    }

    private void RenderReader(FrameBuilder f)
    {
        f.Header(_theme.Bold(TextWidth.Truncate(_readerTitle, Math.Max(10, f.Width - 16))),
                 _theme.Muted("只读全文 · Esc 关闭"));
        int viewH = f.BodyHeight;
        _readerPageH = Math.Max(1, viewH);
        int max = Math.Max(0, _readerLines.Count - viewH);
        if (_readerScroll > max) _readerScroll = max;

        for (int i = 0; i < viewH; i++)
        {
            int idx = _readerScroll + i;
            f.Body(idx < _readerLines.Count ? _readerLines[idx] : string.Empty);
        }

        f.Toast(_readerLines.Count > viewH ? $"行 {_readerScroll + 1}-{Math.Min(_readerLines.Count, _readerScroll + viewH)} / {_readerLines.Count}" : null);
        f.Keys(("↑↓", "滚动"), ("PgUp/PgDn", "翻页"), ("Home/End", "首尾"), ("Esc", "关闭"));
        f.Render();
    }

    // -----------------------------------------------------------------------
    // 帧内输入：短字段不再跳到 ReadLine，帧和光标模型保持连续。
    // -----------------------------------------------------------------------

    private void StartInput(string label, string current, Func<string, string> apply)
    {
        _inputActive = true;
        _inputLabel = string.IsNullOrWhiteSpace(current) ? label : $"{label} [当前 {current}]";
        _inputValue = string.Empty;   // 从空开始输入；空值提交=清空/自动（各字段语义见帮助）
        _inputCommit = apply;
        _toast = null;
    }

    /// <summary>输入行：标签 + 值的可见尾巴，长 URL 也能看到正在输入的部分。</summary>
    private string InputPrompt(int width)
    {
        const string cursor = "▏";
        string label = _inputLabel + " > ";
        int labelW = TextWidth.Measure(label);
        int room = Math.Max(6, width - labelW - TextWidth.Measure(cursor));
        return label + Tail(_inputValue, room) + cursor;
    }

    /// <summary>取字符串结尾，使显示宽度不超过 maxWidth（不切开宽字符）。</summary>
    private static string Tail(string text, int maxWidth)
    {
        var elements = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) elements.Add(e.GetTextElement());
        var sb = new System.Text.StringBuilder();
        int used = 0;
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            int w = TextWidth.Measure(elements[i]);
            if (used + w > maxWidth) break;
            used += w;
            sb.Insert(0, elements[i]);
        }
        return sb.ToString();
    }

    private void HandleInputKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            string value = _inputValue;
            var commit = _inputCommit;
            _inputActive = false;
            _inputCommit = null;
            if (commit is not null) _toast = commit(value);
            return;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            _inputActive = false;
            _inputCommit = null;
            _toast = "已取消输入";
            return;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (_inputValue.Length > 0)
            {
                // 按码点删：低代理孤零零地留着会渲染成「�」
                int cut = char.IsLowSurrogate(_inputValue[^1]) && _inputValue.Length >= 2 ? 2 : 1;
                _inputValue = _inputValue[..^cut];
            }
            return;
        }

        char ch = key.KeyChar;
        if (ch >= ' ' && !char.IsControl(ch)) _inputValue += ch;
    }

    private string BuildTitle(int width) =>
        _theme.Bold(TextWidth.Truncate("人工确认 · " + _topicName, Math.Max(10, width - 14)))
        + _theme.Muted($"   topic {_topicId}");

    private string BuildStatsLine()
    {
        int pending = _items.Count(i => !i.Confirmed && !i.Ignored);
        int confirmed = _items.Count(i => i.Confirmed);
        int ignored = _items.Count(i => i.Ignored);

        var stat = new StringBuilder();
        stat.Append(_theme.Muted("待确认 ")).Append(_theme.Warning(pending.ToString()));
        stat.Append(_theme.Muted("  已确认 ")).Append(_theme.Success(confirmed.ToString()));
        stat.Append(_theme.Muted("  已忽略 ")).Append(ignored.ToString());
        stat.Append(_theme.Muted($"  共 {_items.Count} 条"));
        var vis = Visible;
        if (vis.Count > 0) stat.Append(_theme.Muted($"  行 {Math.Min(_index + 1, vis.Count)}/{vis.Count}"));
        stat.Append(_theme.Muted(_onlyPending ? "  [只看待确认]" : "  [显示全部]"));
        if (_dirty) stat.Append("   ").Append(_theme.Warning("● 未保存（S）"));
        return stat.ToString();
    }

    private string BuildAssertLine()
    {
        var a = _assertions;
        var line = new StringBuilder();
        line.Append(_theme.Muted("断言  ")).Append("有棒号 ").Append(_theme.Bold(a.Attributed.ToString()));
        if (a.Dups > 0) line.Append("   ").Append(_theme.Error($"棒号重复 {a.Dups}"));
        line.Append("   ").Append(a.Missing.Count > 0
            ? _theme.Error($"缺棒 {a.Missing.Count}")
            : _theme.Success("无缺棒"));
        if (a.Unattributed > 0) line.Append("   ").Append(_theme.Warning($"未归属 {a.Unattributed}"));
        return line.ToString();
    }

    /// <summary>返回 true 表示需要重新渲染由调用方控制；这里直接处理全部按键。</summary>
    public void HandleKey(ConsoleKeyInfo key)
    {
        // 提示只保留一帧：任何后续按键先清空，避免「已保存」「已取消输入」一直挂在底部。
        _toast = null;
        if (_overlay != Overlay.None) { HandleOverlayKey(key); return; }
        if (_inputActive) { HandleInputKey(key); return; }
        if (_panel != Panel.None)
        {
            HandlePanelKey(key);
            return;
        }

        // Esc 只负责「退回上一级」；退出应用是 Q 的职责（有未保存改动时 Q 先拦一次）。
        if (key.Key == ConsoleKey.Escape)
        {
            _toast = "已在最外层：Q 退出，S 保存，? 帮助";
            return;
        }
        if (key.Key == ConsoleKey.Q)
        {
            if (_dirty && !_quitArmed)
            {
                _quitArmed = true;
                _toast = "有未保存改动：再按 Q 放弃并退出，或按 S 保存";
                return;
            }
            ShouldExit = true;
            return;
        }
        _quitArmed = false;

        var cur = Current;
        switch (key.Key)
        {
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
            case ConsoleKey.Oem2:
                if (key.KeyChar == '?') { OpenHelp(); return; }
                break;
            case ConsoleKey.V:
                if (cur is not null) OpenReader(cur);
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
                StartInput("棒号（留空=未知）", cur.Bar?.ToString() ?? "", v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) { cur.Bar = null; _dirty = true; return "棒号已清空"; }
                    if (int.TryParse(v.Trim(), out var n)) { cur.Bar = n; _dirty = true; return $"棒号改为 {n}"; }
                    return "无效数字，未修改";
                });
                break;
            case ConsoleKey.T:
                StartInput("时段（如 20:30）", cur.DeclaredTime ?? "", v =>
                {
                    cur.DeclaredTime = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    _dirty = true;
                    return "时段已改为 " + (cur.DeclaredTime ?? "空");
                });
                break;
            case ConsoleKey.U:
                StartInput("创作者昵称", cur.ResolvedAuthor ?? cur.PosterName, v =>
                {
                    cur.ResolvedAuthor = string.IsNullOrWhiteSpace(v) ? cur.PosterName : v.Trim();
                    _dirty = true;
                    return "创作者已改为 " + cur.ResolvedAuthor;
                });
                break;
            case ConsoleKey.P:
                StartInput("代发者（留空=非代发）", cur.ProxyName ?? "", v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) { cur.IsProxy = false; cur.ProxyName = null; _dirty = true; return "已标记为非代发"; }
                    cur.IsProxy = true;
                    cur.ProxyName = v.Trim();
                    _dirty = true;
                    return "代发者已改为 " + cur.ProxyName;
                });
                break;
            case ConsoleKey.N:
                StartInput("备注（留空=清除）", cur.ManualNote ?? "", v =>
                {
                    cur.ManualNote = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                    _dirty = true;
                    return "备注已保存";
                });
                break;
            case ConsoleKey.K:
                StartInput("作品类型（插画/文章/视频/MMD/手书/COS/漫画/音乐…）",
                          string.IsNullOrWhiteSpace(cur.TypeLabel) ? BarParser.InferWorkKind(cur.Type, cur.MergedText) : cur.TypeLabel,
                            v =>
                            {
                                cur.TypeLabel = string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim();
                                _dirty = true;
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
        // 链条视图内：↑↓/PgUp/PgDn 选节点，Enter（或 V）看节点详情。
        if (_panel == Panel.Chain && _chain is not null)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: _chain.MoveNodeCursor(-1); return;
                case ConsoleKey.DownArrow: _chain.MoveNodeCursor(1); return;
                case ConsoleKey.PageUp: _chain.MoveNodeCursor(-10); return;
                case ConsoleKey.PageDown: _chain.MoveNodeCursor(10); return;
                case ConsoleKey.Home: _chain.MoveNodeCursor(-1_000_000); return;
                case ConsoleKey.End: _chain.MoveNodeCursor(1, toEnd: true); return;
                case ConsoleKey.Enter:
                case ConsoleKey.V:
                    OpenNodeDetail();
                    return;
            }
        }

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

    /// <summary>把选中节点摊进全文查看器（Esc 回到链条视图，选择保留）。</summary>
    private void OpenNodeDetail()
    {
        if (_chain is null) return;
        _readerTitle = "节点详情 · 选中节点（只读）";
        _readerLines = _chain.NodeDetailLines().ToList();
        _readerScroll = 0;
        _overlay = Overlay.Reader;
    }

    private void RenderPanel(FrameBuilder f)
    {
        if (_chain is null)
        {
            var title = _panel == Panel.Gaps ? "缺口清单" : "链条视图";
            ChainView.RenderUnavailable(f, title, _panelError,
                $"jieli --link <某一棒的帖子链接> [--date 2026-9-6] [--as {ChainSlug}]", _toast);
        }
        else if (_panel == Panel.Gaps)
        {
            _chain.RenderGaps(f, _toast);
        }
        else
        {
            _chain.RenderChain(f, _toast);
        }
    }

    /// <summary>
    /// A 键：粘链接补录。帧内输入（不再离开备用屏开 ReadLine）——
    /// 读不到/解析失败/重复都只提示，绝不改动当前列表。
    /// </summary>
    private void AddByLink()
    {
        StartInput("粘贴一棒帖子的链接（opus / 动态 / BV·av）", string.Empty, TryAddByLink);
    }

    private string TryAddByLink(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "没有读到链接，列表未改动。";

        var reference = LinkResolver.Resolve(input, out var reason);
        if (reference is null) return "补录失败：" + (reason ?? "无法识别该链接") + " 列表未改动。";
        if (reference.Kind == PostKind.Topic)
            return "补录失败：这是话题链接，不是一根棒。请粘某一棒帖子的链接。列表未改动。";
        if (FindDuplicate(reference.Id) is { } dup)
            return $"补录失败：第 {BarTextOf(dup)} 棒 {dup.ResolvedAuthor ?? dup.PosterName} 已在列表里，未重复添加。";

        var logs = new List<string>();
        try
        {
            using var http = new ChainHttp();
            var reader = new ChainPostReader(http);
            var facts = reader.Read(reference, m => logs.Add(m), CancellationToken.None);
            if (facts is null) return "补录失败：读取器没有返回内容，列表未改动。";

            var item = ToReviewItem(facts, reference);
            _items.Add(item);
            SortItems();
            _index = Visible.IndexOf(item);
            if (_index < 0)
            {
                // 当前在"只看待确认"以外的过滤状态时，强制回到全部视图，保证新条目可见。
                _onlyPending = false;
                _index = Math.Max(0, _items.IndexOf(item));
            }
            RecomputeAssertions();
            _dirty = true;
            return $"已补录：第 {BarTextOf(item)} 棒 {item.ResolvedAuthor ?? item.PosterName}（待确认；Enter 确认、S 保存）";
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            if (logs.Count > 0 && !detail.Contains(logs[^1], StringComparison.Ordinal))
                detail += "（" + logs[^1] + "）";
            return "补录失败：" + detail + " 列表未改动。";
        }
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
