using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

/// <summary>SwiftlyS2 permissions.jsonc: SteamID keys, group membership only, native reader (restart to apply).</summary>
public static class SwiftlyS2AdminTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (InstanceException e) when (e.Code == code) { }
        }

        var root = Path.Combine(Path.GetTempPath(), "hcsl-sw-admins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            using (new LocalWorkspaceManager().Acquire(workspace)) { }
            var instanceId = Guid.NewGuid();
            var presetId = Guid.NewGuid();
            var preset = Path.Combine(workspace.DataRoot, "instances", instanceId.ToString("D"), "presets", presetId.ToString("D"));
            var configs = Path.Combine(preset, "framework-data", "swiftlys2", "configs");
            Directory.CreateDirectory(configs);
            var plugins = Path.Combine(preset, "plugins.json");
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":1,"Plugins":[{"Id":"swiftlys2","Version":"1.5.2","Enabled":true}]}""");
            var file = Path.Combine(configs, "permissions.jsonc");
            var store = new LocalAdminStore();
            AdminCommand Cmd(AdminAction action, string? hash = null, AdminMutation[]? mutations = null, bool initGroups = false) =>
                new(action, instanceId, presetId, hash, mutations, initGroups);
            const string steamId = "76561198000000000";

            // 1) Source selection + capabilities: groups only, restart to apply.
            var empty = store.Execute(workspace, Cmd(AdminAction.List));
            Check(empty.Snapshot.SourceId == "swiftlys2", "SwiftlyS2 source not selected.");
            Check(empty.Snapshot.Capabilities is { Key: AdminKeyKind.SteamId64, Fields: AdminFields.Groups,
                Groups: AdminGroupsLayout.SameFile, Reload: AdminReloadKind.Restart, ReloadCommand: null }, "SwiftlyS2 capabilities wrong.");
            Check(!File.Exists(file), "List must not create the file.");

            // 2) init-groups writes the two expected objects and nothing else.
            var skeleton = store.Execute(workspace, Cmd(AdminAction.Apply, empty.Snapshot.Hash, [], initGroups: true));
            Check(File.Exists(file) && File.ReadAllText(file).Contains("\"Players\"", StringComparison.Ordinal), "Skeleton missing Players.");
            var skeletonText = File.ReadAllText(file);
            Check(skeletonText.Contains("\"PermissionGroups\"", StringComparison.Ordinal) && !skeletonText.Contains("hcsl_admin", StringComparison.Ordinal),
                "Skeleton invented groups.");
            Check(skeleton.Snapshot.Entries.Length == 0, "Skeleton opened with entries.");

            // 3) Grant stores group membership keyed by SteamID, ignoring player-level permissions.
            var granted = store.Execute(workspace, Cmd(AdminAction.Apply, skeleton.Snapshot.Hash,
                [new(AdminMutationKind.Upsert, steamId, null, ["vip", "admin"], ["test.1"], 50)]));
            Check(granted.Snapshot.Entries.Length == 1 && granted.Snapshot.Entries[0].Groups.SequenceEqual(["vip", "admin"]), "Groups not stored.");
            Check(granted.Snapshot.Entries[0].DisplayName == granted.Snapshot.Entries[0].SteamId64,
                "SwiftlyS2 keys players by SteamID; the key and identity must agree.");
            Check(granted.Applied[0].Contains("Upsert", StringComparison.Ordinal), "Applied summary missing.");
            Check(granted.Snapshot.Warnings.Any(w => w.Contains("ignored", StringComparison.Ordinal)), "Ignored flags not reported.");
            var written = File.ReadAllText(file);
            Check(written.Contains("\"76561198000000000\": [\"vip\", \"admin\"]", StringComparison.Ordinal), "Membership array not written:\n" + written);

            // 4) Groups (with parent references) are exposed read-only.
            File.WriteAllText(file, "{\n  \"Permissions\": {\n    \"Players\": {\n      \"76561198000000000\": [ \"vip2\" ]\n    },\n    \"PermissionGroups\": {\n      \"vip2\": [ \"vip\", \"test.*\" ],\n      \"vip\": [ \"test.3\" ]\n    }\n  }\n}\n");
            var listed = store.Execute(workspace, Cmd(AdminAction.List));
            Check(listed.Snapshot.Groups.Length == 2 && listed.Snapshot.Groups[0].Name == "vip"
                && listed.Snapshot.Groups[1].Flags.SequenceEqual(["vip", "test.*"]), "Group parsing with parents.");

            // 5) A STEAM_X:Y:Z key is the same admin; editing keeps one entry.
            File.WriteAllText(file, "{\n  \"Permissions\": {\n    \"Players\": {\n      \"STEAM_0:0:19867136\": [ \"vip\" ]\n    },\n    \"PermissionGroups\": {}\n  }\n}\n");
            var alt = store.Execute(workspace, Cmd(AdminAction.List));
            Check(alt.Snapshot.Entries.Single().SteamId64 == steamId, "Alternate identity not normalised.");
            store.Execute(workspace, Cmd(AdminAction.Apply, alt.Snapshot.Hash, [new(AdminMutationKind.SetGroups, steamId, null, ["admin"])]));
            var after = store.Execute(workspace, Cmd(AdminAction.List));
            Check(after.Snapshot.Entries.Length == 1 && after.Snapshot.Entries[0].Groups.SequenceEqual(["admin"]), "Edit duplicated the entry.");

            // 6) Revoke + stale hash.
            var revokeBase = store.Execute(workspace, Cmd(AdminAction.List));
            var revoked = store.Execute(workspace, Cmd(AdminAction.Apply, revokeBase.Snapshot.Hash, [new(AdminMutationKind.Remove, steamId)]));
            Check(revoked.Snapshot.Entries.Length == 0, "Revoke failed.");
            Reject(() => store.Execute(workspace, Cmd(AdminAction.Apply, revokeBase.Snapshot.Hash, [new(AdminMutationKind.Remove, steamId)])), "HCSL-ADMIN-CONFLICT");

            // 6b) An entry whose key is not a valid SteamID (example placeholder) is removable by its raw key.
            File.WriteAllText(file, "{\n  \"Permissions\": {\n    \"Players\": {\n      \"7656\": [ \"vip2\" ]\n    },\n    \"PermissionGroups\": {}\n  }\n}\n");
            var placeholder = store.Execute(workspace, Cmd(AdminAction.List));
            Check(placeholder.Snapshot.Entries.Single().SteamId64 == "" && placeholder.Snapshot.Entries[0].Problems.Contains("HCSL-ADMIN-ID"),
                "Unparsable key should be listed with a problem code.");
            var byKey = store.Execute(workspace, Cmd(AdminAction.Apply, placeholder.Snapshot.Hash,
                [new(AdminMutationKind.Remove, "", Key: "7656")]));
            Check(byKey.Snapshot.Entries.Length == 0 && !File.ReadAllText(file).Contains("\"7656\"", StringComparison.Ordinal),
                "Key-based removal did not delete the entry.");

            // 7) No supported framework enabled → no source.
            File.WriteAllText(plugins, """{"FormatVersion":1,"Revision":2,"Plugins":[{"Id":"swiftlys2","Version":"1.5.2","Enabled":false}]}""");
            Reject(() => store.Execute(workspace, Cmd(AdminAction.List)), "HCSL-ADMIN-SOURCE");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
