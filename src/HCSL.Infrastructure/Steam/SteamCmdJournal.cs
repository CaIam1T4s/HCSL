using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Steam;

internal sealed record SteamCmdJournal(int Version, int OwnerPid, long OwnerStartTicks, int? ProcessGroup, string? JobName = null, string? PidNamespace = null)
{
    internal static string PathFor(string directory) => Path.Combine(directory, ".hcsl-session.json");
    internal static SteamCmdJournal New(int? group = null, string? jobName = null)
    {
        using var self = Process.GetCurrentProcess();
        return new(1, self.Id, self.StartTime.ToUniversalTime().Ticks, group, jobName,
            OperatingSystem.IsLinux() ? new FileInfo("/proc/self/ns/pid").LinkTarget : null);
    }

    internal void Save(string directory)
    {
        var path = PathFor(directory);
        LocalPaths.RejectLink(path);
        var temporary = path + ".tmp";
        LocalPaths.RejectLink(temporary);
        using (var file = LocalPaths.CreateFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, this);
            file.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal static void RefuseExisting(string directory)
    {
        LocalPaths.RejectLink(PathFor(directory));
        if (File.Exists(PathFor(directory)) || File.Exists(PathFor(directory) + ".tmp"))
            throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "An unfinished session exists. Inspect it, then run --recover; new sessions are blocked.");
    }

    internal static void Recover(string directory, bool verifyOnly = false)
    {
        var path = PathFor(directory);
        LocalPaths.RejectLink(path);
        LocalPaths.RejectLink(path + ".tmp");
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".tmp"))
                throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Incomplete journal needs manual inspection; nothing was removed.");
            return;
        }
        SteamCmdJournal journal;
        try { journal = JsonSerializer.Deserialize<SteamCmdJournal>(File.ReadAllText(path))!; }
        catch (JsonException ex) { throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Invalid journal; nothing was removed.", ex); }
        if (journal is null || journal.Version != 1 || journal.OwnerPid <= 0 || journal.OwnerStartTicks <= 0 || journal.ProcessGroup is <= 1)
            throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Unknown journal; nothing was removed.");
        if (OperatingSystem.IsLinux() && (journal.PidNamespace is null ||
            journal.PidNamespace != new FileInfo("/proc/self/ns/pid").LinkTarget))
            throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "PID namespace differs; recovery cannot safely inspect the previous session.");
        if (OperatingSystem.IsWindows() && (journal.JobName is null || !journal.JobName.StartsWith("Local\\HCSL.SteamCmd.", StringComparison.Ordinal)))
            throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Windows journal has no recognized job identity; nothing was removed.");
        try
        {
            using var owner = Process.GetProcessById(journal.OwnerPid);
            if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == journal.OwnerStartTicks)
                throw new SteamCmdException("HCSL-STEAMCMD-BUSY", "The session owner is still alive.");
        }
        catch (ArgumentException) { /* no such process */ }
        if (OperatingSystem.IsLinux() && journal.ProcessGroup is { } group && GroupExists(group))
            throw new SteamCmdException("HCSL-STEAMCMD-BUSY", "The previous process group is still alive; recovery will not kill it.");
        if (OperatingSystem.IsWindows() && journal.JobName is { } jobName && WindowsProcessJob.IsActive(jobName))
            throw new SteamCmdException("HCSL-STEAMCMD-BUSY", "The previous Windows job is still active.");
        // Windows Job Object is kill-on-close; Linux startup is gated until the group journal is durable.
        if (!verifyOnly)
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    internal static bool GroupExists(int group)
    {
        if (group <= 1) throw new SteamCmdException("HCSL-STEAMCMD-PROCESS", "Invalid process group.");
        if (Kill(-group, 0) == 0)
        {
            // A killed orphan can remain a zombie until init reaps it. Zombies cannot write files.
            // The kernel may also reap the last member between the probe above and this scan, so an
            // empty scan is not evidence that the group is still alive.
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 3 || !int.TryParse(fields[2], out var pg) || pg != group) continue;
                    if (fields[0] is not ("Z" or "X")) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { return true; } // Unknown visibility fails closed.
            }
            return false;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == 3) return false; // ESRCH
        if (error == 1) return true; // EPERM: exists, but not owned
        throw new SteamCmdException("HCSL-STEAMCMD-PROCESS", "Unable to inspect process group.");
    }
    internal static void SignalGroup(int group, int signal)
    {
        if (group <= 1) throw new SteamCmdException("HCSL-STEAMCMD-PROCESS", "Invalid process group.");
        if (Kill(-group, signal) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new SteamCmdException("HCSL-STEAMCMD-PROCESS", "Unable to stop process group.");
    }
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
