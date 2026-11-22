using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace HCSL.Host.Views;

/// <summary>
/// Draws the row rules of an Arc <see cref="Table"/> itself. Arc paints one 1px divider per row, but at fractional
/// display scaling the row pitch lands between device pixels and the line is blended away, so some separators vanish.
/// Drawing a snapped 1px line here keeps every row separated at any scale.
/// </summary>
public sealed class TableRowSeparators : Control
{
    private ScrollViewer? scroll;
    private Table? hooked;

    static TableRowSeparators()
    {
        AffectsRender<TableRowSeparators>(TableProperty, LineBrushProperty);
    }

    public static readonly StyledProperty<Table?> TableProperty =
        AvaloniaProperty.Register<TableRowSeparators, Table?>(nameof(Table));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<TableRowSeparators, IBrush?>(nameof(LineBrush));

    public Table? Table { get => GetValue(TableProperty); set => SetValue(TableProperty, value); }

    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (scroll is not null) scroll.ScrollChanged -= OnScrolled;
        scroll = null;
        hooked = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TableProperty) Hook();
    }

    private void Hook()
    {
        var table = Table;
        if (ReferenceEquals(table, hooked)) return;
        if (scroll is not null) scroll.ScrollChanged -= OnScrolled;
        scroll = null;
        hooked = table;
        if (table is null) return;
        scroll = table.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null) scroll.ScrollChanged += OnScrolled;
    }

    private void OnScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Table is not { } table || LineBrush is not { } line) return;
        var pitch = RowPitch(table);
        if (!double.IsFinite(pitch) || pitch <= 1) return;
        var offset = scroll?.Offset.Y ?? 0;
        var header = HeaderHeight(table) - offset;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var stroke = 1 / Math.Max(1, scale);
        for (var y = header + pitch; y < Bounds.Height; y += pitch)
        {
            if (y < header + pitch - 0.5) continue;
            // Snap to a device pixel so the 1px rule is not split across two rows by antialiasing.
            var snapped = Math.Round(y * scale) / scale;
            context.FillRectangle(line, new Rect(0, snapped, Bounds.Width, stroke));
        }
    }

    /// <summary>Row pitch from the realized rows, so a grown row still lines up; falls back to Arc's own height.</summary>
    private static double RowPitch(Table table)
    {
        var rows = new List<double>(2);
        foreach (var candidate in table.GetVisualDescendants())
        {
            if (candidate.GetType().Name != "TableRow" || candidate is not Control row) continue;
            if (row.TranslatePoint(default, table) is not { } origin) continue;
            rows.Add(origin.Y);
            if (rows.Count == 2) break;
        }
        if (rows.Count == 2 && rows[1] > rows[0]) return rows[1] - rows[0];
        // One realized row: no neighbouring rule to space against, so Arc's default row height is enough.
        return 52;
    }

    /// <summary>Header height from the realized header row; falls back to Arc's header token.</summary>
    private static double HeaderHeight(Table table)
    {
        foreach (var candidate in table.GetVisualDescendants())
            if (candidate.GetType().Name == "TableHeaderRow" && candidate is Control header && header.Bounds.Height > 0)
                return header.Bounds.Height;
        return 44;
    }
}
