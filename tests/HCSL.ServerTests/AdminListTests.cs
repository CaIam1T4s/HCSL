using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

/// <summary>CounterStrikeSharp admin list: read, edit, conflict, and comment preservation.</summary>
public static class AdminListTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (InstanceException e) when (e.Code == code) { }
        }

        // Identity formats accepted by the framework are normalised to SteamID64.
        Check(AdminRules.SteamId("76561198000000000") == "76561198000000000", "SteamID64 passthrough.");
        Check(AdminRules.SteamId("STEAM_0:0:19867136") == "76561198000000000", "STEAM_X:Y:Z conversion.");
        Check(AdminRules.SteamId("[U:1:39734272]") == "76561198000000000", "[U:1:N] conversion.");
        Reject(() => AdminRules.SteamId("7656119919085438"), "HCSL-ADMIN-ID");
        Reject(() => AdminRules.SteamId("player"), "HCSL-ADMIN-ID");
        Reject(() => AdminRules.Groups(["bad\"group"]), "HCSL-ADMIN-GROUP");
        Reject(() => AdminRules.Flags(Enumerable.Range(0, AdminRules.MaxFlagsPerEntry + 1).Select(i => "f" + i)), "HCSL-ADMIN-LIMIT");

        var root = Path.Combine(Path.GetTempPath(), "hcsl-admins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            using (new LocalWorkspaceManager().Acquire(workspace)) { }
            var instanceId = Guid.NewGuid();
            var presetId = Guid.NewGuid();
            var preset = Path.Combine(workspace.DataRoot, "instances", instanceId.ToString("D"), "presets", presetId.ToString("D"));
            var configs = Path.Combine(preset, "addons", "counterstrikesharp", "configs");
            Directory.CreateDirectory(configs);
            var plugins = Path.Combine(preset, "plugins.json");
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":1,"Plugins":[{"Id":"counterstrikesharp","Version":"1.0.379","Enabled":true}]}""");

            var store = new LocalAdminStore();
            AdminCommand Cmd(AdminAction action, string? hash = null, AdminMutation[]? mutations = null, bool initGroups = false) =>
                new(action, instanceId, presetId, hash, mutations, initGroups);
            const string steamId = "76561198000000000";

            // 1) Empty source: no files yet, but the panel is available and says why it is empty.
            var empty = store.Execute(workspace, Cmd(AdminAction.List));
            Check(empty.Snapshot.SourceId == "cssharp" && empty.Snapshot.Entries.Length == 0 && empty.Snapshot.Groups.Length == 0, "Empty snapshot.");
            Check(empty.Snapshot.Warnings.Any(w => w.Contains("admins.json", StringComparison.Ordinal)), "Missing file not reported.");

            // 2) First grant with init-groups: creates the conservative group, not root.
            var granted = store.Execute(workspace, Cmd(AdminAction.Apply, empty.Snapshot.Hash,
                [new(AdminMutationKind.Upsert, steamId, "Player", [AdminRules.DefaultGroupName])], initGroups: true));
            Check(granted.Snapshot.Entries.Length == 1 && granted.Snapshot.Entries[0].SteamId64 == steamId
                && granted.Snapshot.Entries[0].DisplayName == "Player", "Grant did not persist.");
            Check(granted.Snapshot.Groups.Any(g => g.Name == AdminRules.DefaultGroupName), "init-groups did not create the group.");
            Check(granted.Snapshot.Groups.All(g => !g.Flags.Contains("@css/root")), "Default group must not include root.");
            Check(granted.Snapshot.Groups.SelectMany(g => g.Flags).Contains("@custom/prac"), "Default group must allow MatchZy practice mode.");
            var adminsFile = Path.Combine(configs, "admins.json");
            Check(File.Exists(adminsFile) && File.ReadAllText(adminsFile).Contains(steamId, StringComparison.Ordinal), "admins.json not written.");
            Check(granted.Applied.Length == 1 && granted.Code == "HCSL-ADMIN-WRITTEN", "Applied summary.");

            // 2b) The entry key is the display name: renaming replaces the key, and the SteamID stays in identity.
            var namedHash = store.Execute(workspace, Cmd(AdminAction.List)).Snapshot.Hash;
            var renamed = store.Execute(workspace, Cmd(AdminAction.Apply, namedHash,
                [new(AdminMutationKind.Upsert, steamId, "Renamed Admin", [AdminRules.DefaultGroupName])]));
            var renamedText = File.ReadAllText(adminsFile);
            Check(renamed.Snapshot.Entries.Single().DisplayName == "Renamed Admin", "Display name was not applied as the key.");
            Check(renamedText.Contains("\"Renamed Admin\"") && !renamedText.Contains("\"Player\""), "Old key was not replaced.");
            Check(renamedText.Contains("\"identity\": \"" + steamId + "\""), "SteamID must stay in identity.");
            var keptHash = store.Execute(workspace, Cmd(AdminAction.List)).Snapshot.Hash;
            store.Execute(workspace, Cmd(AdminAction.Apply, keptHash,
                [new(AdminMutationKind.SetGroups, steamId, null, [AdminRules.DefaultGroupName], ["@css/ban"])]));
            Check(File.ReadAllText(adminsFile).Contains("\"Renamed Admin\""), "Editing without a name changed the key.");

            // 3) A hand-written comment must survive later edits byte-for-byte.
            var withComment = File.ReadAllText(adminsFile).Replace("{\n", "{\n  // managed by hand\n", StringComparison.Ordinal);
            File.WriteAllText(adminsFile, withComment);
            var listed = store.Execute(workspace, Cmd(AdminAction.List));
            var updated = store.Execute(workspace, Cmd(AdminAction.Apply, listed.Snapshot.Hash,
                [new(AdminMutationKind.SetGroups, steamId, null, [AdminRules.DefaultGroupName], ["@css/ban"])]));
            Check(File.ReadAllText(adminsFile).Contains("// managed by hand", StringComparison.Ordinal), "Comment was dropped by the edit.");
            Check(Directory.Exists(Path.Combine(preset, "framework-data", "cssharp", "admin-backups")), "No backup of the previous version.");
            Check(updated.Snapshot.Entries[0].Groups.Length == 1 && updated.Snapshot.Entries[0].Flags.Contains("@css/ban"), "SetGroups/flag update missing.");

            // 4) Stale hash is refused instead of overwriting an external edit.
            Reject(() => store.Execute(workspace, Cmd(AdminAction.Apply, new string('0', 64),
                [new(AdminMutationKind.Remove, steamId)])), "HCSL-ADMIN-CONFLICT");

            // 5) Revoke removes the entry and leaves an empty object.
            var current = store.Execute(workspace, Cmd(AdminAction.List));
            var revoked = store.Execute(workspace, Cmd(AdminAction.Apply, current.Snapshot.Hash, [new(AdminMutationKind.Remove, steamId)]));
            Check(revoked.Snapshot.Entries.Length == 0, "Revoke did not remove the entry.");
            Check(!File.ReadAllText(adminsFile).Contains(steamId, StringComparison.Ordinal), "Identity still present after revoke.");
            Check(File.ReadAllText(adminsFile).Contains("// managed by hand", StringComparison.Ordinal), "Comment lost on revoke.");

            // 6) Alternate identity formats match the same entry.
            var again = store.Execute(workspace, Cmd(AdminAction.List));
            store.Execute(workspace, Cmd(AdminAction.Apply, again.Snapshot.Hash, [new(AdminMutationKind.Upsert, "[U:1:39734272]", "Alt", ["#hcsl/admin"])]));
            var matched = store.Execute(workspace, Cmd(AdminAction.List));
            Check(matched.Snapshot.Entries.Length == 1, "Alternate identity created a duplicate entry.");

            // 6b) Editing must work while the server runs: a held workspace lease must not block the write.
            using (new LocalWorkspaceManager().Acquire(workspace))
            {
                var liveHash = store.Execute(workspace, Cmd(AdminAction.List)).Snapshot.Hash;
                var live = store.Execute(workspace, Cmd(AdminAction.Apply, liveHash,
                    [new(AdminMutationKind.Upsert, steamId, "Player", [AdminRules.DefaultGroupName])]));
                Check(live.Snapshot.Entries.Any(e => e.SteamId64 == steamId), "Admin write failed while a server lease is held.");
            }

            // 7) Large servers are paged, not capped: 300 entries still list with a cursor.
            var many = new System.Text.StringBuilder("{\n");
            for (var i = 0; i < 300; i++)
            {
                var id = (PlayerRosterRules.SteamId64Base + 1 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                many.Append("  \"Player").Append(i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("\": { \"identity\": \"").Append(id).Append("\", \"groups\": [\"#hcsl/admin\"] }")
                    .Append(i < 299 ? "," : "").Append('\n');
            }
            many.Append("}\n");
            File.WriteAllText(adminsFile, many.ToString());
            var page = store.Execute(workspace, Cmd(AdminAction.List));
            Check(page.Snapshot.Total == 300 && page.Snapshot.Entries.Length == AdminRules.DefaultPageSize && page.Snapshot.HasMore,
                $"Paging: total={page.Snapshot.Total} shown={page.Snapshot.Entries.Length} more={page.Snapshot.HasMore}");
            Check(page.Snapshot.Entries[0].DisplayName == "Player000", "Paging order is not stable.");
            Check(page.Snapshot.Capabilities is { Key: AdminKeyKind.DisplayName, Reload: AdminReloadKind.Command, ReloadCommand: "css_admins_reload" },
                "Capabilities missing from the snapshot.");
            var tail = store.Execute(workspace, new(AdminAction.List, instanceId, presetId, Offset: 200));
            Check(tail.Snapshot.Entries.Length == 100 && !tail.Snapshot.HasMore && tail.Snapshot.Entries[0].DisplayName == "Player200",
                "Second page wrong.");
            var found = store.Execute(workspace, new(AdminAction.List, instanceId, presetId, Query: "Player299"));
            Check(found.Snapshot.Total == 1 && found.Snapshot.Entries[0].DisplayName == "Player299", "Query by name.");
            var byId = store.Execute(workspace, new(AdminAction.List, instanceId, presetId, Query: "76561197960265729"));
            Check(byId.Snapshot.Total == 1, "Query by SteamID64.");
            var byGroup = store.Execute(workspace, new(AdminAction.List, instanceId, presetId, Query: "#hcsl/admin"));
            Check(byGroup.Snapshot.Total == 300, "Query by group.");
            Check(AdminRules.Page(0, 0).Limit == AdminRules.DefaultPageSize && AdminRules.Page(0, 500).Limit == AdminRules.MaxPageSize,
                "Page window bounds.");
            try { AdminRules.Page(-1, 10); throw new Exception("Negative offset accepted."); }
            catch (InstanceException e) when (e.Code == "HCSL-ADMIN-LIMIT") { }
            Check(AdminRules.Capabilities("unknown") is null, "Unknown source reported capabilities.");

            // 8) A preset without CounterStrikeSharp has no admin source at all.
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":2,"Plugins":[{"Id":"counterstrikesharp","Version":"1.0.379","Enabled":false}]}""");
            Reject(() => store.Execute(workspace, Cmd(AdminAction.List)), "HCSL-ADMIN-SOURCE");

            // 9) An entry without a usable identity (no "identity" field) is removable by its raw key.
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":4,"Plugins":[{"Id":"counterstrikesharp","Version":"1.0.379","Enabled":true}]}""");
            File.WriteAllText(adminsFile, "{\n  \"Placeholder\": { \"groups\": [ \"#hcsl/admin\" ] }\n}\n");
            var placeholder = store.Execute(workspace, Cmd(AdminAction.List));
            Check(placeholder.Snapshot.Entries.Single().SteamId64 == "" && placeholder.Snapshot.Entries[0].Problems.Contains("HCSL-ADMIN-ID"),
                "Entry without identity should be listed with HCSL-ADMIN-ID.");
            var byKey = store.Execute(workspace, Cmd(AdminAction.Apply, placeholder.Snapshot.Hash,
                [new(AdminMutationKind.Remove, "", Key: "Placeholder")]));
            Check(byKey.Snapshot.Entries.Length == 0 && !File.ReadAllText(adminsFile).Contains("Placeholder", StringComparison.Ordinal),
                "Key-based removal did not delete the entry.");

            // 10) Unparseable file is reported, never "repaired".
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":3,"Plugins":[{"Id":"counterstrikesharp","Version":"1.0.379","Enabled":true}]}""");
            File.WriteAllText(adminsFile, "{ \"Broken\": ");
            Reject(() => store.Execute(workspace, Cmd(AdminAction.List)), "HCSL-ADMIN-PARSE");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
