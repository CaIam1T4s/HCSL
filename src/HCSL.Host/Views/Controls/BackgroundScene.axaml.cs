using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.Host.Views.Controls;

/// <summary>The shared composition recipe for the visible scene and the sidebar's blurred copy.</summary>
public partial class BackgroundScene : UserControl
{
    public static readonly StyledProperty<IImage?> ImageSourceProperty =
        AvaloniaProperty.Register<BackgroundScene, IImage?>(nameof(ImageSource));
    public static readonly StyledProperty<double> StrengthProperty =
        AvaloniaProperty.Register<BackgroundScene, double>(nameof(Strength), 1);

    public IImage? ImageSource { get => GetValue(ImageSourceProperty); set => SetValue(ImageSourceProperty, value); }
    public double Strength { get => GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }

    public BackgroundScene() => InitializeComponent();
}
