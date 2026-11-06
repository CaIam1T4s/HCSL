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
    Source("game/empty-file", "");
    Source("steamapps/appmanifest_730.acf", "\"appid\" \"730\" \"StateFlags\" \"4\" \"buildid\" \"123\"");
    var sdk = Path.Combine(workspace.DataRoot, "steamcmd/tool/linux64/steamclient.so"); Directory.CreateDirectory(Path.GetDirectoryName(sdk)!); File.WriteAllText(sdk, "fixture sdk");
    var state = Path.Combine(workspace.LibraryRoot, ".hcsl/cs2-install.json");
    File.WriteAllText(state, "{\"FormatVersion\":1,\"AppId\":730,\"Status\":\"Ready\",\"BuildId\":\"123\"}");
    var baseline = new RuntimeDeploymentService.Baseline("123", Hash(entrypoint), [new("game/bin/linuxsteamrt64/cs2", new FileInfo(native).Length, Hash(native))], ["game/csgo/gameinfo.gi", "game/csgo/steam.inf"]);
    var service = new RuntimeDeploymentService(baseline);
    DeploymentOptions Options(InstanceEntry e) => new(e.Id, DeploymentCopyMode.FullCopy);
    string Runtime(InstanceEntry e) => Path.Combine(workspace.LibraryRoot, "runtime", e.Id.ToString());
    string Preset(InstanceEntry e) => Path.Combine(workspace.DataRoot, "instances", e.Id.ToString(), "presets", e.Definition!.SelectedPresetId.ToString());
    string Journal(InstanceEntry e) => Path.Combine(workspace.LibraryRoot, ".hcsl", "deployment-" + e.Id + ".json");
    var sourceHashes = Directory.GetFiles(Path.Combine(server, "game"), "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
    Test("plan verifies without runtime or persistent-data seeding", () =>
    {
        var plan = service.Plan(workspace, Options(first));
        Check(plan.FileCount == sourceHashes.Count && plan.LogicalBytes > 0 && plan.PersistentMappings.Length == 6, "incomplete plan");
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
    Test("preset switching deploys separate writable data", () =>
    {
        first = store.Execute(workspace, new(InstanceAction.CreatePreset, first.Id, Revision: first.Definition!.Revision, Name: "Alternate")).Instances.Single(e => e.Id == first.Id);
        var alternate = first.Presets.Single(p => p.Id != first.Definition!.SelectedPresetId);
        first = store.Execute(workspace, new(InstanceAction.SelectPreset, first.Id, alternate.Id, first.Definition!.Revision)).Instances.Single(e => e.Id == first.Id);
        service.Deploy(workspace, Options(first));
        Check(!File.Exists(Path.Combine(Runtime(first), "game/csgo/addons/plugin.db")), "previous preset database reused");
    });
    foreach (var phase in new[] { "Journaled", "Cloned", "Prepared", "PreviousMoved", "Published" })
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
