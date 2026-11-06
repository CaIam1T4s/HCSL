using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Steam;
using HCSL.Infrastructure.Storage;

// Internal Windows launch gate: no SteamCMD code runs until assigned to a kill-on-close job.
if (args is ["--process-gate"]) return SteamCmdProcessGate.Run();
if (args.Contains("--help"))
{
    Console.WriteLine("HCSL SteamCMD tool and CS2 installer.\n" +
        "--settings <absolute> --data <absolute> --library <absolute>\n" +
        "[--executable <absolute>] [--bootstrap] [--install | --validate] [--account <name>] [--timeout-seconds 1..86400] [--recover]\n" +
        "Defaults use platform workspace paths. Bootstrap downloads only the official SteamCMD tool.\n" +
        "Account challenges require an interactive console. Ctrl+C cancels; recovery never kills processes.");
    return 0;
}
try
{
    var options = new Dictionary<string, string>();
    var flags = new HashSet<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] is "--bootstrap" or "--recover" or "--install" or "--validate")
        {
            if (!flags.Add(args[i])) throw new SteamCmdException("HCSL-ARGUMENT", "Duplicate option.");
            continue;
        }
        if (args[i] is not ("--settings" or "--data" or "--library" or "--executable" or "--account" or "--timeout-seconds") ||
            i + 1 == args.Length || args[i + 1].StartsWith("--") || !options.TryAdd(args[i], args[++i]))
            throw new SteamCmdException("HCSL-ARGUMENT", "Invalid arguments. Use --help.");
    }
    if ((flags.Contains("--install") && flags.Contains("--validate")) ||
        (flags.Contains("--recover") && (flags.Contains("--install") || flags.Contains("--validate"))))
        throw new SteamCmdException("HCSL-ARGUMENT", "Choose one operation: probe, install, validate or recover.");
    bool install = flags.Contains("--install") || flags.Contains("--validate");
    var defaults = LocalPaths.Defaults(options.GetValueOrDefault("--settings"));
    var workspace = new WorkspaceRequest(options.GetValueOrDefault("--settings", defaults.SettingsRoot),
        options.GetValueOrDefault("--data", defaults.DataRoot), options.GetValueOrDefault("--library", defaults.LibraryRoot));
    var service = new SteamCmdService(typeof(SteamCmdCheckEntry).Assembly.Location);
    if (flags.Contains("--recover"))
    {
        service.Recover(workspace, options.GetValueOrDefault("--executable"));
        Console.WriteLine("Recovery check completed; no process was killed.");
        return 0;
    }
    int seconds = install ? 21600 : 600;
    if (options.TryGetValue("--timeout-seconds", out var value) && (!int.TryParse(value, out seconds) || (seconds < 1 || seconds > (install ? 86400 : 3600))))
        throw new SteamCmdException("HCSL-ARGUMENT", "Probe timeout must be 1..3600 seconds; installation timeout 1..86400 seconds.");
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.CancelKeyPress += handler;
    try
    {
        var session = new SteamCmdProbeRequest(workspace, options.GetValueOrDefault("--executable"),
            flags.Contains("--bootstrap"), options.GetValueOrDefault("--account"), TimeSpan.FromSeconds(seconds));
        var events = new ConsoleEvents();
        var interaction = options.ContainsKey("--account") ? new ConsoleInteraction() : null;
        var result = install
            ? await service.InstallAsync(new(session, flags.Contains("--validate") ? SteamCmdInstallMode.Validate : SteamCmdInstallMode.InstallOrUpdate), events, interaction, stop.Token)
            : await service.ProbeAsync(session, events, interaction, stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, SteamCmdCheckEntry.OutputJson));
        return result.Stage switch { SteamCmdStage.Succeeded => 0, SteamCmdStage.Cancelled => 130,
            SteamCmdStage.TimedOut => 124, SteamCmdStage.RecoveryRequired => 4, _ => 2 };
    }
    finally { Console.CancelKeyPress -= handler; }
}
catch (WorkspaceException ex) { Console.Error.WriteLine($"{ex.Code}: {ex.Message}"); return ex.Code == "HCSL-LOCK" ? 3 : 2; }
catch (SteamCmdException ex) { Console.Error.WriteLine($"{ex.Code}: {ex.Message}"); return ex.Code == "HCSL-STEAMCMD-BUSY" ? 3 : 4; }
catch (OperationCanceledException) { return 130; }

public static class SteamCmdCheckEntry
{
    internal static JsonSerializerOptions OutputJson { get; } = new() { Converters = { new JsonStringEnumConverter() } };
}
internal sealed class ConsoleEvents : IProgress<SteamCmdEvent>
{
    public void Report(SteamCmdEvent value) => Console.WriteLine(JsonSerializer.Serialize(value, SteamCmdCheckEntry.OutputJson));
}
internal sealed class ConsoleInteraction : ISteamCmdInteraction
{
    public async ValueTask<string> RespondAsync(SteamCmdChallenge challenge, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
            throw new SteamCmdException("HCSL-STEAMCMD-INTERACTION", "Account input requires an interactive terminal.");
        Console.Error.Write(challenge.Kind == SteamCmdChallengeKind.Password ? "Steam password: " : "Steam Guard code: ");
        var input = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable) { await Task.Delay(50, cancellationToken); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return input.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (input.Length > 0) input.Length--; }
            else if (!char.IsControl(key.KeyChar) && input.Length < 512) input.Append(key.KeyChar);
        }
    }
}
