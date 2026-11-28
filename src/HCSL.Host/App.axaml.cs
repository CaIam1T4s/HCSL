using Avalonia;
using Avalonia.Threading;
using HCSL.Frontend;
using HCSL.Core;
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
            var smoke = desktop.Args is ["--package-smoke"] ? new PackageTableSmoke() : null;
            var bootstrapper = new DaemonBootstrapper(smoke?.Pipe);
            IDaemonClient daemon = smoke is null ? new DaemonClient() : smoke;
            var steamCmd = new SteamCmdController(daemon, action => Dispatcher.UIThread.Post(action), bootstrapper);
            HCSL.Application.Diagnostics.DiagnosticsLog.Configure(
                HCSL.Infrastructure.Storage.LocalPaths.DiagnosticsDirectory(), "host.log");
            HCSL.Application.Diagnostics.DiagnosticsLog.Write("host", "start pid=" + Environment.ProcessId + " v" + ProductVersion.Current);
            var baseline = new BaselineController(new BaselineClient(daemon,
                () => new(steamCmd.SettingsRoot, steamCmd.DataRoot, steamCmd.LibraryRoot)));
            var servers = new HostServerController(
                new ServerController(daemon, action => Dispatcher.UIThread.Post(action), bootstrapper),
                new InstanceClient(daemon), new HCSL.Infrastructure.Storage.LocalDirectoryOpener(), daemon);
            desktop.Exit += (_, _) =>
            {
                HCSL.Application.Diagnostics.DiagnosticsLog.Write("host", "exit");
                servers.Dispose(); steamCmd.Dispose(); baseline.Dispose(); smoke?.Dispose();
            };
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(new ThemeService(this), steamCmd, servers, baseline) };
            if (smoke is not null)
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    try
                    {
                        await bootstrapper.EnsureRunningAsync();
                        await CheckPackagedDaemonAsync(smoke);
                        await smoke.CheckTablesAsync(desktop.MainWindow!, servers);
                        desktop.Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        HCSL.Application.Diagnostics.DiagnosticsLog.Write("host", "package smoke failed: " + error);
                        Console.Error.WriteLine(error);
                        desktop.Shutdown(1);
                    }
                };
            else
                desktop.MainWindow.Opened += (_, _) => { _ = steamCmd.ConnectAsync(); _ = servers.InitializeAsync(); _ = baseline.RefreshStatusAsync(); };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CheckPackagedDaemonAsync(IDaemonClient client)
    {
        using var steam = new SteamCmdController(client, action => action());
        await steam.ConnectAsync();
        await steam.RefreshContentStatusAsync();
        if (!steam.Connected || steam.ContentInstalled is null)
            throw new IOException("Package smoke could not read the managed content status.");

        await new InstanceClient(client).ExecuteAsync(new(InstanceAction.List));
        await new ServerClient(client).SendAsync(new(ServerAction.Status));
        try { await new BaselineClient(client).StatusAsync(); }
        catch (IOException error) when (error.Message.StartsWith("HCSL-BASELINE-NOT-READY:", StringComparison.Ordinal))
        {
            // A clean runner has no managed CS2 installation.
        }
    }
}
