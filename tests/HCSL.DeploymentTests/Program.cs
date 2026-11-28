using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HCSL.Core;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;

if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP: deployment baseline is Linux-only."); return 0; }
var root = Path.Combine(Environment.GetEnvironmentVariable("HCSL_DEPLOYMENT_TEST_ROOT") ?? Path.GetTempPath(), "hcsl-deployment-" + Guid.NewGuid().ToString("N"));
var oldState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(root, "state"));
var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
void Reject(string code, Action action)
{
    try { action(); throw new Exception("Expected " + code); }
    catch (DeploymentException e) when (e.Code == code) { }
}
string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
try
{
    var store = new LocalInstanceStore();
    var first = store.Execute(workspace, new(InstanceAction.Create, Name: "A", GamePort: 27801)).Instances.Single();
    var second = store.Execute(workspace, new(InstanceAction.Create, Name: "B", GamePort: 27802)).Instances.Single(e => e.Id != first.Id);
    var server = Path.Combine(workspace.LibraryRoot, "server");
    string Source(string relative, string content)
    { var path = Path.Combine(server, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
    var entrypoint = Source("game/cs2.sh", "#!/bin/sh\nexit 0\n");
    File.SetUnixFileMode(entrypoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var native = Source("game/bin/linuxsteamrt64/cs2", "fixture native");
    Source("game/csgo/steam.inf", "appid=730\n"); Source("game/csgo/gameinfo.gi", "fixture gameinfo");
    Source("game/csgo/cfg/server.cfg", "hostname base\n"); Source("game/csgo/cfg/default.cfg", "fixture defaults");
    Source("game/csgo/resource/data.vpk", "fixture resources");
    Source("game/csgo/maps/de_dust2.vpk", "fixture map");
    Source("game/csgo/maps/de_mirage.vpk", "fixture mirage");
    Source("game/csgo/maps/de_inferno.vpk", "fixture inferno");
    Source("game/empty-file", "");
    Source("steamapps/appmanifest_730.acf", "\"appid\" \"730\" \"StateFlags\" \"4\" \"buildid\" \"123\"");
    var sdk = Path.Combine(workspace.DataRoot, "steamcmd/tool/linux64/steamclient.so"); Directory.CreateDirectory(Path.GetDirectoryName(sdk)!); File.WriteAllText(sdk, "fixture sdk");
    var state = Path.Combine(workspace.LibraryRoot, ".hcsl/cs2-install.json");
    File.WriteAllText(state, "{\"FormatVersion\":1,\"AppId\":730,\"Status\":\"Ready\",\"BuildId\":\"123\"}");
    var baseline = new RuntimeDeploymentService.Baseline("123", Hash(entrypoint), [new("game/bin/linuxsteamrt64/cs2", new FileInfo(native).Length, Hash(native))], ["game/csgo/gameinfo.gi", "game/csgo/steam.inf"]);
    var service = new RuntimeDeploymentService(baseline);
    DeploymentOptions Options(InstanceEntry e) => new(e.Id, DeploymentCopyMode.FullCopy);
    if (args is ["--only", "install-state"])
    {
        File.WriteAllText(state, """{"FormatVersion":1,"AppId":730,"Status":"Ready"}""");
        Reject("HCSL-DEPLOY-INSTALL-STATE", () => service.Plan(workspace, Options(first)));
        File.WriteAllText(state, """{"FormatVersion":1,"AppId":730,"Status":"Ready","BuildId":"456"}""");
        Reject("HCSL-DEPLOY-BASELINE", () => service.Plan(workspace, Options(first)));
        Console.WriteLine("PASS invalid installation state and stale baseline have distinct codes");
        return 0;
    }
    string Runtime(InstanceEntry e) => Path.Combine(workspace.LibraryRoot, "runtime", e.Id.ToString());
    string Preset(InstanceEntry e) => Path.Combine(workspace.DataRoot, "instances", e.Id.ToString(), "presets", e.Definition!.SelectedPresetId.ToString());
    string Journal(InstanceEntry e) => Path.Combine(workspace.LibraryRoot, ".hcsl", "deployment-" + e.Id + ".json");
    var sourceHashes = Directory.GetFiles(Path.Combine(server, "game"), "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
    if (args is ["--only", "plugin-sync"])
    {
        var plugins = new LocalPluginStore();
        var definition = first.Definition!;
        const string relative = "csgo/addons/example-plugin/bin/plugin.so";
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "example-plugin", version);
            var file = Path.Combine(package, "files", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, version);
            File.WriteAllText(Path.Combine(package, "plugin.json"), $$"""{"FormatVersion":1,"Id":"example-plugin","Name":"Example","Version":"{{version}}"}""");
        }
        void Select(string? version)
        {
            var list = plugins.Execute(workspace, new(PluginAction.List, first.Id, definition.SelectedPresetId));
            plugins.Execute(workspace, new(PluginAction.Save, first.Id, definition.SelectedPresetId, definition.Revision,
                list.Hash, version is null ? [] : [new("example-plugin", version)]));
        }
        service.Deploy(workspace, Options(first));
        var sentinel = Path.Combine(Runtime(first), "keep-in-current-runtime.txt");
        File.WriteAllText(sentinel, "original runtime");
        Check(service.Plan(workspace, Options(first)).Scope == DeploymentScope.None, "An unchanged verified deployment should not require deployment.");
        var target = Path.Combine(Runtime(first), "game", relative);
        var incremental = new RuntimeDeploymentService(baseline, phase =>
        {
            if (phase == "Cloned") throw new Exception("Plugin sync cloned the runtime.");
        });
        Select("1.0.0");
        Check(service.Plan(workspace, Options(first)).Scope == DeploymentScope.Plugins, "Preview did not classify plugin sync.");
        incremental.Deploy(workspace, Options(first));
        Check(File.ReadAllText(target) == "1.0.0", "Plugin was not added.");
        Select("2.0.0");
        var pools = new LocalPresetMapPoolStore();
        var pool = pools.Execute(workspace, new(MapPoolAction.Read, first.Id, definition.SelectedPresetId));
        pools.Execute(workspace, new(MapPoolAction.Save, first.Id, definition.SelectedPresetId, definition.Revision,
            pool.Pool.Revision, pool.Hash, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_inferno")]));
        var rotationFile = Path.Combine(Runtime(first), "game/csgo", ServerMapGroupRules.FileName);
        Check(service.Plan(workspace, Options(first)).Scope == DeploymentScope.PluginsAndRotation, "Preview did not classify combined sync.");
        File.WriteAllText(target, "user edit");
        Reject("HCSL-DEPLOY-PLUGIN-CONFLICT", () => incremental.Deploy(workspace, Options(first)));
        Check(File.ReadAllText(target) == "user edit", "Plugin sync overwrote a user edit.");
        File.WriteAllText(target, "1.0.0");
        var failing = new RuntimeDeploymentService(baseline, phase =>
        {
            if (phase == "IncrementalPluginsApplied") throw new IOException("injected sync failure");
        });
        try { failing.Deploy(workspace, Options(first)); throw new Exception("Expected plugin sync failure."); }
        catch (IOException e) when (e.Message == "injected sync failure") { }
        Check(File.ReadAllText(target) == "1.0.0" && !File.Exists(rotationFile), "Failed sync did not roll back both changes.");
        var updated = incremental.Deploy(workspace, Options(first));
        Check(File.ReadAllText(target) == "2.0.0" && updated.RotationHash is not null &&
            RuntimeDeploymentService.RotationFileMatches(updated, Runtime(first)), "Plugin and rotation were not published together.");
        Select(null);
        incremental.Deploy(workspace, Options(first));
        Check(!File.Exists(target) && File.Exists(sentinel), "Plugin removal failed or replaced the runtime.");
        Console.WriteLine("PASS addon sync adds, upgrades and removes plugins without replacing the runtime");
        return 0;
    }
    if (args is ["--only", "plugin-manifest"])
    {
        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "metamod", "1.0.0");
        var overlay = Path.Combine(package, "files", "csgo", "gameinfo.gi");
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
        File.WriteAllText(overlay, "patched gameinfo");
        File.WriteAllText(Path.Combine(package, "plugin.json"), """{"FormatVersion":1,"Id":"metamod","Name":"Metamod","Version":"1.0.0"}""");
        var plugins = new LocalPluginStore();
        var definition = first.Definition!;
        var list = plugins.Execute(workspace, new(PluginAction.List, first.Id, definition.SelectedPresetId));
        plugins.Execute(workspace, new(PluginAction.Save, first.Id, definition.SelectedPresetId, definition.Revision,
            list.Hash, [new("metamod", "1.0.0")]));
        service.Deploy(workspace, Options(first));
        var receipt = DeploymentFiles.Read<DeploymentReceipt>(Path.Combine(Runtime(first), "deployment.json"));
        var deployed = receipt.PluginManifest?.Single();
        Check(deployed is { Id: "metamod", Version: "1.0.0" } &&
            deployed.Files.Contains(new("csgo/gameinfo.gi", Hash(overlay))), "Receipt omitted a non-addon plugin overlay.");
        Console.WriteLine("PASS deployment receipt records plugin files outside persistent addons");
        return 0;
    }
    if (args is ["--only", "rotation-update"])
    {
        var rotationStore = new LocalPresetMapPoolStore();
        var definition = first.Definition!;
        void Save(params string[] maps)
        {
            var current = rotationStore.Execute(workspace, new(MapPoolAction.Read, first.Id, definition.SelectedPresetId));
            rotationStore.Execute(workspace, new(MapPoolAction.Save, first.Id, definition.SelectedPresetId, definition.Revision,
                current.Pool.Revision, current.Hash, maps.Select(map => new ServerMapTarget(ServerMapKind.Standard, map)).ToArray()));
        }
        var initial = service.Deploy(workspace, Options(first));
        Save("de_dust2", "de_inferno");
        Check(service.Plan(workspace, Options(first)).Scope == DeploymentScope.Rotation, "Preview did not classify rotation sync.");
        var prior = service.Deploy(workspace, Options(first));
        Check(prior.OperationId == initial.OperationId && prior.RotationHash is not null &&
            RuntimeDeploymentService.RotationFileMatches(prior, Runtime(first)), "Adding the first rotation replaced the runtime or did not write the map group.");
        Save("de_inferno", "de_dust2");
        var fail = new RuntimeDeploymentService(baseline, phase =>
        {
            if (phase == "Cloned") throw new InvalidOperationException("Rotation update cloned the runtime.");
            if (phase == "RotationWritten") throw new IOException("injected receipt publication failure");
        });
        try { fail.Deploy(workspace, Options(first)); throw new InvalidOperationException("Expected publication failure."); }
        catch (IOException e) when (e.Message == "injected receipt publication failure") { }
        Check(RuntimeDeploymentService.RotationFileMatches(prior, Runtime(first)), "Failed update did not restore the old rotation.");
        var updated = service.Deploy(workspace, Options(first));
        Check(updated.OperationId == prior.OperationId && updated.RotationHash != prior.RotationHash &&
            RuntimeDeploymentService.RotationFileMatches(updated, Runtime(first)), "Rotation update replaced the runtime or did not publish the new rotation.");
        Save();
        try { fail.Deploy(workspace, Options(first)); throw new InvalidOperationException("Expected publication failure."); }
        catch (IOException e) when (e.Message == "injected receipt publication failure") { }
        Check(RuntimeDeploymentService.RotationFileMatches(updated, Runtime(first)), "Failed removal did not restore the map group.");
        var removed = service.Deploy(workspace, Options(first));
        Check(removed.OperationId == initial.OperationId && removed.RotationHash is null &&
            !File.Exists(Path.Combine(Runtime(first), "game/csgo", ServerMapGroupRules.FileName)), "Removing the rotation replaced the runtime or left the map group behind.");
        Console.WriteLine("PASS adding, editing and removing a rotation avoids cloning and rolls back a failed publication");
        return 0;
    }
    if (args is ["--only", "cancel-validation"])
    {
        var deployed = service.Deploy(workspace, Options(first));
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        var installation = new HCSL.Infrastructure.Steam.Cs2Installation(lease.Layout);
        var ready = File.ReadAllText(state);
        installation.Finish(cancelled: true);
        Check(File.ReadAllText(state) == ready, "cancellation before SteamCMD release must not alter the record");
        installation.Begin(validation: true);
        installation.Finish(cancelled: true);
        using (var record = JsonDocument.Parse(File.ReadAllText(state)))
            Check(record.RootElement.GetProperty("Status").GetString() == "ValidationInterrupted" &&
                record.RootElement.GetProperty("BuildId").GetString() == "123", "cancelled validation lost the last verified build");
        File.WriteAllText(native, "unfinished repair in shared content");
        var plan = service.PrepareForStart(lease, Options(first), autoDeploy: false, default);
        Check(plan.BuildId == deployed.BuildId && plan.RuntimePath == Runtime(first), "verified deployment must remain startable");
        Reject("HCSL-DEPLOY-VALIDATION-INTERRUPTED", () => service.Prepare(lease, Options(first), default));
        Reject("HCSL-DEPLOY-VALIDATION-INTERRUPTED", () => service.PrepareForStart(lease, Options(first), autoDeploy: true, default));
        Reject("HCSL-DEPLOY-VALIDATION-INTERRUPTED", () => service.PrepareForStart(lease, Options(second), autoDeploy: false, default));
        Check(DeploymentFiles.Read<DeploymentReceipt>(Path.Combine(Runtime(first), "deployment.json")).OperationId == deployed.OperationId,
            "start eligibility must not silently redeploy");
        File.WriteAllText(Path.Combine(Runtime(first), "game/bin/linuxsteamrt64/cs2"), "damaged deployed binary");
        Reject("HCSL-DEPLOY-BASELINE", () => service.PrepareForStart(lease, Options(first), autoDeploy: false, default));
        Console.WriteLine("PASS cancelled validation preserves only an independently verified deployment");
        return 0;
    }
    Test("managed update writes a local baseline that deployment can resolve", () =>
    {
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        RuntimeDeploymentService.RefreshLocalBaseline(lease.Layout, "123", Guid.NewGuid(), baseline);
        var local = Path.Combine(workspace.LibraryRoot, ".hcsl", "linux-startup-baseline.json");
        Check(File.Exists(local), "local baseline was not written");
        var resolved = new RuntimeDeploymentService().ActiveBaseline(lease.Layout, "123");
        Check(resolved.BuildId == "123" && resolved.Inventory.Length == 1 && resolved.Inventory[0].Sha256 == Hash(native),
            "local baseline was not resolved for deployment");
        // A build id that Steam did not deliver must never become a baseline; the old file stays untouched.
        var before = File.ReadAllBytes(local);
        Reject("HCSL-DEPLOY-BASELINE", () => RuntimeDeploymentService.RefreshLocalBaseline(lease.Layout, "999", Guid.NewGuid(), baseline));
        Check(File.ReadAllBytes(local).SequenceEqual(before), "rejected refresh modified the local baseline");
        Check(new RuntimeDeploymentService().ActiveBaseline(lease.Layout, "999").BuildId != "999",
            "rejected refresh still resolved an untrusted build");
    });
    Test("plan verifies without runtime or persistent-data seeding", () =>
    {
        var plan = service.Plan(workspace, Options(first));
        Check(plan.FileCount == sourceHashes.Count && plan.LogicalBytes > 0 && plan.PersistentMappings.Length == 6, "incomplete plan");
        var instanceRoot = Path.Combine(workspace.DataRoot, "instances", first.Id.ToString());
        Check(plan.PersistentMappings.Single(m => m.RuntimePath == "home").PersistentPath == Path.Combine(instanceRoot, "home"), "home is not instance-scoped");
        Check(plan.PersistentMappings.Single(m => m.RuntimePath == "userdata").PersistentPath == Path.Combine(instanceRoot, "userdata"), "userdata is not instance-scoped");
        Check(!Directory.Exists(Runtime(first)) && !Directory.Exists(Path.Combine(Preset(first), "home")), "plan wrote deployment data");
        Reject("HCSL-DEPLOY-CONFLICT", () => service.Plan(workspace, Options(first) with { ExpectedRevision = 999 }));
    });
    Test("two instances deploy independent games and canonical writable directories", () =>
    {
        File.WriteAllText(Path.Combine(Preset(first), "cfg/server.cfg"), "hostname A\n");
        var a = service.Deploy(workspace, Options(first)); var b = service.Deploy(workspace, Options(second));
        Check(a.Status == "DeployedOffline" && a.BuildId == "123" && a.PresetId != b.PresetId, "wrong receipt");
        Check(File.ReadAllText(Path.Combine(Runtime(first), "game/csgo/cfg/server.cfg")) == "hostname A\n", "custom config overwritten");
        File.WriteAllText(Path.Combine(Runtime(first), "game/csgo/cfg/generated.cfg"), "A generated");
        File.WriteAllText(Path.Combine(Runtime(first), "game/csgo/addons/plugin.db"), "A database");
        File.WriteAllText(Path.Combine(Runtime(first), "userdata/convars.vcfg"), "A convars");
        Check(File.Exists(Path.Combine(Preset(first), "cfg/generated.cfg")), "cfg not persisted");
        Check(!File.Exists(Path.Combine(Runtime(second), "game/csgo/cfg/generated.cfg")), "cross-instance CFG write");
        var binary = Path.Combine(Runtime(first), "game/bin/linuxsteamrt64/cs2"); File.WriteAllText(binary, "modified runtime");
        Check(Hash(native) == sourceHashes[native] && File.ReadAllText(Path.Combine(Runtime(second), "game/bin/linuxsteamrt64/cs2")) == "fixture native", "binary write crossed instances");
        Check((File.GetUnixFileMode(Path.Combine(Runtime(first), "game/cs2.sh")) & UnixFileMode.UserExecute) != 0, "entrypoint mode lost");
    });
    Test("redeploy preserves persistent data and unknown runtime writes", () =>
    {
        File.WriteAllText(Path.Combine(Runtime(first), "unknown-demo.dem"), "preserve me");
        service.Deploy(workspace, Options(first));
        Check(File.ReadAllText(Path.Combine(Runtime(first), "game/csgo/addons/plugin.db")) == "A database", "plugin data lost");
        Check(File.ReadAllText(Path.Combine(Runtime(first), "userdata/convars.vcfg")) == "A convars", "user config lost");
        var previous = Directory.GetDirectories(Path.GetDirectoryName(Runtime(first))!, ".previous-" + first.Id + "-*");
        Check(previous.Any(p => File.Exists(Path.Combine(p, "unknown-demo.dem"))), "unknown writes discarded");
    });
    Test("preset switching deploys separate plugin data but keeps instance data", () =>
    {
        File.WriteAllText(Path.Combine(Runtime(first), "home", "instance-home.marker"), "home");
        File.WriteAllText(Path.Combine(Runtime(first), "userdata", "instance-userdata.marker"), "userdata");
        first = store.Execute(workspace, new(InstanceAction.CreatePreset, first.Id, Revision: first.Definition!.Revision, Name: "Alternate")).Instances.Single(e => e.Id == first.Id);
        var alternate = first.Presets.Single(p => p.Id != first.Definition!.SelectedPresetId);
        first = store.Execute(workspace, new(InstanceAction.SelectPreset, first.Id, alternate.Id, first.Definition!.Revision)).Instances.Single(e => e.Id == first.Id);
        service.Deploy(workspace, Options(first));
        Check(!File.Exists(Path.Combine(Runtime(first), "game/csgo/addons/plugin.db")), "previous preset database reused");
        Check(File.Exists(Path.Combine(Runtime(first), "home", "instance-home.marker")), "instance home lost across preset switch");
        Check(File.Exists(Path.Combine(Runtime(first), "userdata", "instance-userdata.marker")), "instance userdata lost across preset switch");
    });
    foreach (var phase in new[] { "Journaled", "Cloned", "Prepared", "PluginsApplied", "PreviousMoved", "Published" })
        Test("interruption recovery at " + phase, () =>
        {
            var original = JsonSerializer.Deserialize<DeploymentReceipt>(File.ReadAllText(Path.Combine(Runtime(first), "deployment.json")))!;
            var interrupted = new RuntimeDeploymentService(baseline, p => { if (p == phase) throw new IOException("injected interruption"); });
            try { interrupted.Deploy(workspace, Options(first)); throw new Exception("injection not reached"); } catch (IOException) { }
            Check(File.Exists(Journal(first)), "recovery owner missing");
            Reject("HCSL-DEPLOY-RECOVERY", () => service.Plan(workspace, Options(first)));
            var result = service.Recover(workspace, first.Id);
            var current = JsonSerializer.Deserialize<DeploymentReceipt>(File.ReadAllText(Path.Combine(Runtime(first), "deployment.json")))!;
            Check(!File.Exists(Journal(first)), "recovery did not finish");
            Check(phase == "Published" ? current.OperationId != original.OperationId : current.OperationId == original.OperationId, "wrong deployment preserved");
            Check(result.Length > 0, "missing recovery outcome");
        });
    Test("cancelled deployment retains old runtime and can recover", () =>
    {
        using var stop = new CancellationTokenSource();
        var cancelService = new RuntimeDeploymentService(baseline, p => { if (p == "Cloned") stop.Cancel(); });
        try { cancelService.Deploy(workspace, Options(first), cancellationToken: stop.Token); throw new Exception("not cancelled"); } catch (OperationCanceledException) { }
        service.Recover(workspace, first.Id); Check(File.Exists(Path.Combine(Runtime(first), "deployment.json")), "old runtime lost");
    });
    Test("source corruption, unfinished SteamCMD and nonempty plugin manifests fail closed", () =>
    {
        File.WriteAllText(native, "tampered"); Reject("HCSL-DEPLOY-BASELINE", () => service.Plan(workspace, Options(first))); File.WriteAllText(native, "fixture native");
        var journal = Path.Combine(workspace.DataRoot, "steamcmd/tool/.hcsl-session.json"); File.WriteAllText(journal, "{}");
        Reject("HCSL-DEPLOY-STEAMCMD", () => service.Plan(workspace, Options(first))); File.Delete(journal);
        var plugins = Path.Combine(Preset(first), "plugins.json"); var saved = File.ReadAllText(plugins); File.WriteAllText(plugins, "{\"FormatVersion\":1,\"Plugins\":[{}]}");
        Reject("HCSL-DEPLOY-PLUGINS", () => service.Plan(workspace, Options(first))); File.WriteAllText(plugins, saved);
    });
    Test("unknown runtime directory, linked sources and modified mappings are refused", () =>
    {
        var third = store.Execute(workspace, new(InstanceAction.Create, Name: "C", GamePort: 27803)).Instances.Single(e => e.Definition!.Name == "C");
        Directory.CreateDirectory(Runtime(third)); File.WriteAllText(Path.Combine(Runtime(third), "unowned"), "keep");
        Reject("HCSL-DEPLOY-UNMANAGED", () => service.Plan(workspace, Options(third)));
        Check(File.ReadAllText(Path.Combine(Runtime(third), "unowned")) == "keep", "unowned runtime overwritten");
        var sourceLink = Path.Combine(server, "game/linked"); File.CreateSymbolicLink(sourceLink, native);
        try { service.Plan(workspace, Options(first)); throw new Exception("source link accepted"); } catch (WorkspaceException e) when (e.Code == "HCSL-PATH") { } finally { File.Delete(sourceLink); }
        var link = Path.Combine(Runtime(first), "game/csgo/cfg"); var original = new DirectoryInfo(link).LinkTarget!;
        Directory.Delete(link); Directory.CreateSymbolicLink(link, root);
        Reject("HCSL-DEPLOY-PATH", () => service.Plan(workspace, Options(first))); Directory.Delete(link); Directory.CreateSymbolicLink(link, original);
    });
    Test("active process blocks replacing runtime", () =>
    {
        using var sleeper = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { WorkingDirectory = Runtime(first), UseShellExecute = false })!;
        try { Thread.Sleep(100); Reject("HCSL-DEPLOY-RUNNING", () => service.Plan(workspace, Options(first))); }
        finally { if (!sleeper.HasExited) sleeper.Kill(); sleeper.WaitForExit(); }
    });
    Test("native reflink is isolated or fails explicitly without fallback", () =>
    {
        var clone = Path.Combine(root, "reflink-probe");
        try
        {
            DeploymentFiles.Copy(native, clone, DeploymentCopyMode.Reflink);
            File.WriteAllText(clone, "changed clone"); Check(File.ReadAllText(native) == "fixture native", "reflink mutated source");
            Console.WriteLine("  native reflink supported, independent writes confirmed");
        }
        catch (DeploymentException e) when (e.Code == "HCSL-DEPLOY-REFLINK")
        { Check(!File.Exists(clone), "partial file remained after failed clone"); Console.WriteLine("  native reflink unsupported, no fallback confirmed"); }
        finally { if (File.Exists(clone)) File.Delete(clone); }
    });
    Test("default reflink deployment publishes or recovers without replacing old deployment", () =>
    {
        var oldReceipt = DeploymentFiles.Read<DeploymentReceipt>(Path.Combine(Runtime(first), "deployment.json"));
        try
        {
            var receipt = service.Deploy(workspace, new(first.Id));
            Check(receipt.CopyMode == DeploymentCopyMode.Reflink && !File.Exists(Journal(first)), "reflink publication incomplete");
            Check(new FileInfo(Path.Combine(Runtime(first), "game/empty-file")).Length == 0, "empty clone lost");
        }
        catch (DeploymentException e) when (e.Code == "HCSL-DEPLOY-REFLINK")
        {
            Check(File.Exists(Journal(first)), "failed clone has no recovery owner");
            service.Recover(workspace, first.Id);
            Check(DeploymentFiles.Read<DeploymentReceipt>(Path.Combine(Runtime(first), "deployment.json")).OperationId == oldReceipt.OperationId, "unsupported clone replaced old runtime");
        }
    });
    Test("all-standard pool generates only the runtime map group and changes staleness", () =>
    {
        var poolStore = new LocalPresetMapPoolStore();
        var presetId = first.Definition!.SelectedPresetId;
        MapPoolResult Save(ServerMapTarget[] maps)
        {
            var current = poolStore.Execute(workspace, new(MapPoolAction.Read, first.Id, presetId));
            return poolStore.Execute(workspace, new(MapPoolAction.Save, first.Id, presetId, first.Definition.Revision, current.Pool.Revision, current.Hash, maps));
        }
        Save([new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage"), new(ServerMapKind.Standard, "de_inferno")]);
        var plan = service.Plan(workspace, Options(first));
        Check(plan.RotationGroup == ServerMapGroupRules.GroupName(presetId) && plan.RotationHash is not null, "Plan omitted the map group.");
        var receipt = service.Deploy(workspace, Options(first));
        var rotation = Path.Combine(Runtime(first), "game/csgo/gamemodes_server.txt");
        Check(receipt.RotationGroup == plan.RotationGroup && receipt.RotationHash == plan.RotationHash, "Receipt omitted the map group.");
        Check(File.ReadAllText(rotation).Contains("\"de_mirage\"\t\t\"\"", StringComparison.Ordinal), "Generated map group missing pooled maps.");
        Check(!File.Exists(Path.Combine(server, "game/csgo/gamemodes_server.txt")), "Map group was written into the shared installation.");
        Save([new(ServerMapKind.Standard, "de_inferno"), new(ServerMapKind.Standard, "de_mirage"), new(ServerMapKind.Standard, "de_dust2")]);
        var reordered = service.Plan(workspace, Options(first));
        Check(reordered.RotationHash != plan.RotationHash, "Reordered pool did not invalidate the plan.");
        receipt = service.Deploy(workspace, Options(first));
        Check(File.ReadAllText(rotation).IndexOf("de_inferno", StringComparison.Ordinal) < File.ReadAllText(rotation).IndexOf("de_dust2", StringComparison.Ordinal), "Redeploy did not regenerate map group order.");
        File.WriteAllText(rotation, "tampered");
        Check(!RuntimeDeploymentService.RotationFileMatches(receipt, Runtime(first)), "Modified map group reported as intact.");
        service.Deploy(workspace, Options(first));
        Check(File.ReadAllText(rotation).Contains("de_inferno", StringComparison.Ordinal), "Redeploy did not repair the map group.");
        Save([new(ServerMapKind.Standard, "de_mirage")]);
        Reject("HCSL-DEPLOY-ROTATION", () => service.Plan(workspace, Options(first)));
        Save([new(ServerMapKind.Workshop, "3070244462")]);
        Check(service.Plan(workspace, Options(first)).RotationGroup is null, "Workshop pool produced a native map group.");
        service.Deploy(workspace, Options(first));
        Check(!File.Exists(rotation), "Workshop redeploy retained a native map group.");
        Save([new(ServerMapKind.WorkshopCollection, "3070244462")]);
        var collectionPlan = service.Plan(workspace, Options(first));
        Check(collectionPlan.RotationGroup is null && collectionPlan.RotationCollection == "3070244462", "Collection pool plan is wrong.");
        var collectionReceipt = service.Deploy(workspace, Options(first));
        Check(collectionReceipt.RotationCollection == "3070244462" && !File.Exists(rotation), "Collection pool wrote or lost state.");
        Save([new(ServerMapKind.Standard, "de_dust2")]);
        var baseRotation = Path.Combine(server, "game/csgo/gamemodes_server.txt");
        File.WriteAllText(baseRotation, "\"GameModes_Server.txt\"\n{\n}\n");
        try { Reject("HCSL-DEPLOY-ROTATION", () => service.Plan(workspace, Options(first))); }
        finally { File.Delete(baseRotation); }
    });
    Test("nested preset cfg is visible and survives redeploy", () =>
    {
        var cfg = Path.Combine(Preset(first), "cfg", "pt", "map");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "dust2.cfg"), "echo nested");
        service.Deploy(workspace, Options(first));
        var runtime = Path.Combine(Runtime(first), "game", "csgo", "cfg", "pt", "map", "dust2.cfg");
        Check(File.Exists(runtime) && File.ReadAllText(runtime) == "echo nested", "Nested preset cfg missing from the runtime.");
        File.WriteAllText(Path.Combine(cfg, "dust2.cfg"), "echo edited");
        service.Deploy(workspace, Options(first));
        Check(File.ReadAllText(runtime) == "echo edited", "Redeploy overwrote a nested user cfg.");
    });
    Test("enabled plugin packages overlay the runtime and disabling removes only tracked files", () =>
    {
        var presetId = first.Definition!.SelectedPresetId;
        var plugins = new LocalPluginStore();
        var pluginFile = Path.Combine(Preset(first), "plugins.json");
        var initial = plugins.Execute(workspace, new(PluginAction.List, first.Id, presetId));
        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "metamod", "1.0.0");
        Directory.CreateDirectory(Path.Combine(package, "files", "csgo", "addons", "metamod"));
        File.WriteAllText(Path.Combine(package, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"metamod\",\"Name\":\"Metamod\",\"Version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(package, "files", "csgo", "addons", "metamod", "metamod.vdf"), "metamod vdf");
        File.WriteAllText(Path.Combine(package, "files", "csgo", "gameinfo.gi"), "patched gameinfo");
        var saved = plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, initial.Hash,
            [new("metamod", "1.0.0")]));
        var plan = service.Plan(workspace, Options(first));
        Check(plan.PluginSetHash is not null, "Plan omitted plugin set hash.");
        var receipt = service.Deploy(workspace, Options(first));
        Check(receipt.PluginSetHash == plan.PluginSetHash, "Receipt omitted plugin set hash.");
        Check(File.ReadAllText(Path.Combine(Runtime(first), "game/csgo/addons/metamod/metamod.vdf")) == "metamod vdf", "Addon package not overlaid.");
        Check(File.ReadAllText(Path.Combine(Runtime(first), "game/csgo/gameinfo.gi")) == "patched gameinfo", "Runtime gameinfo not overlaid.");
        Check(File.ReadAllText(Path.Combine(server, "game/csgo/gameinfo.gi")) == "fixture gameinfo", "Shared gameinfo changed.");
        var ledger = Path.Combine(Preset(first), ".hcsl-plugin-ledger.json");
        Check(File.Exists(ledger) && File.ReadAllText(ledger).Contains("csgo/addons/metamod/metamod.vdf"), "Plugin ledger missing.");
        // A ledger left inside addons by an older build is migrated out of the game-visible tree.
        var legacyLedger = Path.Combine(Preset(first), "addons", ".hcsl-plugin-ledger.json");
        File.Copy(ledger, legacyLedger, true);
        service.Deploy(workspace, Options(first));
        Check(!File.Exists(legacyLedger) && File.Exists(ledger), "Legacy ledger was not migrated out of addons.");
        var disabled = plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, saved.Hash, []));
        var disabledPlan = service.Plan(workspace, Options(first));
        Check(disabledPlan.PluginSetHash is null && disabledPlan.PluginSetHash != plan.PluginSetHash, "Disabling did not change hash.");
        service.Deploy(workspace, Options(first));
        Check(!File.Exists(Path.Combine(Runtime(first), "game/csgo/addons/metamod/metamod.vdf")), "Disabled package file remained.");
        Check(File.ReadAllText(Path.Combine(Runtime(first), "game/csgo/gameinfo.gi")) == "fixture gameinfo", "Disabled overlay retained gameinfo.");
        Check(!File.ReadAllText(ledger).Contains("metamod.vdf"), "Ledger retained removed file.");
        plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, disabled.Hash, [new("metamod", "1.0.0")]));
        service.Deploy(workspace, Options(first));
        File.WriteAllText(Path.Combine(Runtime(first), "game/csgo/addons/metamod/metamod.vdf"), "user modified");
        var current = plugins.Execute(workspace, new(PluginAction.List, first.Id, presetId));
        var off = plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, current.Hash, []));
        service.Deploy(workspace, Options(first));
        Check(File.ReadAllText(Path.Combine(Runtime(first), "game/csgo/addons/metamod/metamod.vdf")) == "user modified", "Modified plugin data was deleted.");
        plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, off.Hash, []));
        File.WriteAllText(pluginFile, "{\"FormatVersion\":1,\"Revision\":1,\"Plugins\":[{\"Id\":\"metamod\",\"Version\":\"1.0.0\",\"Enabled\":true}]}");
        File.Delete(Path.Combine(workspace.LibraryRoot, "plugin-packages", "metamod", "1.0.0", "plugin.json"));
        Reject("HCSL-DEPLOY-PLUGINS", () => service.Plan(workspace, Options(first)));
    });
    Test("deployment rejects conflicting scripting frameworks", () =>
    {
        var presetId = first.Definition!.SelectedPresetId;
        void Package(string id, string version, string relative)
        {
            var dir = Path.Combine(workspace.LibraryRoot, "plugin-packages", id, version);
            Directory.CreateDirectory(Path.Combine(dir, "files", Path.GetDirectoryName(relative)!));
            File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""{"FormatVersion":1,"Id":"{{id}}","Name":"{{id}}","Version":"{{version}}"}""");
            File.WriteAllText(Path.Combine(dir, "files", relative), "binary");
        }
        Package("metamod", "1.0.0", "csgo/addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so");
        Package("counterstrikesharp", "1.0.0", "csgo/addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so");
        Package("source2mod", "1.0.0", "csgo/addons/source2mod/bin/linuxsteamrt64/source2mod.so");
        var file = Path.Combine(Preset(first), "plugins.json");
        File.WriteAllText(file, "{\"FormatVersion\":1,\"Revision\":1,\"Plugins\":["
            + "{\"Id\":\"metamod\",\"Version\":\"1.0.0\",\"Enabled\":true},"
            + "{\"Id\":\"counterstrikesharp\",\"Version\":\"1.0.0\",\"Enabled\":true},"
            + "{\"Id\":\"source2mod\",\"Version\":\"1.0.0\",\"Enabled\":true}]}");
        Reject("HCSL-PLUGIN-FRAMEWORK-CONFLICT", () => service.Plan(workspace, Options(first)));
        File.WriteAllText(file, "{\"FormatVersion\":1,\"Revision\":1,\"Plugins\":["
            + "{\"Id\":\"metamod\",\"Version\":\"1.0.0\",\"Enabled\":true},"
            + "{\"Id\":\"source2mod\",\"Version\":\"1.0.0\",\"Enabled\":true}]}");
        Check(service.Plan(workspace, Options(first)).PluginSetHash is not null, "Valid Source2Mod selection failed to plan.");
        File.WriteAllText(file, """{"FormatVersion":1,"Revision":1,"Plugins":[]}""");
    });
    Test("failed plugin application restores preset addons and ledger", () =>
    {
        var presetId = first.Definition!.SelectedPresetId;
        var plugins = new LocalPluginStore();
        var current = plugins.Execute(workspace, new(PluginAction.List, first.Id, presetId));
        var empty = plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, current.Hash, []));
        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "rollback", "1.0.0");
        Directory.CreateDirectory(Path.Combine(package, "files", "csgo", "addons", "rollback"));
        File.WriteAllText(Path.Combine(package, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"rollback\",\"Name\":\"Rollback\",\"Version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(package, "files", "csgo", "addons", "rollback", "rollback.vdf"), "package content");
        plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, empty.Hash, [new("rollback", "1.0.0")]));
        var addon = Path.Combine(Preset(first), "addons", "rollback", "rollback.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(addon)!); File.WriteAllText(addon, "preexisting user file");
        var ledger = Path.Combine(Preset(first), ".hcsl-plugin-ledger.json");
        var beforeLedger = File.Exists(ledger) ? File.ReadAllText(ledger) : null;
        var before = JsonSerializer.Deserialize<DeploymentReceipt>(File.ReadAllText(Path.Combine(Runtime(first), "deployment.json")))!;
        var failing = new RuntimeDeploymentService(baseline, p => { if (p == "PluginsApplied") throw new IOException("injected publication failure"); });
        try { failing.Deploy(workspace, Options(first)); throw new Exception("injection not reached"); } catch (IOException) { }
        Check(File.ReadAllText(addon) == "preexisting user file", "Failed deploy did not restore the preset addon file.");
        Check(beforeLedger is null ? !File.Exists(ledger) : File.ReadAllText(ledger) == beforeLedger, "Failed deploy changed the plugin ledger.");
        Check(!Directory.EnumerateDirectories(Preset(first), ".hcsl-plugin-backup-*").Any(), "Failed deploy left a backup directory.");
        var after = JsonSerializer.Deserialize<DeploymentReceipt>(File.ReadAllText(Path.Combine(Runtime(first), "deployment.json")))!;
        Check(after.OperationId == before.OperationId, "Failed plugin publication replaced the runtime.");
        service.Recover(workspace, first.Id);
        Check(!File.Exists(Journal(first)), "Recovery did not finish after the plugin failure.");
    });
    Test("plugin packages cannot write reserved or mapped paths", () =>
    {
        var presetId = first.Definition!.SelectedPresetId;
        var plugins = new LocalPluginStore();
        var current = plugins.Execute(workspace, new(PluginAction.List, first.Id, presetId));
        var empty = plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, current.Hash, []));
        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "reserved", "1.0.0");
        Directory.CreateDirectory(Path.Combine(package, "files", "csgo"));
        File.WriteAllText(Path.Combine(package, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"reserved\",\"Name\":\"Reserved\",\"Version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(package, "files", "csgo", "cfg"), "not a directory");
        plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, empty.Hash, [new("reserved", "1.0.0")]));
        Reject("HCSL-DEPLOY-PLUGINS", () => service.Deploy(workspace, Options(first)));
        service.Recover(workspace, first.Id);
        var reset = plugins.Execute(workspace, new(PluginAction.List, first.Id, presetId));
        plugins.Execute(workspace, new(PluginAction.Save, first.Id, presetId, first.Definition.Revision, reset.Hash, []));
    });
    Test("framework persistent config is seeded, linked, and survives redeploy", () =>
    {
        var mod = store.Execute(workspace, new(InstanceAction.Create, Name: "Mod", GamePort: 27804)).Instances.Single(e => e.Definition!.Name == "Mod");
        var modPresetId = mod.Definition!.SelectedPresetId;
        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "modsharp", "git224");
        Directory.CreateDirectory(Path.Combine(package, "files", "sharp", "configs"));
        Directory.CreateDirectory(Path.Combine(package, "files", "sharp", "bin"));
        File.WriteAllText(Path.Combine(package, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"modsharp\",\"Name\":\"ModSharp\",\"Version\":\"git224\"}");
        File.WriteAllText(Path.Combine(package, "files", "sharp", "configs", "core.json"), "package default");
        File.WriteAllText(Path.Combine(package, "files", "sharp", "bin", "libmodsharp.so"), "loader");
        var plugins = new LocalPluginStore();
        var modPlugins = plugins.Execute(workspace, new(PluginAction.List, mod.Id, modPresetId));
        plugins.Execute(workspace, new(PluginAction.Save, mod.Id, modPresetId, mod.Definition.Revision, modPlugins.Hash, [new("modsharp", "git224")]));
        var modPlan = service.Plan(workspace, Options(mod));
        Check(modPlan.Framework == "modsharp" && modPlan.FrameworkReadiness == FrameworkReadinessProfile.ModSharp, "framework profile not planned");
        Check(modPlan.PersistentMappings.Any(m => m.RuntimePath == "game/sharp/configs" && m.SeedDefaults), "framework persistent mapping missing");
        service.Deploy(workspace, Options(mod));
        var runtimeConfig = Path.Combine(Runtime(mod), "game/sharp/configs/core.json");
        Check(File.ReadAllText(runtimeConfig) == "package default", "package default config not seeded");
        Check(new DirectoryInfo(Path.Combine(Runtime(mod), "game/sharp/configs")).LinkTarget is not null, "framework config is not a persistent link");
        File.WriteAllText(runtimeConfig, "user edited");
        service.Deploy(workspace, Options(mod));
        Check(File.ReadAllText(runtimeConfig) == "user edited", "framework config was overwritten on redeploy");
    });
    Test("ModSharp managed modules persist and keep built-in modules real", () =>
    {
        var mod = store.Execute(workspace, new(InstanceAction.Create, Name: "ModModules", GamePort: 27806)).Instances.Single(e => e.Definition!.Name == "ModModules");
        var presetId = mod.Definition!.SelectedPresetId;
        var presetPath = Path.Combine(workspace.DataRoot, "instances", mod.Id.ToString(), "presets", presetId.ToString());
        var framework = Path.Combine(workspace.LibraryRoot, "plugin-packages", "modsharp", "git224");
        Directory.CreateDirectory(Path.Combine(framework, "files", "sharp", "bin"));
        Directory.CreateDirectory(Path.Combine(framework, "files", "sharp", "modules", "AdminManager"));
        Directory.CreateDirectory(Path.Combine(framework, "files", "sharp", "configs"));
        File.WriteAllText(Path.Combine(framework, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"modsharp\",\"Name\":\"ModSharp\",\"Version\":\"git224\"}");
        File.WriteAllText(Path.Combine(framework, "files", "sharp", "bin", "libmodsharp.so"), "loader");
        File.WriteAllText(Path.Combine(framework, "files", "sharp", "modules", "AdminManager", "AdminManager.dll"), "built in");
        File.WriteAllText(Path.Combine(framework, "files", "sharp", "configs", "core.json"), "{}");
        var join = Path.Combine(workspace.LibraryRoot, "plugin-packages", "joinsound", "1.2");
        Directory.CreateDirectory(Path.Combine(join, "files", "sharp", "modules", "JoinSound"));
        File.WriteAllText(Path.Combine(join, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"joinsound\",\"Name\":\"JoinSound\",\"Version\":\"1.2\"}");
        File.WriteAllText(Path.Combine(join, "files", "sharp", "modules", "JoinSound", "JoinSound.dll"), "managed plugin");
        var plugins = new LocalPluginStore();
        var selected = plugins.Execute(workspace, new(PluginAction.List, mod.Id, presetId));
        plugins.Execute(workspace, new(PluginAction.Save, mod.Id, presetId, mod.Definition.Revision, selected.Hash,
            [new("modsharp", "git224"), new("joinsound", "1.2")]));
        service.Deploy(workspace, Options(mod));
        var dataModule = Path.Combine(presetPath, "framework-data", "modsharp", "modules", "JoinSound");
        var runtimeModules = Path.Combine(Runtime(mod), "game", "sharp", "modules");
        Check(File.Exists(Path.Combine(dataModule, "JoinSound.dll")), "ModSharp plugin was not written to framework-data.");
        Check(new DirectoryInfo(Path.Combine(runtimeModules, "JoinSound")).LinkTarget == dataModule, "ModSharp plugin directory is not a persistent link.");
        Check(Directory.Exists(Path.Combine(runtimeModules, "AdminManager")) && new DirectoryInfo(Path.Combine(runtimeModules, "AdminManager")).LinkTarget is null,
            "ModSharp built-in module must remain a real directory.");
        Directory.CreateDirectory(Path.Combine(presetPath, "framework-data", "modsharp", "modules", "UserMod"));
        File.WriteAllText(Path.Combine(presetPath, "framework-data", "modsharp", "modules", "UserMod", "UserMod.dll"), "user module");
        service.Deploy(workspace, Options(mod));
        Check(File.Exists(Path.Combine(runtimeModules, "UserMod", "UserMod.dll")), "User-owned ModSharp module was not linked into the runtime.");
        File.WriteAllText(Path.Combine(presetPath, "framework-data", "modsharp", "modules", "UserMod", "settings.json"), "user edited");
        service.Deploy(workspace, Options(mod));
        Check(File.ReadAllText(Path.Combine(runtimeModules, "UserMod", "settings.json")) == "user edited", "ModSharp user data was overwritten on redeploy.");
        var off = plugins.Execute(workspace, new(PluginAction.List, mod.Id, presetId));
        plugins.Execute(workspace, new(PluginAction.Save, mod.Id, presetId, mod.Definition.Revision, off.Hash, [new("modsharp", "git224")]));
        service.Deploy(workspace, Options(mod));
        Check(!Directory.Exists(Path.Combine(runtimeModules, "JoinSound")) && File.Exists(Path.Combine(runtimeModules, "UserMod", "UserMod.dll")),
            "Disabling a managed module must remove its runtime link without removing user modules.");
        Directory.CreateDirectory(Path.Combine(runtimeModules, "LegacyMod"));
        File.WriteAllText(Path.Combine(runtimeModules, "LegacyMod", "LegacyMod.dll"), "legacy module");
        service.Deploy(workspace, Options(mod));
        var legacyData = Path.Combine(presetPath, "framework-data", "modsharp", "modules", "LegacyMod");
        Check(File.Exists(Path.Combine(legacyData, "LegacyMod.dll")), "Legacy real module was not migrated into framework-data.");
        Check(new DirectoryInfo(Path.Combine(runtimeModules, "LegacyMod")).LinkTarget == legacyData, "Migrated module is not a persistent link.");

        Directory.CreateDirectory(Path.Combine(runtimeModules, "SwitchAwayMod"));
        File.WriteAllText(Path.Combine(runtimeModules, "SwitchAwayMod", "SwitchAwayMod.dll"), "switch-away module");
        var swiftPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "swiftlys2", "1.5.1");
        Directory.CreateDirectory(Path.Combine(swiftPackage, "files", "csgo", "addons", "swiftlys2", "bin", "linuxsteamrt64"));
        File.WriteAllText(Path.Combine(swiftPackage, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"swiftlys2\",\"Name\":\"SwiftlyS2\",\"Version\":\"1.5.1\"}");
        File.WriteAllText(Path.Combine(swiftPackage, "files", "csgo", "addons", "swiftlys2", "bin", "linuxsteamrt64", "swiftlys2.so"), "loader");
        var switched = plugins.Execute(workspace, new(PluginAction.List, mod.Id, presetId));
        plugins.Execute(workspace, new(PluginAction.Save, mod.Id, presetId, mod.Definition.Revision, switched.Hash,
            [new("swiftlys2", "1.5.1"), new("modsharp", "git224", false), new("joinsound", "1.2", false)]));
        service.Deploy(workspace, Options(mod));
        Check(File.Exists(Path.Combine(presetPath, "framework-data", "modsharp", "modules", "SwitchAwayMod", "SwitchAwayMod.dll")),
            "A real user module was lost when switching away from ModSharp.");
        Check(!Directory.Exists(Path.Combine(presetPath, "framework-data", "modsharp", "modules", "AdminManager")),
            "A built-in ModSharp module was parked as user data.");

        var back = plugins.Execute(workspace, new(PluginAction.List, mod.Id, presetId));
        plugins.Execute(workspace, new(PluginAction.Save, mod.Id, presetId, mod.Definition.Revision, back.Hash,
            [new("modsharp", "git224"), new("swiftlys2", "1.5.1", false), new("joinsound", "1.2", false)]));
        service.Deploy(workspace, Options(mod));
        Check(new DirectoryInfo(Path.Combine(runtimeModules, "SwitchAwayMod")).LinkTarget == Path.Combine(presetPath, "framework-data", "modsharp", "modules", "SwitchAwayMod"),
            "A parked ModSharp module was not linked after switching back.");
    });
    Test("Swiftly materializes addons and parks direct user files across framework switches", () =>
    {
        var swift = store.Execute(workspace, new(InstanceAction.Create, Name: "Swiftly", GamePort: 27805)).Instances.Single(e => e.Definition!.Name == "Swiftly");
        var swiftPresetId = swift.Definition!.SelectedPresetId;
        var swiftPreset = Path.Combine(workspace.DataRoot, "instances", swift.Id.ToString(), "presets", swiftPresetId.ToString());
        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "swiftlys2", "1.5.1");
        Directory.CreateDirectory(Path.Combine(package, "files", "csgo", "addons", "swiftlys2", "bin", "linuxsteamrt64"));
        Directory.CreateDirectory(Path.Combine(package, "files", "csgo", "addons", "swiftlys2", "configs"));
        File.WriteAllText(Path.Combine(package, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"swiftlys2\",\"Name\":\"SwiftlyS2\",\"Version\":\"1.5.1\"}");
        File.WriteAllText(Path.Combine(package, "files", "csgo", "addons", "swiftlys2", "bin", "linuxsteamrt64", "swiftlys2.so"), "loader");
        File.WriteAllText(Path.Combine(package, "files", "csgo", "addons", "swiftlys2", "configs", "core.example.jsonc"), "package config");
        var plugins = new LocalPluginStore();
        var selected = plugins.Execute(workspace, new(PluginAction.List, swift.Id, swiftPresetId));
        plugins.Execute(workspace, new(PluginAction.Save, swift.Id, swiftPresetId, swift.Definition.Revision, selected.Hash, [new("swiftlys2", "1.5.1")]));
        var swiftPlan = service.Plan(workspace, Options(swift));
        Check(swiftPlan.Framework == "swiftlys2", "Swiftly framework not planned");
        Check(swiftPlan.PersistentMappings.Single(m => m.RuntimePath == "game/csgo/addons").Kind == DeploymentMappingKind.Materialized, "Swiftly addons are not materialized");
        Check(swiftPlan.PersistentMappings.Any(m => m.RuntimePath == "game/csgo/addons/swiftlys2/plugins"), "Swiftly plugins path not persistent");
        service.Deploy(workspace, Options(swift));
        var addons = Path.Combine(Runtime(swift), "game/csgo/addons");
        Check(Directory.Exists(addons) && new DirectoryInfo(addons).LinkTarget is null, "Swiftly addons must be a real directory");
        Check(File.Exists(Path.Combine(addons, "swiftlys2/bin/linuxsteamrt64/swiftlys2.so")), "Swiftly package payload missing");
        Check(new DirectoryInfo(Path.Combine(addons, "swiftlys2/plugins")).LinkTarget is not null, "Swiftly plugins must be persistent");
        var hanPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "hanairdrops2", "1.0.0");
        Directory.CreateDirectory(Path.Combine(hanPackage, "files", "csgo", "addons", "swiftlys2", "plugins", "HanAirDropS2"));
        File.WriteAllText(Path.Combine(hanPackage, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"hanairdrops2\",\"Name\":\"HanAirDropS2\",\"Version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(hanPackage, "files", "csgo", "addons", "swiftlys2", "plugins", "HanAirDropS2", "HanAirDropS2.dll"), "managed plugin");
        var withPlugin = plugins.Execute(workspace, new(PluginAction.List, swift.Id, swiftPresetId));
        plugins.Execute(workspace, new(PluginAction.Save, swift.Id, swiftPresetId, swift.Definition.Revision, withPlugin.Hash, [new("swiftlys2", "1.5.1"), new("hanairdrops2", "1.0.0")]));
        service.Deploy(workspace, Options(swift));
        Check(File.Exists(Path.Combine(swiftPreset, "framework-data", "swiftlys2", "plugins", "HanAirDropS2", "HanAirDropS2.dll")), "Managed Swiftly plugin not written to framework-data");
        Directory.CreateDirectory(Path.Combine(swiftPreset, "addons", "swiftlys2", "plugins"));
        Directory.CreateDirectory(Path.Combine(swiftPreset, "addons", "swiftlys2", "configs"));
        File.WriteAllText(Path.Combine(swiftPreset, "addons", "swiftlys2", "plugins", "direct.smx"), "direct plugin");
        File.WriteAllText(Path.Combine(swiftPreset, "addons", "swiftlys2", "configs", "direct.json"), "direct config");
        var off = plugins.Execute(workspace, new(PluginAction.List, swift.Id, swiftPresetId));
        plugins.Execute(workspace, new(PluginAction.Save, swift.Id, swiftPresetId, swift.Definition.Revision, off.Hash, []));
        service.Deploy(workspace, Options(swift));
        Check(!File.Exists(Path.Combine(swiftPreset, "addons", "swiftlys2", "plugins", "direct.smx")), "Direct plugin stayed active");
        Check(File.Exists(Path.Combine(swiftPreset, "framework-data", "swiftlys2", "plugins", "direct.smx")), "Direct plugin was not parked");
        Check(File.Exists(Path.Combine(swiftPreset, "framework-data", "swiftlys2", "configs", "direct.json")), "Direct config was not parked");
        Check(!File.Exists(Path.Combine(swiftPreset, "framework-data", "swiftlys2", "plugins", "HanAirDropS2", "HanAirDropS2.dll")), "Disabled managed plugin stayed active");
        var back = plugins.Execute(workspace, new(PluginAction.List, swift.Id, swiftPresetId));
        plugins.Execute(workspace, new(PluginAction.Save, swift.Id, swiftPresetId, swift.Definition.Revision, back.Hash, [new("swiftlys2", "1.5.1"), new("hanairdrops2", "1.0.0")]));
        service.Deploy(workspace, Options(swift));
        Check(File.Exists(Path.Combine(Runtime(swift), "game/csgo/addons/swiftlys2/plugins/direct.smx")), "Parked plugin was not restored when switching back");
        Check(File.Exists(Path.Combine(Runtime(swift), "game/csgo/addons/swiftlys2/plugins/HanAirDropS2/HanAirDropS2.dll")), "Managed plugin was not restored");
    });
    Test("workspace lease excludes deploy and source remains byte-identical", () =>
    {
        using (var lease = new LocalWorkspaceManager().Acquire(workspace))
        { try { service.Deploy(workspace, Options(first)); throw new Exception("lease bypassed"); } catch (WorkspaceException e) when (e.Code == "HCSL-LOCK") { } }
        foreach (var pair in sourceHashes) Check(Hash(pair.Key) == pair.Value, "source changed: " + pair.Key);
    });
    Console.WriteLine($"{passed} offline deployment test groups passed."); return 0;
}
finally
{
    Environment.SetEnvironmentVariable("XDG_STATE_HOME", oldState);
    // Remove our own controlled mapping links before recursive fixture cleanup.
    if (Directory.Exists(root))
    {
        void Unlink(string dir)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (new DirectoryInfo(entry).LinkTarget != null) { if (Directory.Exists(entry)) Directory.Delete(entry); else File.Delete(entry); }
                else if (Directory.Exists(entry)) Unlink(entry);
            }
        }
        Unlink(root); Directory.Delete(root, true);
    }
}
