using Avalonia;
using TKCode.Arc;
using CS2Launcher.Host.Styles.Themes;

namespace CS2Launcher.Host;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .UseArc(arc => arc.UseArcControls()
            .WithTheme(theme => theme.Appearance(ArcAppearance.System).Preset(ThemeCatalog.ArcPreset)));
}
