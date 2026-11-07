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
