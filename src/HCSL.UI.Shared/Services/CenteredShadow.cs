using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HCSL.UI.Shared.Services;

/// <summary>Centers Arc's elevation, preserving its hue/blur/spread, with optional token-relative opacity emphasis.</summary>
public sealed class CenteredShadow : AvaloniaObject
{
    public static readonly AttachedProperty<BoxShadows> SourceProperty =
        AvaloniaProperty.RegisterAttached<CenteredShadow, Border, BoxShadows>("Source");
    public static readonly AttachedProperty<double> StrengthProperty =
        AvaloniaProperty.RegisterAttached<CenteredShadow, Border, double>("Strength", 1);

    static CenteredShadow()
    {
        SourceProperty.Changed.AddClassHandler<Border>((border, _) => Apply(border));
        StrengthProperty.Changed.AddClassHandler<Border>((border, _) => Apply(border));
    }

    public static BoxShadows GetSource(Border border) => border.GetValue(SourceProperty);
    public static void SetSource(Border border, BoxShadows value) => border.SetValue(SourceProperty, value);

    public static double GetStrength(Border border) => border.GetValue(StrengthProperty);
    public static void SetStrength(Border border, double value) => border.SetValue(StrengthProperty, value);

    private static void Apply(Border border) =>
        border.SetCurrentValue(Border.BoxShadowProperty, Center(GetSource(border), GetStrength(border)));

    public static BoxShadows Center(BoxShadows source) => Center(source, 1);

    private static BoxShadows Center(BoxShadows source, double strength)
    {
        strength = double.IsFinite(strength) ? Math.Max(0, strength) : 1;
        if (source.Count == 0) return default;
        var shadows = new BoxShadow[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var shadow = source[i];
            shadow.OffsetX = 0;
            shadow.OffsetY = 0;
            var color = shadow.Color;
            shadow.Color = Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * strength), 0, 255), color.R, color.G, color.B);
            shadows[i] = shadow;
        }
        return new BoxShadows(shadows[0], shadows.Skip(1).ToArray());
    }
}
