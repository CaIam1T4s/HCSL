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
            var steamCmd = new SteamCmdController(new DaemonClient(), action => Dispatcher.UIThread.Post(action), new DaemonBootstrapper());
            HCSL.Application.Diagnostics.DiagnosticsLog.Configure(
                System.IO.Path.Combine(HCSL.Infrastructure.Storage.LocalPaths.Defaults().DataRoot, "logs"), "host.log");
            HCSL.Application.Diagnostics.DiagnosticsLog.Write("host", "start pid=" + Environment.ProcessId + " v" + ProductVersion.Current);
            var daemon = new DaemonClient();
            var baseline = new BaselineController(new BaselineClient(daemon,
                () => new(steamCmd.SettingsRoot, steamCmd.DataRoot, steamCmd.LibraryRoot)));
            var servers = new HostServerController(
                new ServerController(daemon, action => Dispatcher.UIThread.Post(action), new DaemonBootstrapper()),
                new InstanceClient(daemon), new HCSL.Infrastructure.Storage.LocalDirectoryOpener(), daemon);
            desktop.Exit += (_, _) =>
            {
                HCSL.Application.Diagnostics.DiagnosticsLog.Write("host", "exit");
                servers.Dispose(); steamCmd.Dispose(); baseline.Dispose();
            };
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(new ThemeService(this), steamCmd, servers, baseline) };
            if (desktop.Args is ["--package-smoke"])
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    try
                    {
                        await CheckPackagedDaemonAsync();
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

    private static async Task CheckPackagedDaemonAsync()
    {
        var pipe = $"hcsl-package-smoke-{Guid.NewGuid():N}";
        await new DaemonBootstrapper(pipe).EnsureRunningAsync();
        var client = new DaemonClient(pipe);
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
