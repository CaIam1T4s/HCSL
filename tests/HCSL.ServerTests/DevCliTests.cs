using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
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
        var instanceId = Guid.NewGuid(); var presetId = Guid.NewGuid();
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
        Check(DevCommandLine.Parse(["deploy", "--recover"]).Has("--recover"), "deploy --recover flag lost.");
        var start = DevCommandLine.Parse(["start", "--wait", "--allow-low-memory"]);
        Check(start.Has("--wait") && start.Has("--allow-low-memory"), "start flags lost.");
        // Every flag the usage text advertises must parse; regression for --wait being documented but rejected.
        foreach (var flag in new[] { "--json", "--explain", "--dry-run", "--full-copy", "--reflink", "--confirm-write", "--follow", "--allow-low-memory", "--wait", "--yes", "--force", "--enable" })
            Check(DevCommandLine.Parse(["doctor", flag]).Has(flag), "documented flag rejected: " + flag);

        Check(DevHints.For("HCSL-DEPLOY-RECOVERY")!.Contains("deploy --recover"), "recovery hint missing.");
        Check(DevHints.For("HCSL-CONSOLE-STATE")!.Contains("hcsl start"), "console hint missing.");
        Check(DevHints.For("HCSL-DEPLOY-PLUGINS")!.Contains("hcsl doctor"), "plugin deploy hint missing.");

        var import = DevCommandLine.Parse(["test", "mms", "/tmp/mms.tar.gz", "--version", "2.0.0", "--enable", "--force"]);
        Check(import.Command == "test" && import.Positionals[1] == "/tmp/mms.tar.gz"
            && import.Get("--version") == "2.0.0" && import.Has("--enable") && import.Has("--force"), "test mms parse failed.");
        Check(DevCommandLine.Parse(["test", "plugins"]).Positionals[0] == "plugins", "test plugins parse failed.");

        // The desktop/TUI share these path projections; a missing directory is reported by the caller.
        var fake = new FakeDaemon(new("/s", "/d", "/l"));
        var client = new InstanceClient(fake);
        var addons = client.PresetDirectoryAsync(instanceId, presetId, "addons").GetAwaiter().GetResult();
        Check(addons == Path.Combine("/d", "instances", instanceId.ToString("D"), "presets", presetId.ToString("D"), "addons"), "preset addons path wrong.");
        Check(client.RuntimeDirectoryAsync(instanceId).GetAwaiter().GetResult() == Path.Combine("/l", "runtime", instanceId.ToString("D")), "runtime path wrong.");
        try { client.PresetDirectoryAsync(instanceId, presetId, "../escape").GetAwaiter().GetResult(); throw new Exception("Unknown preset directory accepted."); }
        catch (IOException e) when (e.Message.Contains("HCSL-PRESET-DIR", StringComparison.Ordinal)) { }
        Check(DevHints.For("HCSL-NOT-A-CODE") is null, "unknown code must not invent a hint.");
        Check(DevHints.With("boom", "HCSL-DEPLOY-PLUGINS").Contains("next:"), "hint not appended.");

        Check(DevFormat.Bytes(0) == "0 B" && DevFormat.Bytes(1024) == "1.0 KiB", "byte formatting changed.");

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

        // Ordered plugin draft: array order is load order, so Move must swap and SaveCommand must keep it.
        var draft = new PluginDraft();
        draft.Load(new PluginResult(
            [new("alpha", "Alpha", "1.0.0", "hash", 1, 1), new("beta", "Beta", "1.0.0", "hash", 1, 1), new("gamma", "Gamma", "1.0.0", "hash", 1, 1)],
            [new("alpha", "Alpha", "1.0.0", true, true), new("beta", "Beta", "1.0.0", true, true), new("gamma", "Gamma", "1.0.0", true, true)],
            1, "manifest"));
        Check(draft.Move(0, 1) == 1 && draft.Plugins.Select(p => p.Id).SequenceEqual(["beta", "alpha", "gamma"]), "move down did not swap.");
        Check(draft.Move(2, 1) == 2, "move past the end must clamp.");
        var command = draft.SaveCommand(Guid.NewGuid(), Guid.NewGuid(), 1);
        Check(command.Plugins!.Select(p => p.Id).SequenceEqual(["beta", "alpha", "gamma"]), "draft order lost in SaveCommand.");

        // IPC-safe projections: package hashes are not serialized and truncation reports the total.
        Check(!JsonSerializer.Serialize(new PluginPackageInfo("alpha", "Alpha", "1.0.0", "deadbeef", 1, 1)).Contains("Hash", StringComparison.Ordinal),
            "package hash must not be serialized into the list response.");
        var truncated = new PluginResult([new("alpha", "Alpha", "1.0.0", "hash", 1, 1)],
            [new("alpha", "Alpha", "1.0.0", true, true)], 1, "manifest", null, 7);
        Check(DevFormat.Plugins(truncated).Contains("1 of 7", StringComparison.Ordinal), "truncated catalog total missing.");

        // Instance/preset selectors: UUID, exact name, unique prefix; ambiguous selections list candidates.
        var presetA = new PresetDefinition(1, Guid.NewGuid(), 1, "Default");
        var presetB = new PresetDefinition(1, Guid.NewGuid(), 1, "Practice");
        var dust = new InstanceEntry(Guid.NewGuid(), new(1, Guid.NewGuid(), 1, "cs_test_dust_", 27015, presetA.Id), [presetA, presetB]);
        var inferno = new InstanceEntry(Guid.NewGuid(), new(1, Guid.NewGuid(), 1, "cs_test_inferno_", 27016, presetA.Id), [presetA]);
        var catalog = new[] { dust, inferno };
        Check(DevTargets.Resolve([dust], null).Id == dust.Id, "single instance should resolve without a selector.");
        Reject("Several instances", () => DevTargets.Resolve(catalog, null));
        Check(DevTargets.Resolve(catalog, dust.Id.ToString("D")).Id == dust.Id, "UUID selector failed.");
        Check(DevTargets.Resolve(catalog, "cs_test_dust_").Id == dust.Id, "name selector failed.");
        Check(DevTargets.Resolve(catalog, "CS_TEST_INFERNO_").Id == inferno.Id, "name selector must ignore case.");
        Check(DevTargets.Resolve(catalog, "inferno").Id == inferno.Id, "unique prefix selector failed.");
        Reject("matches several", () => DevTargets.Resolve(catalog, "cs_test_"));
        Reject("was not found", () => DevTargets.Resolve(catalog, "nope"));
        Check(DevTargets.ResolvePreset(dust, "practice").Id == presetB.Id, "preset name selector failed.");
        Check(DevTargets.ResolvePreset(dust, presetA.Id.ToString("D")).Id == presetA.Id, "preset UUID selector failed.");
        Check(DevTargets.ResolvePreset(dust, null).Id == presetA.Id, "selected preset fallback failed.");
        Reject("unknown", () => DevTargets.ResolvePreset(dust, "missing"));
        Reject("does not belong", () => DevTargets.ResolvePreset(inferno, presetB.Id.ToString("D")));

        Console.WriteLine("PASS dev.cli-parse-hints-projections-paths-and-selectors");
    }

    private sealed class FakeDaemon(WorkspacePaths defaults) : IDaemonClient
    {
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: defaults));
    }
}
