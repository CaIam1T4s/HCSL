using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.Host.Views;

/// <summary>
/// Bands behind log lines, drawn instead of hosted as children: adding Border children from a layout callback
/// re-ran layout and looped ("Infinite layout loop detected"), while drawing only invalidates visuals.
/// Three groups share one layer: the engine echo of a written command, every search match, and the current match.
/// </summary>
public sealed class LogBandLayer : Control
{
    private double lineHeight, origin;
    private int[] echo = [], matches = [], current = [];

    static LogBandLayer()
    {
        AffectsRender<LogBandLayer>(EchoBrushProperty, MatchBrushProperty, CurrentMatchBrushProperty);
    }

    public static readonly StyledProperty<IBrush?> EchoBrushProperty =
        AvaloniaProperty.Register<LogBandLayer, IBrush?>(nameof(EchoBrush));

    public static readonly StyledProperty<IBrush?> MatchBrushProperty =
        AvaloniaProperty.Register<LogBandLayer, IBrush?>(nameof(MatchBrush));

    public static readonly StyledProperty<IBrush?> CurrentMatchBrushProperty =
        AvaloniaProperty.Register<LogBandLayer, IBrush?>(nameof(CurrentMatchBrush));

    public IBrush? EchoBrush { get => GetValue(EchoBrushProperty); set => SetValue(EchoBrushProperty, value); }

    public IBrush? MatchBrush { get => GetValue(MatchBrushProperty); set => SetValue(MatchBrushProperty, value); }

    public IBrush? CurrentMatchBrush { get => GetValue(CurrentMatchBrushProperty); set => SetValue(CurrentMatchBrushProperty, value); }

    /// <summary>
    /// Rows to tint, ascending, with the measured line height and the text origin; the layer draws only what its
    /// bounds can show and repaints only when one of these values actually changed.
    /// </summary>
    public void SetBands(IReadOnlyList<int> echoRows, IReadOnlyList<int> matchRows, IReadOnlyList<int> currentRows,
        double lineHeight, double origin)
    {
        if (this.lineHeight == lineHeight && this.origin == origin &&
            echo.AsSpan().SequenceEqual(echoRows.ToArray()) && matches.AsSpan().SequenceEqual(matchRows.ToArray()) &&
            current.AsSpan().SequenceEqual(currentRows.ToArray()))
            return;
        echo = [.. echoRows];
        matches = [.. matchRows];
        current = [.. currentRows];
        this.lineHeight = lineHeight;
        this.origin = origin;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (lineHeight <= 0 || Bounds.Width <= 0) return;
        Fill(context, echo, EchoBrush);
        Fill(context, matches, MatchBrush);
        Fill(context, current, CurrentMatchBrush);
    }

    private void Fill(DrawingContext context, int[] rows, IBrush? fill)
    {
        if (fill is null || rows.Length == 0) return;
        foreach (var band in Bands(rows, lineHeight, origin, Bounds.Size))
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
