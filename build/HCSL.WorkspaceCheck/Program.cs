using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Storage;

if (args.Contains("--help"))
{
    Console.WriteLine("HCSL workspace foundation check (not a game server or daemon).\n" +
        "--settings <absolute path> --data <absolute path> --library <absolute path> [--hold]\n" +
        "Omitted data/library paths use saved settings first, then platform defaults. --hold retains ownership until Enter, EOF or Ctrl+C.\n" +
        "Creates workspace directories and markers; existing nonempty unmarked roots are refused.");
    return 0;
}
try
{
    var values = new Dictionary<string, string>();
    bool hold = false;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--hold") { hold = true; continue; }
        if (args[i] is not ("--settings" or "--data" or "--library") || i + 1 == args.Length ||
            args[i + 1].StartsWith("--") || !values.TryAdd(args[i], args[++i]))
            throw new WorkspaceException("HCSL-ARGUMENT", "Unknown, duplicate or incomplete argument. Use --help.");
    }
    var defaults = LocalPaths.Defaults(values.GetValueOrDefault("--settings"));
    using var lease = new LocalWorkspaceManager().Acquire(new(
        values.GetValueOrDefault("--settings", defaults.SettingsRoot),
        values.GetValueOrDefault("--data", defaults.DataRoot),
        values.GetValueOrDefault("--library", defaults.LibraryRoot)));
    Console.WriteLine(JsonSerializer.Serialize(new { Status = "Acquired", lease.Layout, lease.Identity, lease.Capacity }));
    Console.Out.Flush();
    if (hold)
    {
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        Console.CancelKeyPress += handler;
        try
        {
            _ = Task.Run(() => { Console.ReadLine(); stop.TrySetResult(); });
            stop.Task.GetAwaiter().GetResult();
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    return 0;
}
catch (WorkspaceException ex)
{
    Console.Error.WriteLine($"{ex.Code}: {ex.Message}");
    return ex.Code == "HCSL-LOCK" ? 3 : 2;
}

/// <summary>Assembly locator used by the cross-process integration checks.</summary>
public static class WorkspaceCheckEntry { }
