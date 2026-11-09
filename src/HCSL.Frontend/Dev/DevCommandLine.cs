namespace HCSL.Frontend.Dev;

/// <summary>A parsed hcsl invocation. Commands are task names, never raw tool flags.</summary>
public sealed record DevInvocation(string Command, string[] Positionals, Dictionary<string, string?> Options)
{
    public bool Has(string name) => Options.ContainsKey(name);
    public string? Get(string name) => Options.TryGetValue(name, out var value) ? value : null;
}

/// <summary>User-facing usage errors: the message must name the option and show a working example.</summary>
public sealed class DevUsageException(string message) : Exception(message);

public static class DevCommandLine
{
    public static readonly string[] Commands =
    [
        "status", "plan", "deploy", "recover", "start", "stop", "exec", "logs", "doctor", "plugins", "gc"
    ];

    private static readonly HashSet<string> ValueOptions = ["--instance", "--preset", "--settings", "--data", "--library", "--after", "--run"];
    private static readonly HashSet<string> FlagOptions = ["--json", "--explain", "--dry-run", "--full-copy", "--reflink", "--confirm-write", "--follow", "--allow-low-memory", "--yes", "--keep"];

    public const string Usage = """
        hcsl - HCSL developer/test console (Linux)

        Usage: hcsl <command> [options]

        Commands:
          status                 Show workspace, instance, run state and connection addresses.
          plan                   Validate a deployment without writing anything (add --explain).
          deploy                 Deploy the selected preset (auto reflink/full copy, add --explain).
          recover                Recover an interrupted deployment journal.
          start | stop           Start/stop the daemon-owned server (start waits with --wait).
          exec <command...>      Send one CS2 console command to the current run (no RunId needed).
          logs                   Print recent log lines (--follow, --after <sequence>).
          plugins                List available/selected/rejected plugin packages.
          doctor                 Check workspace, permissions, packages, leftovers and report fixes.
          gc                     Delete stale .previous-*/.staging-* deployments (needs --yes).

        Options:
          --instance <UUID>      Instance to target (default: the only instance, or an error listing them).
          --preset <UUID>        Preset override (default: the instance's selected preset).
          --settings <path>      Settings root override.
          --data <path>          Data root override.
          --library <path>       Library root override.
          --json                 Machine-readable output.
          --explain              Explain what will change and why, before doing it.
          --dry-run              With deploy: same as plan, never writes.
          --full-copy|--reflink  Force the copy mode (default: probe the library filesystem).
          --confirm-write        exec: acknowledge a command that can change server state.
          --allow-low-memory     start: allow 3072..4095 MiB free memory.
          --follow --after <n>   logs: stream new lines / start after a sequence.
          --keep                 gc/doctor: keep diagnostic folders instead of deleting.

        Examples:
          hcsl status
          hcsl plan --explain
          hcsl deploy --explain
          hcsl exec "meta version"
          hcsl logs --follow
          hcsl doctor
          hcsl plugins --json
          hcsl gc --yes
        """;

    public static DevInvocation Parse(string[] args)
    {
        if (args.Length == 0) throw new DevUsageException("No command. Try: hcsl status");
        if (args[0] is "-h" or "--help" or "help") return new("help", [], []);
        var command = args[0];
        if (!Commands.Contains(command))
            throw new DevUsageException($"Unknown command '{command}'. Commands: {string.Join(", ", Commands)}. Try: hcsl status");

        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help") return new("help", [], []);
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (ValueOptions.Contains(arg))
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new DevUsageException($"{arg} needs a value. Example: hcsl {Example(command)}");
                    if (!options.TryAdd(arg, args[++i])) throw new DevUsageException($"Duplicate option {arg}.");
                }
                else if (FlagOptions.Contains(arg))
                {
                    if (!options.TryAdd(arg, null)) throw new DevUsageException($"Duplicate option {arg}.");
                }
                else throw new DevUsageException($"Unknown option '{arg}'. Run: hcsl help");
                continue;
            }
            positionals.Add(arg);
        }

        if (command == "exec" && positionals.Count == 0)
            throw new DevUsageException("exec needs a command. Example: hcsl exec \"meta version\"");
        if (command is "status" or "start" or "stop" or "plan" or "deploy" or "recover" or "logs" or "doctor" or "plugins" or "gc" && positionals.Count > 0)
            throw new DevUsageException($"{command} takes no positional arguments (got '{positionals[0]}'). Run: hcsl help");
        return new(command, [.. positionals], options);
    }

    private static string Example(string command) => command switch
    {
        "exec" => "exec \"meta version\"",
        "logs" => "logs --after 0",
        "gc" => "gc --yes",
        _ => $"{command} --instance 565653a2-28fe-4fce-8bb6-6e231d55ac61"
    };
}
