using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

/// <summary>Source2Mod permissions.json: JSONC with comments, SteamID keys, groups in the same file.</summary>
public static class Source2ModAdminTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (InstanceException e) when (e.Code == code) { }
        }

        var root = Path.Combine(Path.GetTempPath(), "hcsl-s2m-admins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            using (new LocalWorkspaceManager().Acquire(workspace)) { }
            var instanceId = Guid.NewGuid();
            var presetId = Guid.NewGuid();
            var preset = Path.Combine(workspace.DataRoot, "instances", instanceId.ToString("D"), "presets", presetId.ToString("D"));
            var configs = Path.Combine(preset, "addons", "source2mod", "configs");
            Directory.CreateDirectory(configs);
            var plugins = Path.Combine(preset, "plugins.json");
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":1,"Plugins":[{"Id":"source2mod","Version":"1.0.51","Enabled":true}]}""");
            var file = Path.Combine(configs, "permissions.json");
            var store = new LocalAdminStore();
            AdminCommand Cmd(AdminAction action, string? hash = null, AdminMutation[]? mutations = null, bool initGroups = false) =>
                new(action, instanceId, presetId, hash, mutations, initGroups);
            const string steamId = "76561198000000000";

            // 1) The source is selected by the enabled framework, and declares its own shape.
            var empty = store.Execute(workspace, Cmd(AdminAction.List));
            Check(empty.Snapshot.SourceId == "source2mod" && empty.Snapshot.Entries.Length == 0, "Source2Mod source not selected.");
            Check(empty.Snapshot.Capabilities is { Key: AdminKeyKind.SteamId64, Groups: AdminGroupsLayout.SameFile,
                Reload: AdminReloadKind.Command, ReloadCommand: "s2m admins reload" }, "Source2Mod capabilities wrong.");
            Check(!File.Exists(file), "List must not create the file.");

            // 2) init-groups writes a skeleton with no invented privileges, keeping its comment.
            var skeleton = store.Execute(workspace, Cmd(AdminAction.Apply, empty.Snapshot.Hash, [], initGroups: true));
            Check(File.Exists(file), "init-groups did not create permissions.json.");
            var text = File.ReadAllText(file);
            Check(text.Contains("// Keyed by SteamID64", StringComparison.Ordinal), "Skeleton comment missing.");
            Check(skeleton.Snapshot.Groups.Length == 0 && skeleton.Snapshot.Entries.Length == 0, "Skeleton invented entries or groups.");

            // 3) First grant: SteamID64 key, groups + permissions, comment preserved.
            var granted = store.Execute(workspace, Cmd(AdminAction.Apply, skeleton.Snapshot.Hash,
                [new(AdminMutationKind.Upsert, steamId, null, ["hcsl_admin"], ["admin.kick", "admin.ban"])]));
            Check(granted.Snapshot.Entries.Length == 1 && granted.Snapshot.Entries[0].SteamId64 == steamId, "Grant not persisted.");
            Check(granted.Snapshot.Entries[0].DisplayName == granted.Snapshot.Entries[0].SteamId64,
                "Source2Mod keys by SteamID; the key and identity must agree.");
            Check(granted.Snapshot.Entries[0].Groups.SequenceEqual(["hcsl_admin"]), "Groups not stored.");
            Check(granted.Snapshot.Entries[0].Flags.SequenceEqual(["admin.kick", "admin.ban"]), "Permissions not stored.");
            var written = File.ReadAllText(file);
            Check(written.Contains("// Keyed by SteamID64", StringComparison.Ordinal), "Comment lost on grant.");
            Check(written.Contains("\"76561198000000000\"", StringComparison.Ordinal), "Entry not keyed by SteamID64.");
            Check(granted.Snapshot.Capabilities?.Key == AdminKeyKind.SteamId64, "Snapshot capabilities missing after write.");

            // 4) SetGroups keeps permissions and immunity.
            var current = store.Execute(workspace, Cmd(AdminAction.List));
            var regrouped = store.Execute(workspace, Cmd(AdminAction.Apply, current.Snapshot.Hash,
                [new(AdminMutationKind.SetGroups, steamId, null, ["root"], null, 100)]));
            Check(regrouped.Snapshot.Entries[0].Groups.SequenceEqual(["root"]), "SetGroups failed.");
            Check(regrouped.Snapshot.Entries[0].Flags.SequenceEqual(["admin.kick", "admin.ban"]), "SetGroups dropped permissions.");
            Check(regrouped.Snapshot.Entries[0].Immunity == 100, "Immunity not stored.");

            // 5) A STEAM_X:Y:Z key in the file is the same admin, not a duplicate.
            File.WriteAllText(file, "{\n  \"Groups\": {},\n  \"Admins\": {\n    \"STEAM_0:0:19867136\": { \"Groups\": [ \"vip\" ] }\n  }\n}\n");
            var alt = store.Execute(workspace, Cmd(AdminAction.List));
            Check(alt.Snapshot.Entries.Length == 1 && alt.Snapshot.Entries[0].SteamId64 == steamId, "Alternate identity not normalised.");
            var updated = store.Execute(workspace, Cmd(AdminAction.Apply, alt.Snapshot.Hash,
                [new(AdminMutationKind.SetGroups, steamId, null, ["admin"])]));
            var after = store.Execute(workspace, Cmd(AdminAction.List));
            Check(after.Snapshot.Entries.Length == 1 && after.Snapshot.Entries[0].Groups.SequenceEqual(["admin"]),
                "Edit by alternate key created a duplicate entry.");

            // 6) Groups defined in the file are exposed for the UI.
            File.WriteAllText(file, "{\n  \"Groups\": {\n    \"admin\": { \"Permissions\": [ \"admin.*\" ], \"Inherits\": [ \"vip\" ], \"Immunity\": 50 }\n  },\n  \"Admins\": {}\n}\n");
            var grouped = store.Execute(workspace, Cmd(AdminAction.List));
            Check(grouped.Snapshot.Groups.Length == 1 && grouped.Snapshot.Groups[0].Name == "admin"
                && grouped.Snapshot.Groups[0].Flags.SequenceEqual(["admin.*"]) && grouped.Snapshot.Groups[0].Immunity == 50, "Group parsing.");

            // 7) Revoke removes the entry and keeps the document valid.
            var revokeBase = store.Execute(workspace, Cmd(AdminAction.List));
            store.Execute(workspace, Cmd(AdminAction.Apply, revokeBase.Snapshot.Hash,
                [new(AdminMutationKind.Upsert, steamId, null, ["admin"])]));
            var revokeHash = store.Execute(workspace, Cmd(AdminAction.List)).Snapshot.Hash;
            var revoked = store.Execute(workspace, Cmd(AdminAction.Apply, revokeHash, [new(AdminMutationKind.Remove, steamId)]));
            Check(revoked.Snapshot.Entries.Length == 0, "Revoke failed.");
            Check(!File.ReadAllText(file).Contains(steamId, StringComparison.Ordinal), "Identity still present.");

            // 8) Stale hash is refused.
            Reject(() => store.Execute(workspace, Cmd(AdminAction.Apply, revokeHash, [new(AdminMutationKind.Remove, steamId)])), "HCSL-ADMIN-CONFLICT");

            // 8b) An entry with a non-SteamID key is removable by that raw key.
            File.WriteAllText(file, "{\n  \"Groups\": {},\n  \"Admins\": {\n    \"7656\": { \"Groups\": [ \"admin\" ] }\n  }\n}\n");
            var placeholder = store.Execute(workspace, Cmd(AdminAction.List));
            Check(placeholder.Snapshot.Entries.Single().Problems.Contains("HCSL-ADMIN-ID"), "Unparsable key not reported.");
            var byKey = store.Execute(workspace, Cmd(AdminAction.Apply, placeholder.Snapshot.Hash,
                [new(AdminMutationKind.Remove, "", Key: "7656")]));
            Check(byKey.Snapshot.Entries.Length == 0, "Key-based removal did not delete the entry.");

            // 9) A preset with neither scripting framework has no source.
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":2,"Plugins":[{"Id":"source2mod","Version":"1.0.51","Enabled":false}]}""");
            Reject(() => store.Execute(workspace, Cmd(AdminAction.List)), "HCSL-ADMIN-SOURCE");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
