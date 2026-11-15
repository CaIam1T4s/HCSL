using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;

namespace HCSL.UI.Shared.Styles.Themes;

/// <summary>Arc theme preset and the small set of shell resources shared by HOST and CLIENT.</summary>
public static class ThemeCatalog
{
    private sealed record Palette(
        string Background, string Surface, string Raised, string MutedSurface,
        string Sidebar, string Foreground, string Secondary, string Border,
        string BorderSubtle, string BorderStrong, string Disabled, string DisabledText,
        string GradientStart, string GradientEnd, string Scrim, string Shadow);

    private static readonly Palette Light = new(
        "#FFFFFF", "#FFFFFF", "#FFFFFF", "#F8F8F8",
        "#F8F8F8", "#0B0B0B", "#585858", "#E9E9E9",
        "#F3F3F3", "#C4C4C4", "#F3F3F3", "#7D7D7D",
        "#F5F3FF", "#ECE9F8", "#00000052", "0 4 16 0 #12000000");

    private static readonly Palette Dark = new(
        "#141414", "#191919", "#1F1F1F", "#242424",
        "#191919", "#F2F2F2", "#B4B4B4", "#2B2B2B",
        "#222222", "#484848", "#242424", "#8C8C8C",
        "#141414", "#1B1726", "#0000008C", "0 6 22 0 #70000000");

    public static ArcThemePreset ArcPreset { get; } = ArcThemePreset.Create(
        "HCSLArc", preset =>
        {
            preset.Token("Button", "FocusRingRadius", "999");
        }, "HCSLAppearance");

    public static ResourceDictionary CreateResources() => new()
    {
        ["PillRadius"] = new CornerRadius(999),
        ThemeDictionaries =
        {
            [ThemeVariant.Light] = CreatePaletteResources(Light),
            [ThemeVariant.Dark] = CreatePaletteResources(Dark)
        }
    };

    private static ResourceDictionary CreatePaletteResources(Palette palette)
    {
        var resources = new ResourceDictionary
        {
            ["ShellBackground"] = new SolidColorBrush(Color.Parse(palette.Background)),
            ["FloatingSurfaceBackground"] = new SolidColorBrush(Color.Parse(palette.Raised), 0.8),
            ["SidebarBackground"] = new SolidColorBrush(Color.Parse(palette.Sidebar)),
            ["BackgroundScrim"] = new SolidColorBrush(Color.Parse(palette.Scrim)),
            ["BackdropStartColor"] = Color.Parse(palette.GradientStart),
            ["BackdropEndColor"] = Color.Parse(palette.GradientEnd)
        };
        return resources;
    }
}
