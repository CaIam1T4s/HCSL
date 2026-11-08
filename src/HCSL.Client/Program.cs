using Avalonia;
using TKCode.Arc;
using HCSL.UI.Shared.Styles.Themes;

namespace HCSL.Client;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
        .UseArc(arc => arc.UseArcControls().WithTheme(theme => theme.Appearance(ArcAppearance.System).Preset(ThemeCatalog.ArcPreset)))
        .StartWithClassicDesktopLifetime(args);
}
