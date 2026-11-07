using System.Text.Json;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
using HCSL.Application.Abstractions;
var root = Path.Combine(Path.GetTempPath(), "hcsl-instances-" + Guid.NewGuid().ToString("N"));
var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
var store = new LocalInstanceStore(); int count = 0;
InstanceCatalog Run(InstanceCommand c) => store.Execute(workspace, c);
InstanceEntry Entry() => Run(new(InstanceAction.List)).Instances.Single();
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); count++; }
void Reject(string code, Action action)
{
    try { action(); throw new Exception("Expected " + code); }
    catch (InstanceException e) when (e.Code == code) { }
}
try
{
    Test("create/reopen/default preset without server installation", () =>
    {
        var e = Run(new(InstanceAction.Create, Name: "First")).Instances.Single();
        Check(e.Definition!.Revision == 1 && e.Presets.Single().Id == e.Definition.SelectedPresetId, "invalid default");
        Check(new LocalInstanceStore().Execute(workspace, new(InstanceAction.List)).Instances.Single().Id == e.Id, "identity lost");
        Check(!File.Exists(Path.Combine(workspace.LibraryRoot, "server", "game", "cs2.sh")), "unexpected game deployment");
    });
    Test("rename preserves directory, duplicate port rejected, stale editor rejected", () =>
    {
        var d = Entry().Definition!;
        Run(new(InstanceAction.Update, d.Id, Revision: d.Revision, Name: "Renamed", GamePort: 27016));
        Check(Directory.Exists(Path.Combine(workspace.DataRoot, "instances", d.Id.ToString())), "directory moved");
        Reject("HCSL-INSTANCE-CONFLICT", () => Run(new(InstanceAction.Update, d.Id, Revision: 1, Name: "stale")));
        Reject("HCSL-INSTANCE-PORT", () => Run(new(InstanceAction.Create, Name: "Second", GamePort: 27016)));
    });
    Test("network bind address rules and persistence", () =>
    {
        var d = Entry().Definition!;
        Check(InstanceRules.Resolve(null).BindAddress == "127.0.0.1", "default must stay loopback");
        Check(InstanceRules.Resolve(new(NetworkAccessMode.Lan, "192.168.1.20")).Mode == NetworkAccessMode.Lan, "lan mode lost");
        Reject("HCSL-INSTANCE-BIND", () => InstanceRules.Network(new(NetworkAccessMode.Lan, "0.0.0.0")));
        Reject("HCSL-INSTANCE-BIND", () => InstanceRules.Network(new(NetworkAccessMode.Lan, "localhost")));
        Reject("HCSL-INSTANCE-BIND", () => InstanceRules.Network(new(NetworkAccessMode.Loopback, "192.168.1.20")));
        Reject("HCSL-INSTANCE-BIND", () => InstanceRules.Network(new(NetworkAccessMode.Lan, "127.0.0.1")));
        // Hand-edited metadata reaches Resolve without validation; it must not throw.
        Check(InstanceRules.Resolve(new(NetworkAccessMode.Lan, "localhost")).BindAddress == "localhost", "resolve must not throw");
        Reject("HCSL-INSTANCE-BIND", () => InstanceRules.Network(new(NetworkAccessMode.Lan, "localhost")));
        Run(new(InstanceAction.Update, d.Id, Revision: d.Revision, Name: "Renamed", GamePort: 27016, Network: new(NetworkAccessMode.Lan, "192.168.1.20")));
        d = Entry().Definition!;
        Check(d.Network?.Mode == NetworkAccessMode.Lan && d.Network.BindAddress == "192.168.1.20", "network not persisted");
        // A command without Network keeps the stored address: an older caller must not reset it to loopback.
        Run(new(InstanceAction.Update, d.Id, Revision: d.Revision, Name: "Renamed", GamePort: 27016));
        Check(Entry().Definition?.Network?.BindAddress == "192.168.1.20", "network was silently reset");
    });
    Test("preset copy isolates CFG and excludes plugin business data", () =>
    {
        var e = Entry(); var p = e.Presets.Single();
        var dir = Path.Combine(workspace.DataRoot, "instances", e.Id.ToString(), "presets", p.Id.ToString());
        File.WriteAllText(Path.Combine(dir, "cfg", "server.cfg"), "hostname original");
        File.WriteAllText(Path.Combine(dir, "addons", "database.db"), "private data");
        Run(new(InstanceAction.CopyPreset, e.Id, p.Id, e.Definition!.Revision, "Copy"));
        e = Entry(); var copy = e.Presets.Single(x => x.Id != p.Id);
        var dest = Path.Combine(Path.GetDirectoryName(dir)!, copy.Id.ToString());
        Check(File.ReadAllText(Path.Combine(dest, "cfg", "server.cfg")) == "hostname original", "cfg not copied");
        File.WriteAllText(Path.Combine(dest, "cfg", "server.cfg"), "changed");
        Check(File.ReadAllText(Path.Combine(dir, "cfg", "server.cfg")) == "hostname original", "cfg shared");
        Check(!File.Exists(Path.Combine(dest, "addons", "database.db")), "business data copied");
    });
    Test("select/update/delete preset invariants and recycle", () =>
    {
        var e = Entry(); var d = e.Definition!;
        Reject("HCSL-PRESET-SELECTED", () => Run(new(InstanceAction.DeletePreset, e.Id, d.SelectedPresetId, d.Revision)));
        var p = e.Presets.Single(x => x.Id != d.SelectedPresetId);
        Run(new(InstanceAction.SelectPreset, e.Id, p.Id, d.Revision));
        e = Entry();
        Run(new(InstanceAction.UpdatePreset, e.Id, p.Id, e.Definition!.Revision, Preset: p with { Name = "Edited", Map = "de_inferno" }));
        e = Entry(); Check(e.Presets.Single(x => x.Id == p.Id).Map == "de_inferno", "update not persisted");
        Run(new(InstanceAction.DeletePreset, e.Id, d.SelectedPresetId, e.Definition!.Revision));
        Check(Entry().Presets.Length == 1 && Directory.GetDirectories(Path.Combine(workspace.DataRoot, "instances", ".trash")).Length == 1, "recycle missing");
    });
    Test("interrupted staging ignored and malformed metadata visible without overwrite", () =>
    {
        var e = Entry(); var dir = Path.Combine(workspace.DataRoot, "instances", e.Id.ToString());
        Directory.CreateDirectory(Path.Combine(workspace.DataRoot, "instances", ".creating-abandoned"));
        var file = Path.Combine(dir, "instance.json"); var original = File.ReadAllText(file);
        File.WriteAllText(file, "{broken"); Check(Entry().Error == "HCSL-INSTANCE-CORRUPT", "corruption hidden");
        Check(File.ReadAllText(file) == "{broken", "corruption overwritten"); File.WriteAllText(file, original);
    });
    Test("workspace lease keeps listing readable but excludes mutations", () =>
    {
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        Check(Run(new(InstanceAction.List)).Instances.Length > 0, "list must stay readable while a session holds the lease");
        try { Run(new(InstanceAction.Create, Name: "Blocked", GamePort: 27999)); throw new Exception("lease bypassed"); }
        catch (WorkspaceException e) { Check(e.Code == "HCSL-LOCK", "unexpected " + e.Code); }
    });
    if (OperatingSystem.IsLinux()) Test("linked metadata and copied CFG rejected", () =>
    {
        var e = Entry(); var dir = Path.Combine(workspace.DataRoot, "instances", e.Id.ToString(), "presets", e.Presets[0].Id.ToString(), "cfg");
        var external = Path.Combine(root, "external"); File.WriteAllText(external, "outside");
        File.CreateSymbolicLink(Path.Combine(dir, "linked.cfg"), external);
        Reject("HCSL-INSTANCE-PATH", () => Run(new(InstanceAction.CopyPreset, e.Id, e.Presets[0].Id, e.Definition!.Revision, "unsafe")));
        Check(File.ReadAllText(external) == "outside", "external file modified"); File.Delete(Path.Combine(dir, "linked.cfg"));
    });
    Test("deleting instance preserves all data in recycle area", () =>
    {
        var e = Entry(); Run(new(InstanceAction.Delete, e.Id, Revision: e.Definition!.Revision));
        Check(Run(new(InstanceAction.List)).Instances.Length == 0, "instance still active");
        Check(Directory.GetFiles(Path.Combine(workspace.DataRoot, "instances", ".trash"), "instance.json", SearchOption.AllDirectories).Length == 1, "instance data lost");
    });
    Console.WriteLine($"{count} instance test groups passed."); return 0;
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
