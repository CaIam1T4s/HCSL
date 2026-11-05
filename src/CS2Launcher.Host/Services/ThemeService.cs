using Avalonia;
using Avalonia.Styling;
using TKCode.Arc;
using CS2Launcher.Host.Styles.Themes;

namespace CS2Launcher.Host.Services;

public sealed class ThemeService(Application application) : IThemeService
{
    public void Apply(ThemePreference preference)
    {
        ArcTheme.Apply(theme => theme.Preset(ThemeCatalog.ArcPreset).Appearance(preference switch
        {
            ThemePreference.Light => ArcAppearance.Light,
            ThemePreference.Dark => ArcAppearance.Dark,
            _ => ArcAppearance.System
        }));
        application.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
