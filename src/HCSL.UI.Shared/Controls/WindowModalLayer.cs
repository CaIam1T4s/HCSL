using Avalonia;
using Avalonia.Controls;

namespace HCSL.UI.Shared.Controls;

/// <summary>Window-template slot above client chrome and the VisualLayerManager's transient popups.</summary>
public sealed class WindowModalLayer : AvaloniaObject
{
    public static readonly AttachedProperty<Control?> ContentProperty =
        AvaloniaProperty.RegisterAttached<WindowModalLayer, Window, Control?>("Content");
    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<WindowModalLayer, Window, bool>("IsOpen");

    public static Control? GetContent(Window window) => window.GetValue(ContentProperty);
    public static void SetContent(Window window, Control? value) => window.SetValue(ContentProperty, value);
    public static bool GetIsOpen(Window window) => window.GetValue(IsOpenProperty);
    public static void SetIsOpen(Window window, bool value) => window.SetValue(IsOpenProperty, value);
}
