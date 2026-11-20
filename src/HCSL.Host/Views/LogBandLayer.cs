using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.Host.Views;

/// <summary>One tinted span: the row it belongs to, and the horizontal span inside that row's line.</summary>
public readonly record struct BandSpan(int Row, double Left, double Width);

/// <summary>
/// Bands behind the log text, drawn instead of hosted as children: adding Border children from a layout callback
/// re-ran layout and looped ("Infinite layout loop detected"), while drawing only invalidates visuals. Positions are
/// taken from the scroll viewport, not from the presenter's own translation, so both scroll axes move the bands.
/// The engine echo of a written command tints a whole line; search matches tint only the matched characters.
/// </summary>
public sealed class LogBandLayer : Control
{
    private double lineHeight, offsetX, offsetY;
    private Size viewport;
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
    /// Bands to draw for the given line height, scroll offset and viewport. Echo rows tint whole lines, match spans
    /// tint the matched characters; only spans inside the viewport are drawn and the layer repaints on a real change.
    /// </summary>
    public void SetBands(IReadOnlyList<int> echoRows, IReadOnlyList<BandSpan> matchSpans, IReadOnlyList<BandSpan> currentSpans,
        double lineHeight, double offsetX, double offsetY, Size viewport)
    {
        if (this.lineHeight == lineHeight && this.offsetX == offsetX && this.offsetY == offsetY && this.viewport == viewport &&
            echo.AsSpan().SequenceEqual(echoRows.ToArray()) &&
            matches.AsSpan().SequenceEqual(matchSpans.ToArray()) &&
            current.AsSpan().SequenceEqual(currentSpans.ToArray()))
            return;
        echo = [.. echoRows];
        matches = [.. matchSpans];
        current = [.. currentSpans];
        this.lineHeight = lineHeight;
        this.offsetX = offsetX;
        this.offsetY = offsetY;
        this.viewport = viewport;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (lineHeight <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return;
        Fill(context, WholeLines(echo), EchoBrush);
        Fill(context, matches, MatchBrush);
        Fill(context, current, CurrentMatchBrush);
    }

    private void Fill(DrawingContext context, IReadOnlyList<BandSpan> spans, IBrush? fill)
    {
        if (fill is null || spans.Count == 0) return;
        foreach (var band in Bands(spans, lineHeight, offsetX, offsetY, viewport))
            context.FillRectangle(fill, band);
    }

    private static IReadOnlyList<BandSpan> WholeLines(IReadOnlyList<int> rows)
    {
        var spans = new BandSpan[rows.Count];
        for (var i = 0; i < rows.Count; i++) spans[i] = new BandSpan(rows[i], 0, double.PositiveInfinity);
        return spans;
    }

    /// <summary>
    /// Pixels of the bands that intersect the viewport, in layer coordinates. A zero width means "the whole line".
    /// Pure so the geometry, both scroll axes and the clipping are testable.
    /// </summary>
    public static IReadOnlyList<Rect> Bands(IReadOnlyList<BandSpan> spans, double lineHeight,
        double offsetX, double offsetY, Size viewport)
    {
        var bands = new List<Rect>(spans.Count);
        if (lineHeight <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return bands;
        foreach (var span in spans)
        {
            var top = span.Row * lineHeight - offsetY;
            if (top + lineHeight <= 0 || top >= viewport.Height) continue;
            var left = span.Left - offsetX;
            var width = span.Width <= 0 ? viewport.Width : span.Width;
            if (left + width <= 0 || left >= viewport.Width) continue;
            // Clip to the viewport so a span cannot bleed over the neighbouring rows or past the right edge.
            var clipLeft = Math.Max(0, left);
            var clipRight = Math.Min(viewport.Width, left + width);
            if (clipRight <= clipLeft) continue;
            // A line half scrolled out keeps only its visible part, so it never covers the neighbour's row.
            var visibleTop = Math.Max(0, top);
            var visibleBottom = Math.Min(viewport.Height, top + lineHeight);
            if (visibleBottom <= visibleTop) continue;
            bands.Add(new Rect(clipLeft, visibleTop, clipRight - clipLeft, visibleBottom - visibleTop));
        }
        return bands;
    }
}
