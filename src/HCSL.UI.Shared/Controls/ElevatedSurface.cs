using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace HCSL.UI.Shared.Controls;

/// <summary>Decorative, token-based elevation behind a control; never fades its foreground.</summary>
public sealed class ElevatedSurface : Grid
{
    public ElevatedSurface()
    {
        Classes.Add("elevated-surface");
        Children.Add(Shadow("surface-resting-shadow", 1));
        Children.Add(Shadow("surface-active-shadow", 0));
    }

    public ElevatedSurface(Control content) : this() => Children.Add(content);

    private static Border Shadow(string state, double opacity)
    {
        var border = new Border { Background = Brushes.Transparent, IsHitTestVisible = false };
        border.SetValue(OpacityProperty, opacity, BindingPriority.Style);
        border.Classes.Add("surface-shadow");
        border.Classes.Add(state);
        return border;
    }
}
