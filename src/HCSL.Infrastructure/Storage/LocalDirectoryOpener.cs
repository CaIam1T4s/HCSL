using System.Diagnostics;
using HCSL.Application.Abstractions;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

public sealed class LocalDirectoryOpener : ILocalDirectoryOpener
{
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
