using System.Text;
using System.Text.Json;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Server;

/// <summary>
/// Records which map a run actually loaded. The daemon keeps that fact in memory only while it lives,
/// so a record next to the instance (written under the server's workspace lease) lets a fresh daemon
/// tell "last run loaded de_inferno" apart from "the preset asks for de_inferno". Runs that predate the
/// record are answered from the newest redacted server log instead.
/// </summary>
public static class ServerLastRunStore
{
    private const string MapMarker = "Created physics for ";
    private const int LogScanBytes = 512 * 1024;

    public static string PathFor(string instancesRoot, Guid instanceId) =>
        Path.Combine(instancesRoot, instanceId.ToString("D"), "last-run.json");

    public static void Write(string instancesRoot, Guid instanceId, Guid runId, string map)
    {
        if (!IsMapName(map)) return;
        var path = PathFor(instancesRoot, instanceId);
        LocalPaths.RejectLink(path);
        DeploymentFiles.AtomicJson(path, new ServerLastRun(instanceId, runId, map, DateTimeOffset.UtcNow));
    }

    public static ServerLastRun? Read(string instancesRoot, Guid instanceId)
    {
        var path = PathFor(instancesRoot, instanceId);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return null;
            var record = JsonSerializer.Deserialize<ServerLastRun>(File.ReadAllText(path));
            if (record is null || record.InstanceId != instanceId || !IsMapName(record.Map)) return null;
            return record;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Map of the last run for this instance: the record when present, otherwise the newest server log
    /// (deployments from before the record existed). <paramref name="stamp"/> lets callers cache by
    /// filesystem time instead of re-reading on every status poll.
    /// </summary>
    public static string? ReadMap(string instancesRoot, Guid instanceId, out DateTime stamp)
    {
        stamp = Stamp(instancesRoot, instanceId);
        var record = Read(instancesRoot, instanceId);
        if (record is not null) return record.Map;
        return ReadFromLogs(instancesRoot, instanceId);
    }

    /// <summary>Newest filesystem time of anything that could answer "which map ran last".</summary>
    public static DateTime Stamp(string instancesRoot, Guid instanceId)
    {
        var stamp = TryStamp(PathFor(instancesRoot, instanceId));
        var log = NewestLog(instancesRoot, instanceId);
        return log is not null && log.LastWriteTimeUtc > stamp ? log.LastWriteTimeUtc : stamp;
    }

    private static FileInfo? NewestLog(string instancesRoot, Guid instanceId)
    {
        var directory = Path.Combine(instancesRoot, instanceId.ToString("D"), "logs");
        try
        {
            if (!Directory.Exists(directory)) return null;
            return new DirectoryInfo(directory).EnumerateFiles("server-*.log")
                .Where(file => file.LinkTarget is null && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadFromLogs(string instancesRoot, Guid instanceId)
    {
        var newest = NewestLog(instancesRoot, instanceId);
        if (newest is null) return null;
        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(LogScanBytes, newest.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);
            string? found = null;
            foreach (var line in Encoding.UTF8.GetString(buffer, 0, read).Split('\n'))
                if (MapFrom(line) is { } name) found = name;
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTime TryStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is null ? info.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Parses the launch log line that names the loaded map; anything unsafe yields null.</summary>
    public static string? MapFrom(string line)
    {
        var at = line.IndexOf(MapMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        var name = line[(at + MapMarker.Length)..].Trim();
        return IsMapName(name) ? name : null;
    }

    public static bool IsMapName(string? map)
    {
        if (string.IsNullOrEmpty(map) || map.Length > 240 || map.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '/' or '.'))) return false;
        var parts = map.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or "..")) return false;
        if (parts.Length == 1) return true;
        var offset = parts[0] == "maps" ? 1 : 0;
        return parts.Length == offset + 3 && parts[offset] == "workshop" && parts[offset + 1].All(char.IsAsciiDigit);
    }
}
