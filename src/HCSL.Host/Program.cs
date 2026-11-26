using Avalonia;
using TKCode.Arc;
using HCSL.UI.Shared.Styles.Themes;
#if DEBUG
using Spyglass.Agent;
#endif

namespace HCSL.Host;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .UseSpyglass()
#endif
        .WithInterFont()
        .UseArc(arc => arc.UseArcControls().UseArcBlocks().UseArcSpecial()
            .WithTheme(theme => theme.Appearance(ArcAppearance.System)
                .Accent(ArcAccentPreset.Violet)
                .Preset(ThemeCatalog.ArcPreset)));
}
