using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.Host.Views;

/// <summary>
/// Thin bands behind selected log lines, drawn instead of hosted as children. Adding Border children from a layout
/// callback re-ran layout and looped ("Infinite layout loop detected"); a drawing primitive invalidates only visuals.
/// </summary>
public sealed class EchoBandLayer : Control
{
    private double lineHeight, origin;
    private int[] lines = [];

    static EchoBandLayer()
    {
        AffectsRender<EchoBandLayer>(FillProperty);
    }

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<EchoBandLayer, IBrush?>(nameof(Fill));

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Row indices to tint, ascending; the layer draws only what its bounds can show.</summary>
    public void SetBands(IReadOnlyList<int> rows, double lineHeight, double origin)
    {
        if (this.lineHeight == lineHeight && this.origin == origin && lines.AsSpan().SequenceEqual(rows.ToArray()))
            return;
        lines = rows.ToArray();
        this.lineHeight = lineHeight;
        this.origin = origin;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var fill = Fill;
        if (fill is null || lines.Length == 0 || lineHeight <= 0 || Bounds.Width <= 0) return;
        foreach (var band in Bands(lines, lineHeight, origin, Bounds.Size))
            context.FillRectangle(fill, band);
    }

    /// <summary>Bands that intersect the visible area, in layer coordinates; pure so the geometry is testable.</summary>
    public static IReadOnlyList<Rect> Bands(IReadOnlyList<int> rows, double lineHeight, double origin, Size size)
    {
        var bands = new List<Rect>(rows.Count);
        if (lineHeight <= 0 || size.Width <= 0 || size.Height <= 0) return bands;
        for (var i = 0; i < rows.Count; i++)
        {
            var top = origin + rows[i] * lineHeight;
            if (top + lineHeight < 0 || top > size.Height) continue;
            bands.Add(new Rect(0, top, size.Width, lineHeight));
        }
        return bands;
    }
}
