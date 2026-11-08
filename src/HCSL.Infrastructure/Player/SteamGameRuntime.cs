using System.Diagnostics;
using HCSL.Application.Abstractions;

namespace HCSL.Infrastructure.Player;

/// <summary>Local process hints only; never treats process presence as game readiness.</summary>
public sealed class SteamGameRuntime : IGameRuntime
{
    public static Uri StartUri => new("steam://rungameid/730");
    public Task RequestStartAsync(CancellationToken ct = default) => SteamGameConnector.DispatchAsync(StartUri, ct);
    public Task<GameRunState> ProbeAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return GameRunState.Unknown;
        Process[] processes;
        try { processes = Process.GetProcessesByName("cs2"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        { return GameRunState.Unknown; }
        var unknown = false;
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited) continue;
                    // Don't mistake another logged-on user's game for this user's instance.
                    if (OperatingSystem.IsWindows() && process.SessionId != self.SessionId) continue;
                    if (OperatingSystem.IsLinux())
                    {
                        var proc = "/proc/" + process.Id;
                        var command = File.ReadAllText(Path.Combine(proc, "cmdline")).Split('\0');
                        if (command.Contains("-dedicated", StringComparer.OrdinalIgnoreCase)) continue;
                        var ownUid = File.ReadLines("/proc/self/status").First(x => x.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
                        var uid = File.ReadLines(Path.Combine(proc, "status")).First(x => x.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
                        if (uid != ownUid) continue;
                    }
                    return GameRunState.Running;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
                {
                    // It may have exited while inspecting. Missing access is not proof it stopped.
                    unknown = true;
                }
            }
            return unknown ? GameRunState.Unknown : GameRunState.Stopped;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }, ct);
}
