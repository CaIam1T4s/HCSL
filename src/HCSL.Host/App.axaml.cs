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
            var daemon = new DaemonClient();
            var baseline = new BaselineController(new BaselineClient(daemon));
            var servers = new HostServerController(
                new ServerController(daemon, action => Dispatcher.UIThread.Post(action), new DaemonBootstrapper()),
                new InstanceClient(daemon), new HCSL.Infrastructure.Storage.LocalDirectoryOpener(), daemon);
            desktop.Exit += (_, _) => { servers.Dispose(); steamCmd.Dispose(); baseline.Dispose(); };
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(new ThemeService(this), steamCmd, servers, baseline) };
            desktop.MainWindow.Opened += (_, _) => { _ = steamCmd.ConnectAsync(); _ = servers.InitializeAsync(); _ = baseline.RefreshStatusAsync(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
