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
        for (var i = 0; i < lines.Length; i++)
        {
            var top = origin + lines[i] * lineHeight;
            if (top + lineHeight < 0 || top > Bounds.Height) continue;
            context.FillRectangle(fill, new Rect(0, top, Bounds.Width, lineHeight));
        }
    }
}
