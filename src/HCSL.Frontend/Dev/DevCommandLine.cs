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
        "status", "instances", "plan", "deploy", "recover", "start", "stop", "exec", "logs", "doctor", "plugins", "import", "gc", "test", "frameworks"
    ];

    private static readonly HashSet<string> ValueOptions = ["--instance", "--preset", "--settings", "--data", "--library", "--after", "--run", "--version", "--id", "--name", "--meta", "--install", "--framework"];
    private static readonly HashSet<string> FlagOptions = ["--json", "--explain", "--dry-run", "--full-copy", "--reflink", "--confirm-write", "--follow", "--allow-low-memory", "--wait", "--yes", "--force", "--enable", "--recover", "--probe", "--no-runtime", "--import-cfg"];

    public const string Usage = """
        hcsl - HCSL developer/test console (Linux)

        Usage: hcsl <command> [options]

        Commands:
          status                 Show workspace, instance, run state and connection addresses.
          instances              List instances (name, UUID, selected preset, port, deployment).
          instances migrate      Move preset home/userdata to the instance layout (explicit, one-time).
          plan                   Validate a deployment without writing anything (add --explain).
          deploy                 Deploy the selected preset (auto reflink/full copy, add --explain).
                                 --recover first recovers an interrupted deployment journal.
          recover                Recover an interrupted deployment journal.
          start | stop           Start/stop the daemon-owned server (start --wait blocks until Running/Failed).
          exec <command...>      Send one CS2 console command to the current run (no RunId needed).
          logs                   Print recent log lines (--follow, --after <sequence>).
          plugins                List available/selected/rejected plugin packages.
          plugins versions       List every installed package version and size.
          plugins remove <id> <version>
                                 Delete an unused package version (needs --yes; refuses presets).
          import <path>          Probe (--probe) or import a folder/archive into plugin-packages.
          frameworks             Check Metamod/CounterStrikeSharp versions; --install <id> downloads
                                 the latest official release, verifies its SHA-256 and imports it.
          doctor                 Check workspace, permissions, packages, leftovers and report fixes.
          gc                     Delete stale .previous-*/.staging-* deployments (needs --yes).
          test mms <dir|tar.gz>  Import a Metamod:Source release as a plugin package (--enable to select it).
          test plugins           Run the plugin/deployment test suites in this repository.

        Options:
          --instance <UUID|name> Instance to target (UUID, exact name or unique prefix; default: the only instance).
          --preset <UUID>        Preset override (default: the instance's selected preset).
          --settings <path>      Settings root override.
          --data <path>          Data root override.
          --library <path>       Library root override.
          --json                 Machine-readable output.
          --explain              Explain what will change and why, before doing it.
          --dry-run              With deploy: same as plan, never writes.
          --full-copy|--reflink  Force the copy mode (default: probe the library filesystem).
          --wait                 start: wait until Running or failed/needs recovery.
          --confirm-write        exec: acknowledge a command that can change server state.
          --version <v>          test mms: package version (default 2.0.0).
          --enable               test mms: add the package to the selected preset.
          --force                test mms/gc: overwrite an existing package version.
          --allow-low-memory     start: allow 3072..4095 MiB free memory.
          --follow --after <n>   logs: stream new lines / start after a sequence.
          --install <id>         frameworks: install metamod, counterstrikesharp or source2mod.
          --no-runtime           frameworks: CounterStrikeSharp without the bundled .NET runtime.
          --import-cfg           import: copy package cfg/** into the selected preset (no overwrite).
          --framework <id>       import: choose swiftlys2 or modsharp for an ambiguous managed plugin.

        Examples:
          hcsl status
          hcsl instances migrate --instance 565653a2-28fe-4fce-8bb6-6e231d55ac61
          hcsl plan --explain
          hcsl deploy --explain
          hcsl exec "meta version"
          hcsl logs --follow
          hcsl doctor
          hcsl plugins --json
          hcsl gc --yes
          hcsl test mms ~/Downloads/mmsource-2.0.0-dev-linux.tar.gz --enable
          hcsl frameworks
          hcsl frameworks --install counterstrikesharp
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
        if (command == "plugins" && positionals.Count > 0
            && !(positionals.Count == 1 && positionals[0] == "versions")
            && !(positionals.Count == 3 && positionals[0] == "remove"))
            throw new DevUsageException("plugins accepts: no arguments, 'versions', or 'remove <id> <version> --yes'.");
        if (command == "instances" && positionals.Count > 0 && !(positionals.Count == 1 && positionals[0] == "migrate"))
            throw new DevUsageException("instances accepts no arguments or 'migrate'. Run: hcsl help");
        if (command is "status" or "start" or "stop" or "plan" or "deploy" or "recover" or "logs" or "doctor" or "gc" or "frameworks" && positionals.Count > 0)
            throw new DevUsageException($"{command} takes no positional arguments (got '{positionals[0]}'). Run: hcsl help");
        return new(command, [.. positionals], options);
    }

    private static string Example(string command) => command switch
    {
        "exec" => "exec \"meta version\"",
        "logs" => "logs --after 0",
        "gc" => "gc --yes",
        "frameworks" => "frameworks --install counterstrikesharp",
        _ => $"{command} --instance 565653a2-28fe-4fce-8bb6-6e231d55ac61"
    };
}
