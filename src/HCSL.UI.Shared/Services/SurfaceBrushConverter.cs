using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HCSL.UI.Shared.Services;

/// <summary>Fades a surface brush, without changing the opacity of its text or child controls.</summary>
public sealed class SurfaceBrushConverter : IValueConverter
{
    public static SurfaceBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ISolidColorBrush brush ||
            !double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            return value;
        return new SolidColorBrush(brush.Color, brush.Opacity * Math.Clamp(opacity, 0, 1));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
