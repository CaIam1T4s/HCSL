using Avalonia;
using Avalonia.Threading;
using HCSL.Frontend;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HCSL.Host.Services;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using HCSL.UI.Shared.Styles.Themes;

namespace HCSL.Host;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        Resources = ThemeCatalog.CreateResources();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var steamCmd = new SteamCmdController(new DaemonClient(), action => Dispatcher.UIThread.Post(action), new DaemonBootstrapper());
            var servers = new HostServerController(
                new ServerController(new DaemonClient(), action => Dispatcher.UIThread.Post(action), new DaemonBootstrapper()),
                new InstanceClient(new DaemonClient()), new HCSL.Infrastructure.Storage.LocalDirectoryOpener());
            desktop.Exit += (_, _) => { servers.Dispose(); steamCmd.Dispose(); };
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(new ThemeService(this), steamCmd, servers) };
            desktop.MainWindow.Opened += (_, _) => { _ = steamCmd.ConnectAsync(); _ = servers.InitializeAsync(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
