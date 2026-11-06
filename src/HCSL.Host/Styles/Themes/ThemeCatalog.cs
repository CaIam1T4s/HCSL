using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;

namespace HCSL.Host.Styles.Themes;

/// <summary>Single palette source for the shell resources and native Arc theme presets.</summary>
public static class ThemeCatalog
{
    private sealed record Palette(
        string Background, string Surface, string Raised, string MutedSurface,
        string Sidebar, string Foreground, string Secondary, string Border,
        string Accent, string AccentSoft, string Disabled, string DisabledText,
        string GradientStart, string GradientEnd, string Scrim, string Shadow);

    private static readonly Palette WarmLight = new(
        "#E7E0D4", "#F0EADF", "#F3EDE3", "#DFD6C8",
        "#E5DDCF", "#302D28", "#696157", "#CFC4B3",
        "#9C471A", "#EBD6BF", "#D9D0C2", "#736A60",
        "#CDD8D5", "#C4A789", "#05B8A17D", "0 5 18 0 #382D2115");

    private static readonly Palette Dark = new(
        "#171C24", "#222934", "#293240", "#202630",
        "#202630", "#EDF1F7", "#A6B2C2", "#3B4655",
        "#FFB47B", "#463226", "#3B4553", "#C0CAD7",
        "#1B3444", "#594133", "#140D1724", "0 6 22 0 #70000000");

    public static ArcThemePreset ArcPreset { get; } = ArcThemePreset.Create(
        "HCSLWarm", preset =>
        {
            // Override only the light appearance; dark retains Arc's native palette.
            preset.ForLight(light =>
            {
                foreach (var (token, color) in LightArcColors()) light.Token(token, color);
            });
            preset.Token("Button", "FocusRingRadius", "999");
        }, "HCSLAppearance");

    private static Dictionary<string, string> LightArcColors() => new()
    {
        ["Background"] = WarmLight.Background,
        ["Surface"] = WarmLight.Surface,
        ["SurfaceRaised"] = WarmLight.Raised,
        ["SurfaceMuted"] = WarmLight.MutedSurface,
        ["Foreground"] = WarmLight.Foreground,
        ["TextSecondary"] = WarmLight.Secondary,
        ["TextMuted"] = WarmLight.Secondary,
        ["Border"] = WarmLight.Border,
        ["BorderSubtle"] = WarmLight.MutedSurface,
        ["BorderStrong"] = WarmLight.Secondary,
        ["Accent"] = WarmLight.Accent,
        ["AccentStrong"] = WarmLight.Accent,
        ["AccentSubtle"] = WarmLight.AccentSoft,
        ["AccentForeground"] = WarmLight.Raised,
        ["FocusRing"] = WarmLight.Accent,
        ["ControlFill"] = WarmLight.Surface,
        ["ControlTrack"] = WarmLight.MutedSurface,
        ["ControlTrackHover"] = WarmLight.Border,
        ["ControlGlyph"] = WarmLight.Foreground,
        ["ControlThumb"] = WarmLight.Raised
    };

    public static ResourceDictionary CreateResources() => new()
    {
        ["PillRadius"] = new CornerRadius(999),
        ThemeDictionaries =
        {
            [ThemeVariant.Light] = CreatePaletteResources(WarmLight),
            [ThemeVariant.Dark] = CreatePaletteResources(Dark)
        }
    };

    private static ResourceDictionary CreatePaletteResources(Palette palette)
    {
        var resources = new ResourceDictionary();
        var brushes = new Dictionary<string, string>
        {
            ["ShellBackground"] = palette.Background,
            ["PanelBackground"] = palette.Surface,
            ["SidebarBackground"] = palette.Sidebar,
            ["PrimaryText"] = palette.Foreground,
            ["MutedText"] = palette.Secondary,
            ["Outline"] = palette.Border,
            ["AccentBrush"] = palette.Accent,
            ["AccentSoft"] = palette.AccentSoft,
            ["DisabledBackground"] = palette.Disabled,
            ["DisabledText"] = palette.DisabledText,
            ["BackgroundScrim"] = palette.Scrim,
            ["OnDangerText"] = "#FFFFFF"
        };
        foreach (var (key, value) in brushes) resources[key] = new SolidColorBrush(Color.Parse(value));
        resources["BackdropStartColor"] = Color.Parse(palette.GradientStart);
        resources["BackdropEndColor"] = Color.Parse(palette.GradientEnd);
        resources["LaunchShadow"] = BoxShadows.Parse(palette.Shadow);
        return resources;
    }
}
