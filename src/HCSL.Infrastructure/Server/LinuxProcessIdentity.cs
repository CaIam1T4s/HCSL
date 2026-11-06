namespace HCSL.Infrastructure.Server;

// Linux /proc starttime is a stable kernel tick count. Process.StartTime converts boot time
// to wall clock independently in each .NET process and can differ by fractions of a millisecond.
internal static class LinuxProcessIdentity
{
    internal static long StartTicks(int pid)
    {
        var text = File.ReadAllText($"/proc/{pid}/stat");
        var fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0] is "Z" or "X") return 0;
        return long.Parse(fields[19]);
    }
}
