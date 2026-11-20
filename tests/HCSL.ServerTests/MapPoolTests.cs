using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;
public static class MapPoolTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (InstanceException e) when (e.Code == code) { }
            catch (WorkspaceException e) when (e.Code == code) { }
        }
        var root = Path.Combine(Path.GetTempPath(), "hcsl-map-pool-" + Guid.NewGuid());
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            var instances = new LocalInstanceStore(); var store = new LocalPresetMapPoolStore();
            var entry = instances.Execute(workspace, new(InstanceAction.Create, Name: "Pool")).Instances.Single();
            var d = entry.Definition!; var p = entry.Presets.Single();
            MapPoolCommand Read() => new(MapPoolAction.Read, d.Id, p.Id);
            var first = store.Execute(workspace, Read()); Check(first.Pool.Revision == 0 && first.Hash is null && first.Pool.Maps.Length == 0, "Old preset default.");
            var draft = new MapPoolDraft(); draft.Load(first);
            draft.Add(MapPoolRules.Parse("standard de_dust2")); draft.Add(MapPoolRules.Parse("workshop 12345"));
            Reject(() => draft.Add(MapPoolRules.Parse("standard DE_DUST2")), "HCSL-MAPPOOL-DUPLICATE");
            Reject(() => draft.Add(new(ServerMapKind.Standard, "de_dust2;quit")), "HCSL-MAP-INVALID");
            Reject(() => draft.Add(MapPoolRules.Parse("https://example.com/12345")), "HCSL-MAPPOOL-INPUT");
            Check(draft.Maps.Count == 2 && draft.Dirty && draft.Move(0, -1) == 0, "Invalid edit changed draft/bounds.");
            Check(draft.Move(1, -1) == 0 && draft.Maps[0].Kind == ServerMapKind.Workshop, "Reorder failed.");
            var command = draft.SaveCommand(d.Id, p.Id, d.Revision);
            var saved = store.Execute(workspace, command); draft.Load(saved);
            Check(!draft.Dirty && saved.Pool.Revision == 1 && store.Execute(workspace, Read()).Pool.Maps.SequenceEqual(saved.Pool.Maps), "Roundtrip.");
            Reject(() => store.Execute(workspace, command), "HCSL-MAPPOOL-CONFLICT");
            Reject(() => store.Execute(workspace, draft.SaveCommand(d.Id, p.Id, d.Revision + 1)), "HCSL-INSTANCE-STALE");
            var unchanged = instances.Execute(workspace, new(InstanceAction.List)).Instances.Single();
            Check(unchanged.Presets.Single().Map == p.Map && unchanged.Definition!.Revision == d.Revision, "Pool changed startup/preset revision.");
            using (new LocalWorkspaceManager().Acquire(workspace))
            {
                Check(store.Execute(workspace, Read()).Pool.Revision == 1, "Read took management lock.");
                Reject(() => store.Execute(workspace, draft.SaveCommand(d.Id, p.Id, d.Revision)), "HCSL-LOCK");
            }
            var copied = instances.Execute(workspace, new(InstanceAction.CopyPreset, d.Id, p.Id, d.Revision, "Copy")).Instances.Single();
            var other = copied.Presets.Single(x => x.Id != p.Id);
            Check(store.Execute(workspace, new(MapPoolAction.Read, d.Id, other.Id)).Pool.Maps.SequenceEqual(saved.Pool.Maps), "Copy lost maps.");
            var next = store.Execute(workspace, draft.SaveCommand(d.Id, p.Id, copied.Definition!.Revision) with { Maps = [] });
            Check(next.Pool.Maps.Length == 0 && store.Execute(workspace, new(MapPoolAction.Read, d.Id, other.Id)).Pool.Maps.Length == 2, "Copies share mutable storage.");
            var path = Path.Combine(workspace.DataRoot, "instances", d.Id.ToString("D"), "presets", p.Id.ToString("D"), "maps.json");
            var baseline = File.ReadAllBytes(path);
            File.WriteAllText(path, "{\"formatVersion\":1,\"revision\":2,\"maps\":[{\"kind\":\"standard\",\"value\":\"de_nuke\"}]}");
            Reject(() => store.Execute(workspace, new(MapPoolAction.Save, d.Id, p.Id, copied.Definition.Revision, next.Pool.Revision, next.Hash, [])), "HCSL-MAPPOOL-CONFLICT");
            File.WriteAllText(path, new string('x', MapPoolRules.MaxBytes + 1)); Reject(() => store.Execute(workspace, Read()), "HCSL-MAPPOOL-LIMIT");
            File.WriteAllText(path, "{}"); Reject(() => store.Execute(workspace, Read()), "HCSL-MAPPOOL-INVALID");
            File.WriteAllBytes(path, baseline);
            if (OperatingSystem.IsLinux())
            {
                var target = path + ".outside"; File.Move(path, target); File.CreateSymbolicLink(path, target);
                try { store.Execute(workspace, Read()); throw new Exception("Linked maps accepted."); }
                catch (WorkspaceException) { }
                File.Delete(path); File.Move(target, path);
                var parent = Path.GetDirectoryName(path)!; var moved = parent + ".real";
                Directory.Move(parent, moved); Directory.CreateSymbolicLink(parent, moved);
                try { Reject(() => store.Execute(workspace, Read()), "HCSL-INSTANCE-PATH"); }
                finally { Directory.Delete(parent); Directory.Move(moved, parent); }
            }
            var many = Enumerable.Range(0, MapPoolRules.MaxMaps).Select(i => new ServerMapTarget(ServerMapKind.Standard, "de_" + new string('a', 90) + i.ToString("D3"))).ToArray();
            MapPoolRules.Validate(new(1, 0, many));
            var frame = new LocalResponse(LocalProtocol.Version, Guid.NewGuid(), true, MapPoolResultJson: JsonSerializer.Serialize(new MapPoolResult(new(1, 1, many), "hash")));
            Check(JsonSerializer.SerializeToUtf8Bytes(frame).Length < LocalProtocol.MaxFrameBytes - 1024, "Worst-case response frame too large.");
            Reject(() => MapPoolRules.Validate(new(1, 0, many.Append(new(ServerMapKind.Standard, "de_extra")).ToArray())), "HCSL-MAPPOOL-INVALID");
            Reject(NativeMapRotation.RequireVerified, "HCSL-ROTATION-UNVERIFIED");
            draft.Remove(0); Check(draft.Maps.Count == 1 && draft.Dirty, "Remove failed.");
            // Rotation support is part of the catalog's promise: only built-in pools and a single collection rotate.
            Check(MapPoolRules.Rotation(new(1, 0, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage")])) == MapPoolRotation.MapGroup,
                "An all built-in pool must rotate through the launcher's map group.");
            Check(MapPoolRules.Rotation(new(1, 0, [new(ServerMapKind.WorkshopCollection, "123")])) == MapPoolRotation.Collection,
                "A single collection must rotate natively.");
            Check(MapPoolRules.Rotation(new(1, 0, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Workshop, "123")])) == MapPoolRotation.Manual,
                "A pool holding a single Workshop map must be manual.");
            Check(MapPoolRules.Rotation(new(1, 0, [new(ServerMapKind.Standard, "de_dust2")])) == MapPoolRotation.Manual,
                "A one-map pool has nothing to rotate.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        // Catalog: built-in files, downloaded Workshop items, and folders that exist without a map.
        var catalog = MapCatalogScanner.Build(
        [
            new MapFile("maps/de_dust2.bsp", 100, DateTimeOffset.UnixEpoch),
            new MapFile("maps/notes.txt", 10, null),
            new MapFile("maps/workshop/3070244462/de_akizuki.bsp", 200, null),
            new MapFile("maps/workshop/3070244462/de_second.bsp", 200, null),
            new MapFile("maps/workshop/3120456789/", 0, null)
        ]);
        MapCatalogRules.Validate(catalog);
        Check(catalog.Entries.Length == 3, "Catalog must list one built-in, one downloaded Workshop item and one incomplete download.");
        Check(catalog.Entries.Count(e => e.Source == MapCatalogSource.Standard) == 1, "Only .bsp files become built-in entries.");
        var downloaded = catalog.Entries.Single(e => e.WorkshopId == "3070244462");
        Check(downloaded.Incomplete == false && downloaded.SizeBytes == 200, "A Workshop item with a map must count as downloaded once.");
        Check(catalog.Entries.Single(e => e.WorkshopId == "3120456789").Incomplete, "A Workshop folder without a .bsp must be marked incomplete.");
        Check(catalog.Entries[0].Key == "standard:de_dust2" && MapCatalogRules.KeyOf(new(ServerMapKind.Workshop, "7")) == "workshop:7",
            "Catalog keys must match map targets so membership can be compared.");
        Check(MapCatalogRules.MembershipCount("standard:de_dust2", [("A", [new ServerMapTarget(ServerMapKind.Standard, "de_dust2")]), ("B", [])]) == 1,
            "Pool membership must count the pools holding a map.");
        Check(MapCatalogScanner.Build([new MapFile("maps/workshop/01/bad.bsp", 1, null)]).Entries.Length == 0,
            "An invalid Workshop folder name must not become a catalog entry.");
        Check(MapCatalogScanner.Scan(Path.Combine(root, "missing-game")).Entries.Length == 0, "A missing game directory is an empty catalog.");
        // Instance-level pool library: shared by presets, delete unbinds, names must be unique.
        var a = new MapPoolRecord(Guid.NewGuid(), "比赛池", [new ServerMapTarget(ServerMapKind.Standard, "de_dust2")]);
        var b = new MapPoolRecord(Guid.NewGuid(), "练习池", [new ServerMapTarget(ServerMapKind.Workshop, "3070244462")]);
        var presetA = Guid.NewGuid(); var presetB = Guid.NewGuid();
        var library = new MapPoolLibrary(1, 0, [a, b], new() { [presetA] = a.Id, [presetB] = b.Id });
        MapPoolLibraryRules.Validate(library);
        Check(library.PoolOf(presetA)?.Name == "比赛池" && library.PoolOf(presetB)?.Name == "练习池", "Presets must resolve their assigned pool.");
        Check(library.PoolOf(Guid.NewGuid()) is null, "An unassigned preset has no pool.");
        var afterDelete = MapPoolLibraryRules.Delete(library, a.Id, out var unbound);
        Check(afterDelete.Pools.Length == 1 && unbound.SequenceEqual([presetA]) && afterDelete.PoolOf(presetA) is null,
            "Deleting a pool must unbind every preset that used it.");
        Reject(() => MapPoolLibraryRules.Validate(new(1, 0, [a, a with { Name = "另一个" }], new())), "HCSL-MAPPOOL-LIBRARY");
        Reject(() => MapPoolLibraryRules.Validate(new(1, 0, [a, a with { Id = Guid.NewGuid() }], new())), "HCSL-MAPPOOL-NAME");
        Reject(() => MapPoolLibraryRules.Validate(new(1, 0, [a], new() { [presetA] = Guid.NewGuid() })), "HCSL-MAPPOOL-LIBRARY");
        Reject(() => MapPoolLibraryRules.Validate(new(1, 0, [a with { Name = " " }], new())), "HCSL-MAPPOOL-NAME");
        Check(!MapPoolLibraryRules.Names.Valid(new string('x', 41)) && MapPoolLibraryRules.Names.Valid("比赛池"), "Pool name bounds changed.");
        Console.WriteLine("PASS maps.pool-legacy-crud-order-dedup-revisions-hash-copy-isolation-size-link-lock-frame-and-unverified-rotation-gate");
    }
}
