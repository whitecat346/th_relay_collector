#nullable enable
using System.Text;
using JieLi.Tui.Text;

namespace JieLi.Tui.Tui.Rendering;

/// <summary>列的重要度。终端变窄时按 P3 → P2 → P1 的顺序丢列，P0 尽量保留。</summary>
public enum Priority
{
    P0,
    P1,
    P2,
    P3,
}

/// <summary>一列的定义：宽度、对齐、重要度、在什么终端宽度下才显示。</summary>
public sealed class TableColumn
{
    public required string Header { get; init; }

    /// <summary>固定列宽；0 表示按 Min 取。Flexible 列忽略该值。</summary>
    public int Width { get; init; }

    public int Min { get; init; } = 3;

    /// <summary>吃掉剩余宽度（全表最多一列）。</summary>
    public bool Flexible { get; init; }

    public bool RightAlign { get; init; }

    public Priority Priority { get; init; } = Priority.P0;

    /// <summary>终端宽度 ≥ 该值时才显示；0 = 总是显示。</summary>
    public int ShowFromWidth { get; init; }
}

/// <summary>
/// 自适应表格：给定列定义与终端宽度，算出每列宽度并渲染行。
/// 保证：可见行的显示宽度 ≤ 终端宽度；窄屏优先丢低优先级列，而不是横向截断或折行。
/// 宽度一律用 <see cref="TextWidth"/>（Wcwidth）算，表头与数据不会因口径不同错位。
/// </summary>
public sealed class TableBuilder
{
    private const string Sep = " ";

    private readonly TableColumn[] _cols;

    public TableBuilder(params TableColumn[] columns) => _cols = columns;

    public IReadOnlyList<TableColumn> Columns => _cols;

    /// <summary>计算每列宽度（隐藏列宽度为 0）。keep 是最终保留的列下标，按原顺序。</summary>
    public int[] Widths(int width, out List<int> keep)
    {
        width = Math.Max(10, width);
        keep = new List<int>();
        for (int i = 0; i < _cols.Length; i++)
        {
            if (_cols[i].ShowFromWidth == 0 || width >= _cols[i].ShowFromWidth) keep.Add(i);
        }

        // 1) 丢列：从最低优先级开始，直到固定宽度能塞进一行（留出灵活列的最小宽）。
        while (keep.Count > 1 && FixedSum(keep) + SepCount(keep) + FlexMin(keep) > width)
        {
            int drop = LowestPriorityIndex(keep);
            if (drop < 0) break;
            keep.RemoveAt(drop);
        }

        // 2) 分宽度：固定列先取 Width（0 则 Min），其余给灵活列。
        var w = new int[_cols.Length];
        int fixedSum = 0;
        foreach (int i in keep)
        {
            w[i] = FixedWidth(i);
            fixedSum += w[i];
        }

        int flex = keep.FirstOrDefault(i => _cols[i].Flexible, -1);
        if (flex >= 0)
        {
            int avail = width - SepCount(keep) - (fixedSum - w[flex]);
            w[flex] = Math.Max(_cols[flex].Min, avail);
        }

        // 3) 仍然超宽：把非灵活列按优先级从低到高压到 Min。
        while (TotalWidth(w, keep) > width)
        {
            int idx = ShrinkableIndex(w, keep, flex);
            if (idx < 0) break;
            w[idx]--;
        }

        // 4) 兜底：灵活列再让出超出的部分（不低于 Min）。
        int over = TotalWidth(w, keep) - width;
        if (over > 0 && flex >= 0 && w[flex] - over >= _cols[flex].Min)
        {
            w[flex] -= over;
        }

        return w;
    }

    /// <summary>渲染表头 + 数据行。rowRole 可按行号给整行上色（如选中行反显）。</summary>
    public List<string> Render(int width, IReadOnlyList<string[]> rows, Func<int, Role?>? rowRole = null, Theme? theme = null)
    {
        var w = Widths(width, out var keep);
        var lines = new List<string>(rows.Count + 1) { Header(width, theme, w, keep) };
        for (int r = 0; r < rows.Count; r++)
        {
            lines.Add(DataRow(rows[r], w, keep, rowRole?.Invoke(r), theme));
        }
        return lines;
    }

    public string Header(int width, Theme? theme, int[] w, List<int> keep)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < keep.Count; k++)
        {
            int i = keep[k];
            if (k > 0) sb.Append(Sep);
            sb.Append(Cell(_cols[i].Header, w[i], _cols[i].RightAlign));
        }
        var text = sb.ToString();
        return theme is null ? text : theme.Bold(text);
    }

    public string DataRow(string[] cells, int[] w, List<int> keep, Role? role, Theme? theme)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < keep.Count; k++)
        {
            int i = keep[k];
            if (k > 0) sb.Append(Sep);
            var value = i < cells.Length ? cells[i] : string.Empty;
            sb.Append(Cell(value, w[i], _cols[i].RightAlign));
        }
        var text = sb.ToString();
        return role is null || theme is null ? text : theme.Paint(role.Value, text);
    }

    /// <summary>按显示宽度裁剪并补齐到列宽。</summary>
    public static string Cell(string? text, int width, bool rightAlign)
    {
        if (width <= 0) return string.Empty;
        var t = TextWidth.Truncate(text ?? string.Empty, width);
        return rightAlign ? TextWidth.PadLeft(t, width) : TextWidth.Pad(t, width);
    }

    private int FixedWidth(int i) => _cols[i].Flexible ? 0 : (_cols[i].Width > 0 ? _cols[i].Width : _cols[i].Min);

    private int FixedSum(List<int> keep) => keep.Sum(i => FixedWidth(i));

    private int FlexMin(List<int> keep)
    {
        int flex = keep.FirstOrDefault(i => _cols[i].Flexible, -1);
        return flex < 0 ? 0 : _cols[flex].Min;
    }

    private static int SepCount(List<int> keep) => Math.Max(0, keep.Count - 1);

    private static int TotalWidth(int[] w, List<int> keep) => keep.Sum(i => w[i]) + SepCount(keep);

    /// <summary>返回最该被丢掉的列（在 keep 里的位置）：最低优先级里最靠右的那列；全是 P0 返回 -1。</summary>
    private int LowestPriorityIndex(List<int> keep)
    {
        for (var p = Priority.P3; p > Priority.P0; p--)
        {
            for (int k = keep.Count - 1; k >= 0; k--)
            {
                if (_cols[keep[k]].Priority == p) return k;
            }
        }
        return -1;
    }

    /// <summary>返回下一个可压缩的列（keep 下标）：优先级最低且宽于 Min，跳过灵活列。</summary>
    private int ShrinkableIndex(int[] w, List<int> keep, int flex)
    {
        int best = -1;
        Priority bestP = Priority.P0;
        foreach (int i in keep)
        {
            if (i == flex) continue;
            if (w[i] <= _cols[i].Min) continue;
            var p = _cols[i].Priority;
            if (best < 0 || p > bestP)
            {
                best = i;
                bestP = p;
            }
        }
        return best < 0 ? -1 : keep.IndexOf(best);
    }
}
