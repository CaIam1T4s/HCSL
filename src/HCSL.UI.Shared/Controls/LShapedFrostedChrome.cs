using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace HCSL.UI.Shared.Controls;

/// <summary>One continuous Arc glass surface for the title bar and sidebar, with shadows only toward page content.</summary>
public sealed class LShapedFrostedChrome : Panel
{
    public static readonly StyledProperty<BackgroundScene?> SourceProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, BackgroundScene?>(nameof(Source));
    public static readonly StyledProperty<double> TitleBarHeightProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, double>(nameof(TitleBarHeight));
    public static readonly StyledProperty<bool> IsTitleBarVisibleProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, bool>(nameof(IsTitleBarVisible), true);
    public static readonly StyledProperty<double> SidebarWidthProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, double>(nameof(SidebarWidth));
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, double>(nameof(BlurRadius), 18);
    public static readonly StyledProperty<double> TintOpacityProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, double>(nameof(TintOpacity), 0.72);

    public BackgroundScene? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double TitleBarHeight { get => GetValue(TitleBarHeightProperty); set => SetValue(TitleBarHeightProperty, value); }
    public bool IsTitleBarVisible { get => GetValue(IsTitleBarVisibleProperty); set => SetValue(IsTitleBarVisibleProperty, value); }
    public double SidebarWidth { get => GetValue(SidebarWidthProperty); set => SetValue(SidebarWidthProperty, value); }
    public double BlurRadius { get => GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public double TintOpacity { get => GetValue(TintOpacityProperty); set => SetValue(TintOpacityProperty, value); }

    public static readonly StyledProperty<bool> CoversWorkspaceProperty =
        AvaloniaProperty.Register<LShapedFrostedChrome, bool>(nameof(CoversWorkspace));
    public bool CoversWorkspace { get => GetValue(CoversWorkspaceProperty); set => SetValue(CoversWorkspaceProperty, value); }

    private readonly ArcFrostedBackdrop material = new() { Name = "ChromeMaterial" };
    private readonly Canvas shadows = new() { Name = "ChromeShadowLayer", IsHitTestVisible = false };
    private readonly Border topShadow = new() { Background = Brushes.Transparent };
    private readonly Border sideShadow = new() { Background = Brushes.Transparent };

    static LShapedFrostedChrome() => AffectsArrange<LShapedFrostedChrome>(
        TitleBarHeightProperty, SidebarWidthProperty, IsTitleBarVisibleProperty, CoversWorkspaceProperty);

    public LShapedFrostedChrome()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(material);
        Children.Add(shadows);
        shadows.Children.Add(topShadow);
        shadows.Children.Add(sideShadow);
        material.Bind(ArcFrostedBackdrop.SourceProperty, this.GetObservable(SourceProperty));
        material.Bind(ArcFrostedBackdrop.BlurRadiusProperty, this.GetObservable(BlurRadiusProperty));
        material.Bind(ArcFrostedBackdrop.TintOpacityProperty, this.GetObservable(TintOpacityProperty));
        ArcTokens.Bind(topShadow, Border.BoxShadowProperty, ArcDesignTokenKind.ShadowResting);
        ArcTokens.Bind(sideShadow, Border.BoxShadowProperty, ArcDesignTokenKind.ShadowResting);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var height = finalSize.Height;
        var top = IsTitleBarVisible ? Clamp(TitleBarHeight, height) : 0;
        var side = Clamp(SidebarWidth, width);
        var outline = new PolylineGeometry(
        [
            default, new Point(width, 0), new Point(width, top),
            new Point(side, top), new Point(side, height), new Point(0, height)
        ], isFilled: true);
        // Clip after the single full-window material has sampled/blurred its backdrop.
        material.Clip = CoversWorkspace ? null : outline;
        shadows.IsVisible = !CoversWorkspace;
        material.Arrange(new Rect(finalSize));
        // Rectangular shadow casters never draw over the glass, including its internal title/sidebar join.
        shadows.Clip = new RectangleGeometry(new Rect(side, top, width - side, height - top));
        topShadow.IsVisible = top > 0;
        topShadow.Width = width;
        topShadow.Height = top;
        sideShadow.IsVisible = side > 0 && height > top;
        sideShadow.Width = side;
        sideShadow.Height = height - top;
        Canvas.SetTop(sideShadow, top);
        shadows.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private static double Clamp(double value, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, maximum) : 0;
}
