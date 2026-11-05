using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CS2Launcher.Host.Views.Controls;

/// <summary>Application-local frosted background; never blurs its foreground or the desktop.</summary>
public partial class BackgroundGlass : UserControl
{
    public static readonly StyledProperty<Visual?> SourceProperty =
        AvaloniaProperty.Register<BackgroundGlass, Visual?>(nameof(Source));
    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<BackgroundGlass, double>(nameof(OffsetY));
    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<BackgroundGlass, double>(nameof(BlurRadius), 18);
    public static readonly StyledProperty<double> TintOpacityProperty =
        AvaloniaProperty.Register<BackgroundGlass, double>(nameof(TintOpacity), 0.72);

    public Visual? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double OffsetY { get => GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double BlurRadius { get => GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public double TintOpacity { get => GetValue(TintOpacityProperty); set => SetValue(TintOpacityProperty, value); }

    public BackgroundGlass()
    {
        InitializeComponent();
        UpdateBlur();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlurRadiusProperty && BlurredSurface is not null) UpdateBlur();
    }

    private void UpdateBlur() => BlurredSurface.Effect = BlurRadius > 0
        ? new BlurEffect { Radius = BlurRadius } : null;
}
