using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.Host.Views;

/// <summary>One tinted span: the row it belongs to, and the horizontal span inside that row's line.</summary>
public readonly record struct BandSpan(int Row, double Left, double Width);

/// <summary>
/// Bands behind the log text, drawn instead of hosted as children: adding Border children from a layout callback
/// re-ran layout and looped ("Infinite layout loop detected"), while drawing only invalidates visuals.
/// The engine echo of a written command tints a whole line; search matches tint only the matched characters.
/// </summary>
public sealed class LogBandLayer : Control
{
    private double lineHeight, origin;
    private int[] echo = [];
    private BandSpan[] matches = [], current = [];

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
    /// Bands to draw with the measured line height and the text origin. Echo rows tint whole lines, match spans tint
    /// the matched characters; the layer draws only what its bounds can show and repaints only on a real change.
    /// </summary>
    public void SetBands(IReadOnlyList<int> echoRows, IReadOnlyList<BandSpan> matchSpans, IReadOnlyList<BandSpan> currentSpans,
        double lineHeight, double origin)
    {
        if (this.lineHeight == lineHeight && this.origin == origin &&
            echo.AsSpan().SequenceEqual(echoRows.ToArray()) &&
            matches.AsSpan().SequenceEqual(matchSpans.ToArray()) &&
            current.AsSpan().SequenceEqual(currentSpans.ToArray()))
            return;
        echo = [.. echoRows];
        matches = [.. matchSpans];
        current = [.. currentSpans];
        this.lineHeight = lineHeight;
        this.origin = origin;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (lineHeight <= 0 || Bounds.Width <= 0) return;
        Fill(context, WholeLines(echo), EchoBrush);
        Fill(context, matches, MatchBrush);
        Fill(context, current, CurrentMatchBrush);
    }

    private void Fill(DrawingContext context, IReadOnlyList<BandSpan> spans, IBrush? fill)
    {
        if (fill is null || spans.Count == 0) return;
        foreach (var band in Bands(spans, lineHeight, origin, Bounds.Size))
            context.FillRectangle(fill, band);
    }

    private static IReadOnlyList<BandSpan> WholeLines(IReadOnlyList<int> rows)
    {
        var spans = new BandSpan[rows.Count];
        for (var i = 0; i < rows.Count; i++) spans[i] = new BandSpan(rows[i], 0, double.PositiveInfinity);
        return spans;
    }

    /// <summary>
    /// Rects that intersect the visible area, in layer coordinates; a zero width means "the whole line".
    /// Pure so the geometry is testable.
    /// </summary>
    public static IReadOnlyList<Rect> Bands(IReadOnlyList<BandSpan> spans, double lineHeight, double origin, Size size)
    {
        var bands = new List<Rect>(spans.Count);
        if (lineHeight <= 0 || size.Width <= 0 || size.Height <= 0) return bands;
        foreach (var span in spans)
        {
            var top = origin + span.Row * lineHeight;
            if (top + lineHeight < 0 || top > size.Height) continue;
            var left = Math.Max(0, span.Left);
            if (left > size.Width) continue;
            var width = span.Width <= 0 ? size.Width : Math.Min(span.Width, size.Width - left);
            if (width <= 0) continue;
            bands.Add(new Rect(left, top, width, lineHeight));
        }
        return bands;
    }
}
