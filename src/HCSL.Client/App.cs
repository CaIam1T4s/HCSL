using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using HCSL.Frontend;
using HCSL.Infrastructure.Player;
using HCSL.Client.Views;

namespace HCSL.Client;
public sealed class App : Avalonia.Application
{
    public override void Initialize()
    {
        Resources = HCSL.UI.Shared.Styles.Themes.ThemeCatalog.CreateResources();
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://HCSL.Client/"))
        { Source = new Uri("avares://HCSL.UI.Shared/Styles/Shell.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var config = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var root = Path.Combine(config, "hcsl-player");
            var controller = new PlayerClientController(new PlayerServerStore(root, new PlayerSecretStore(Path.Combine(root, "secrets"))), new SteamGameConnector(), new SteamGameRuntime());
            desktop.MainWindow = new PlayerWindow(controller);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
