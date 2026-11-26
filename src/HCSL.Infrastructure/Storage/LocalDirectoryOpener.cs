using System.Diagnostics;
using HCSL.Application.Abstractions;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

public sealed class LocalDirectoryOpener : ILocalDirectoryOpener
{
    public async Task RevealAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(directory);
        if (!Directory.Exists(path)) throw new IOException("插件目录不存在。");
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            LocalPaths.RejectLink(current.FullName);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add("/select," + path);
                using var explorer = Process.Start(start);
                if (explorer is not null) return;
            }
            else if (OperatingSystem.IsLinux())
            {
                if (await TryFileManagerAsync(path, "ShowItems", cancellationToken)) return;
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        await OpenAsync(Path.GetDirectoryName(path) ?? path, cancellationToken);
    }

    private static async Task<bool> TryFileManagerAsync(string path, string method, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gdbus") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "call", "--session", "--timeout", "3", "--dest", "org.freedesktop.FileManager1",
            "--object-path", "/org/freedesktop/FileManager1", "--method", "org.freedesktop.FileManager1." + method,
            "['" + new Uri(path).AbsoluteUri.Replace("'", "%27", StringComparison.Ordinal) + "']", "''" }) start.ArgumentList.Add(arg);
        try
        {
            using var bus = Process.Start(start);
            if (bus is null) return false;
            // gdbus prints the empty D-Bus reply tuple on success; only the exit code matters here.
            await Task.WhenAll(bus.StandardOutput.ReadToEndAsync(cancellationToken),
                bus.StandardError.ReadToEndAsync(cancellationToken), bus.WaitForExitAsync(cancellationToken));
            return bus.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public async Task OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(directory);
        try
        {
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
                LocalPaths.RejectLink(current.FullName);
        }
        catch (WorkspaceException e) { throw new IOException("HCSL-PRESET-CFG: Linked directories are not opened.", e); }
        if (!Directory.Exists(path)) throw new IOException("HCSL-PRESET-CFG: CFG directory does not exist; refresh the instance catalog.");
        if (OperatingSystem.IsLinux() && await TryFileManagerAsync(path, "ShowFolders", cancellationToken)) return;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(path) { UseShellExecute = true }
            : OperatingSystem.IsLinux()
                ? new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true }
                : throw new IOException("HCSL-PRESET-CFG: File manager integration is supported on Windows/Linux only.");
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(path);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("HCSL-PRESET-CFG: File manager could not be started.");
            // xdg-open normally exits after dispatch. Do not wait on a persistent desktop file manager.
            if (!OperatingSystem.IsWindows())
            {
                var waiting = Stopwatch.StartNew();
                while (!process.HasExited && waiting.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(50, cancellationToken);
                if (process.HasExited && process.ExitCode != 0)
                    throw new IOException("HCSL-PRESET-CFG: File manager rejected the open request.");
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (System.ComponentModel.Win32Exception e)
        { throw new IOException("HCSL-PRESET-CFG: Install/configure a desktop file manager (xdg-open on Linux).", e); }
    }
}
