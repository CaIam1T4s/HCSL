using HCSL.Core;
using HCSL.Frontend.Dev;

namespace HCSL.ServerTests;

/// <summary>Pure formatting/parsing checks for the hcsl dev console; no daemon, no filesystem.</summary>
public static class DevCliTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(string fragment, Action action)
    {
        try { action(); throw new Exception("Expected a usage error containing: " + fragment); }
        catch (DevUsageException e) when (e.Message.Contains(fragment, StringComparison.Ordinal)) { }
    }

    public static void Run()
    {
        var status = DevCommandLine.Parse(["status"]);
        Check(status.Command == "status" && status.Positionals.Length == 0, "status parse failed.");
        Check(DevCommandLine.Parse(["help"]).Command == "help" && DevCommandLine.Parse(["--help"]).Command == "help", "help parse failed.");

        var id = Guid.NewGuid();
        var plugins = DevCommandLine.Parse(["plugins", "--instance", id.ToString("D"), "--json"]);
        Check(plugins.Get("--instance") == id.ToString("D") && plugins.Has("--json"), "options not captured.");

        var exec = DevCommandLine.Parse(["exec", "meta version", "--confirm-write"]);
        Check(exec.Command == "exec" && string.Join(' ', exec.Positionals) == "meta version" && exec.Has("--confirm-write"), "exec parse failed.");

        var logs = DevCommandLine.Parse(["logs", "--after", "42", "--follow"]);
        Check(logs.Get("--after") == "42" && logs.Has("--follow"), "logs parse failed.");

        Reject("Unknown command", () => DevCommandLine.Parse(["nope"]));
        Reject("Unknown option", () => DevCommandLine.Parse(["status", "--wat"]));
        Reject("needs a value", () => DevCommandLine.Parse(["deploy", "--instance"]));
        Reject("exec needs a command", () => DevCommandLine.Parse(["exec"]));
        Reject("takes no positional", () => DevCommandLine.Parse(["status", "extra"]));
        Reject("Duplicate option", () => DevCommandLine.Parse(["status", "--json", "--json"]));

        var deploy = DevCommandLine.Parse(["deploy", "--dry-run", "--explain"]);
        Check(deploy.Has("--dry-run") && deploy.Has("--explain"), "deploy flags lost.");

        Check(DevHints.For("HCSL-DEPLOY-RECOVERY")!.Contains("hcsl recover"), "recovery hint missing.");
        Check(DevHints.For("HCSL-CONSOLE-STATE")!.Contains("hcsl start"), "console hint missing.");
        Check(DevHints.For("HCSL-NOT-A-CODE") is null, "unknown code must not invent a hint.");
        Check(DevHints.With("boom", "HCSL-DEPLOY-PLUGINS").Contains("next:"), "hint not appended.");

        Check(DevFormat.Bytes(0) == "0 B" && DevFormat.Bytes(1024) == "1.0 KiB", "byte formatting changed.");

        var instanceId = Guid.NewGuid(); var presetId = Guid.NewGuid();
        var plan = new DeploymentPlan(instanceId, 12, presetId, 2, "26445667", "/tmp/runtime", DeploymentCopyMode.Reflink,
            3140, 73L * 1024 * 1024 * 1024, 1024, 2048,
            [new("game/csgo/addons", "/tmp/addons")], ["note one"], "group", "ABCDEF", null, "0123456789ABCDEF");
        var entry = new InstanceEntry(instanceId, new(1, instanceId, 12, "A", 27015, presetId), []);
        var preset = new PresetDefinition(2, presetId, 2, "Default");
        var planText = DevFormat.Plan(plan, entry, preset);
        Check(planText.Contains("Reflink") && planText.Contains("3,140") && planText.Contains("group") && planText.Contains("note one"), "plan projection lost fields.");

        var snapshot = new ServerSnapshot(Guid.NewGuid(), instanceId, ServerState.Running, true, "HCSL-SERVER-MAP-READY",
            true, true, null, 42, false, [], Map: "de_dust2", ConnectionAddresses: ["127.0.0.1"]);
        var statusText = DevFormat.Status(snapshot, entry, preset);
        Check(statusText.Contains("Running") && statusText.Contains("de_dust2") && statusText.Contains("127.0.0.1"), "status projection lost fields.");

        var result = new PluginResult(
            [new("metamod", "Metamod:Source", "2.0.0", "hash", 61, 8 * 1024 * 1024)],
            [new("metamod", "Metamod:Source", "2.0.0", true, true)],
            5, "manifest", [new("broken", "0.1.0", "HCSL-PLUGIN-PACKAGE")]);
        var pluginText = DevFormat.Plugins(result);
        Check(pluginText.Contains("metamod") && pluginText.Contains("rejected   1") && pluginText.Contains("HCSL-PLUGIN-PACKAGE"), "plugin projection lost fields.");
        Check(DevFormat.ExplainPlugins(result).Contains("1 enabled"), "plugin explain summary wrong.");

        Console.WriteLine("PASS dev.cli-parse-hints-and-projections");
    }
}
