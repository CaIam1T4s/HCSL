namespace HCSL.Core;

/// <summary>
/// Recognizes the engine's own echo of a command HCSL wrote to its standard input. CS2 repeats the line in its
/// output (for example before "Unknown command"), which is the line the console highlights; HCSL never adds one.
/// </summary>
public static class ConsoleEcho
{
    /// <summary>Leading characters the engine may put before the echoed text.</summary>
    private static readonly char[] Leaders = [']', '>', ' ', '\t'];

    /// <summary>How long an echo may take to come back before the command is forgotten.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    /// <summary>Compares an output line with a written command, ignoring the engine's prompt characters.</summary>
    public static bool Matches(string line, string command) =>
        string.Equals(Normalize(line), Normalize(command), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text) => text.Trim().TrimStart(Leaders).TrimEnd();
}

/// <summary>
/// Remembers commands the console just wrote and consumes the engine's echo of each one, so the log line can be
/// marked exactly once and a later identical line (a real replay or a player chat) stays unmarked.
/// </summary>
public sealed class ConsoleEchoWindow(int capacity = 4)
{
    private readonly List<(string Command, DateTimeOffset At)> pending = [];

    public int Pending => pending.Count;

    /// <summary>Remembers a written command; older entries beyond the window or the capacity are dropped.</summary>
    public void Record(string command, DateTimeOffset at)
    {
        lock (pending)
        {
            Expire(at);
            if (pending.Count >= Math.Max(1, capacity)) pending.RemoveAt(0);
            pending.Add((command, at));
        }
    }

    /// <summary>Takes the oldest command this line echoes. A line that echoes nothing leaves the queue alone.</summary>
    public bool TryConsume(string line, DateTimeOffset at, out string command)
    {
        lock (pending)
        {
            Expire(at);
            for (var i = 0; i < pending.Count; i++)
            {
                if (!ConsoleEcho.Matches(line, pending[i].Command)) continue;
                command = pending[i].Command;
                pending.RemoveAt(i);
                return true;
            }
        }
        command = "";
        return false;
    }

    private void Expire(DateTimeOffset at)
    {
        for (var i = pending.Count - 1; i >= 0; i--)
            if (at - pending[i].At > ConsoleEcho.Window) pending.RemoveAt(i);
    }
}
