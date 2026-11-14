using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace HCSL.UI.Shared.Controls;

/// <summary>Arc's static frosted material for shell chrome, without card rims or pointer effects.</summary>
public sealed class ArcFrostedBackdrop : GlassCard
{
    private static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<ArcFrostedBackdrop, IBrush?>("SurfaceBrush");
    public static readonly StyledProperty<BackgroundScene?> SourceProperty =
        AvaloniaProperty.Register<ArcFrostedBackdrop, BackgroundScene?>(nameof(Source));
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<ArcFrostedBackdrop, double>(nameof(BlurRadius), 18,
            coerce: static (_, value) => double.IsFinite(value) ? Math.Max(0, value) : 0);
    public static readonly StyledProperty<double> TintOpacityProperty =
        AvaloniaProperty.Register<ArcFrostedBackdrop, double>(nameof(TintOpacity), 0.72,
            coerce: static (_, value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.72);

    public BackgroundScene? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double BlurRadius { get => GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public double TintOpacity { get => GetValue(TintOpacityProperty); set => SetValue(TintOpacityProperty, value); }
    protected override Type StyleKeyOverride => typeof(GlassCard);
    private BackgroundScene? observed;

    public ArcFrostedBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        CornerRadius = default;
        Padding = default;
        MaxTilt = SheenIntensity = 0;
        // These public token keys affect only this decorative surface, never its foreground controls.
        Resources[GlassPaneTokens.Key(GlassPaneTokenKind.EdgeLight)] = Colors.Transparent;
        Resources[GlassPaneTokens.Key(GlassPaneTokenKind.EdgeShade)] = Colors.Transparent;
        Resources[GlassPaneTokens.Key(GlassPaneTokenKind.Glaze)] = Colors.Transparent;
        Resources[ArcDesignTokens.Key(ArcDesignTokenKind.Border)] = Colors.Transparent;
        Resources[ArcDesignTokens.Key(ArcDesignTokenKind.ShadowRaised)] = default(BoxShadows);
        ArcTokens.Bind(this, SurfaceBrushProperty, ArcDesignTokenKind.Surface);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveSource();
        UpdateMaterial();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (observed is not null) observed.PropertyChanged -= SourceChanged;
        observed = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            GlassBackdrop.SetSource(this, Source);
            if (this.IsAttachedToVisualTree()) ObserveSource();
        }
        if (change.Property == BlurRadiusProperty || change.Property == TintOpacityProperty ||
            change.Property == SurfaceBrushProperty) UpdateMaterial();
    }

    private void ObserveSource()
    {
        if (observed is not null) observed.PropertyChanged -= SourceChanged;
        observed = Source;
        if (observed is not null) observed.PropertyChanged += SourceChanged;
        GlassBackdrop.SetSource(this, Source);
    }

    private void SourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BackgroundScene.ImageSourceProperty && e.Property != BackgroundScene.StrengthProperty) return;
        // Arc caches static backdrops. Refresh after wallpaper/strength edits without a per-frame live loop.
        GlassBackdrop.SetSource(this, null);
        GlassBackdrop.SetSource(this, Source);
    }

    private void UpdateMaterial()
    {
        var surface = (GetValue(SurfaceBrushProperty) as ISolidColorBrush)?.Color ?? Colors.Transparent;
        var tint = Color.FromArgb((byte)Math.Round(255 * TintOpacity), surface.R, surface.G, surface.B);
        Resources[GlassPaneTokens.Key(GlassPaneTokenKind.BlurRadius)] = BlurRadius;
        Resources[GlassPaneTokens.Key(GlassPaneTokenKind.Tint)] = tint;
        Resources[GlassPaneTokens.Key(GlassPaneTokenKind.FallbackTint)] = tint;
    }
}
