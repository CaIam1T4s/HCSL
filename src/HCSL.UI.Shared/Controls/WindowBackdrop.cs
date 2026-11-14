using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.UI.Shared.Controls;

/// <summary>Shared wallpaper state for windows whose background also covers the title bar.</summary>
public sealed class WindowBackdrop : AvaloniaObject
{
    public static readonly AttachedProperty<IImage?> ImageSourceProperty =
        AvaloniaProperty.RegisterAttached<WindowBackdrop, Window, IImage?>("ImageSource");
    public static readonly AttachedProperty<double> StrengthProperty =
        AvaloniaProperty.RegisterAttached<WindowBackdrop, Window, double>("Strength", 1);

    public static IImage? GetImageSource(Window window) => window.GetValue(ImageSourceProperty);
    public static void SetImageSource(Window window, IImage? value) => window.SetValue(ImageSourceProperty, value);
    public static double GetStrength(Window window) => window.GetValue(StrengthProperty);
    public static void SetStrength(Window window, double value) => window.SetValue(StrengthProperty, value);
}
