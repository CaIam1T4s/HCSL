using System.Globalization;

namespace HCSL.Frontend;

/// <summary>Identity of the bundled daemon payload. The daemon reports the write time of the assembly it is
/// running; comparing it with the copy this frontend would launch is the only reliable signal that a stale
/// process is serving the pipe after a rebuild.</summary>
public static class DaemonPayload
{
    public const string RelativeDirectory = "daemon";

    public static string Directory(string? payloadDirectory = null) =>
        Path.GetFullPath(payloadDirectory ?? Path.Combine(AppContext.BaseDirectory, RelativeDirectory));

    /// <summary>Write time of the bundled daemon assembly, or null when the payload is incomplete.</summary>
    public static DateTime? Bundled(string? payloadDirectory = null)
    {
        try
        {
            var assembly = Path.Combine(Directory(payloadDirectory), "HCSL.Daemon.dll");
            return File.Exists(assembly) ? File.GetLastWriteTimeUtc(assembly) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Content hash of the bundled daemon assembly. The daemon reports the same hash of the file it runs, so a
    /// rebuild that changes nothing compares equal even though the file was written again.
    /// </summary>
    public static string? BundledHash(string? payloadDirectory = null)
    {
        try
        {
            var assembly = Path.Combine(Directory(payloadDirectory), "HCSL.Daemon.dll");
            if (!File.Exists(assembly)) return null;
            using var stream = File.OpenRead(assembly);
            return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Compares the running daemon's reported identity with the payload this frontend would launch. Hash
    /// identities are compared exactly; a timestamp from an older daemon keeps the previous rule.
    /// </summary>
    public static bool IsStale(string? runningBuild, string? payloadDirectory = null)
    {
        if (runningBuild is { Length: > 0 } && runningBuild.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return BundledHash(payloadDirectory) is { } bundled
                && !string.Equals(runningBuild, bundled, StringComparison.OrdinalIgnoreCase);
        return IsStale(Parse(runningBuild), Bundled(payloadDirectory));
    }

    public static DateTime? Parse(string? stamp) =>
        DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime() : null;

    /// <summary>A running daemon is stale when it is older than the bundled binary. Equal stamps are current;
    /// an unknown stamp on either side must never trigger a replacement.</summary>
    public static bool IsStale(DateTime? running, DateTime? bundled) =>
        running is { } r && bundled is { } b && r < b;

    /// <summary>Filesystem timestamps can land inside the same tick window on coarse filesystems.</summary>
    public static bool IsStale(DateTime? running, DateTime? bundled, TimeSpan tolerance) =>
        running is { } r && bundled is { } b && r + tolerance < b;
}
