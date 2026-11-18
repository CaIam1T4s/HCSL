using System.Text;

namespace HCSL.Application.Diagnostics;

/// <summary>
/// Best-effort local event log for the daemon and the frontends. It records what happened and the stable
/// error code only: never request payloads, console text, credentials or paths beyond the file's own root.
/// A failure to write is swallowed; diagnostics must never break an operation.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object sync = new();
    private static readonly List<string> pending = new();
    private static string? filePath;

    /// <summary>Point both sides at <c>&lt;data-root&gt;/logs</c>; buffered lines are flushed on the first call.</summary>
    public static void Configure(string directory, string fileName)
    {
        lock (sync)
        {
            if (filePath is not null) return;
            try
            {
                Directory.CreateDirectory(directory);
                filePath = Path.Combine(directory, fileName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                pending.Clear();
                return;
            }
            foreach (var line in pending) Append(line);
            pending.Clear();
        }
    }

    public static void Write(string area, string message)
    {
        var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " [" + area + "] " + message;
        lock (sync)
        {
            if (filePath is null)
            {
                if (pending.Count < 200) pending.Add(line);
                return;
            }
            Append(line);
        }
    }

    private static void Append(string line)
    {
        if (filePath is not { } path) return;
        try
        {
            // Rotate once at the size limit so one run cannot grow the file without bound.
            if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
            {
                var previous = path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
