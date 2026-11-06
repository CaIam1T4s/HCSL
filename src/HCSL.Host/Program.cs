using Avalonia;
using TKCode.Arc;
using HCSL.Host.Styles.Themes;

namespace HCSL.Host;

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
