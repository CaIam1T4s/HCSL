using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// CounterStrikeSharp admin list (<c>addons/counterstrikesharp/configs/admins.json</c> + <c>admin_groups.json</c>).
/// Edits are minimal text splices so comments, ordering and formatting in a hand-maintained file survive.
/// </summary>
public sealed class LocalAdminStore : IAdminStore
{
    private const string FrameworkId = AdminRules.CounterStrikeSharp;
    private const string AdminsFile = "admins.json";
    private const string GroupsFile = "admin_groups.json";
    private static readonly JsonDocumentOptions Tolerant = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AdminResult Execute(WorkspaceRequest workspace, AdminCommand command)
    {
        if (!Enum.IsDefined(command.Action) || command.InstanceId == Guid.Empty || command.PresetId == Guid.Empty)
            throw new InstanceException("HCSL-ADMIN-REQUEST", "An admin action with stable instance/preset IDs is required.");
        if (command.Action == AdminAction.List)
        {
            var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
            return ExecuteRoot(Path.Combine(paths.DataRoot, "instances"), command);
        }
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
            throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before editing admin lists.");
        return ExecuteRoot(lease.Layout.InstancesRoot, command);
    }

    private AdminResult ExecuteRoot(string instancesRoot, AdminCommand command)
    {
        var preset = PresetPath(instancesRoot, command.InstanceId, command.PresetId);
        var configs = Path.Combine(preset, "addons", "counterstrikesharp", "configs");
        Safe(configs);
        var adminsPath = Path.Combine(configs, AdminsFile);
        var groupsPath = Path.Combine(configs, GroupsFile);
        var enabled = EnabledFrameworks(Path.Combine(preset, "plugins.json"));
        if (!enabled.Contains("counterstrikesharp"))
            throw new InstanceException("HCSL-ADMIN-SOURCE", "This preset does not enable CounterStrikeSharp; the panel has no admin source.");

        if (command.Action == AdminAction.List) return new(ReadSnapshot(adminsPath, groupsPath), "HCSL-ADMIN-OK", []);

        var before = File.Exists(adminsPath) ? File.ReadAllText(adminsPath) : "{\n}\n";
        var hash = Hash(adminsPath);
        if (!string.Equals(hash, command.ExpectedHash ?? "", StringComparison.OrdinalIgnoreCase))
            throw new InstanceException("HCSL-ADMIN-CONFLICT", "The admin file changed outside HCSL. Reload the list before saving; your draft was not written.");
        AdminRules.Validate(command.Mutations);
        var warnings = new List<string>();
        if (command.InitGroups && !File.Exists(groupsPath))
        {
            WriteAtomic(groupsPath, AdminRules.DefaultGroupsJson());
            warnings.Add("Created " + GroupsFile + " with the conservative group " + AdminRules.DefaultGroupName + ".");
        }
        if (!File.Exists(groupsPath) && command.Mutations!.Any(m => m.Groups is { Length: > 0 }))
            warnings.Add(GroupsFile + " is missing: groups are not defined yet, use --init-groups.");

        var text = Apply(before, command, warnings);
        if (text != before)
        {
            Backup(preset, adminsPath);
            WriteAtomic(adminsPath, text);
        }
        var snapshot = ReadSnapshot(adminsPath, groupsPath);
        foreach (var warning in warnings) snapshot = snapshot with { Warnings = [.. snapshot.Warnings, warning] };
        return new(snapshot, "HCSL-ADMIN-WRITTEN", Applied(command), null);
    }

    private static string Apply(string text, AdminCommand command, List<string> warnings)
    {
        JsonSpanEditor.Node root;
        try { root = JsonSpanEditor.ParseObject(text); }
        catch (FormatException) { throw new InstanceException("HCSL-ADMIN-PARSE", "The admin file is not parseable JSON/JSONC; nothing was written."); }
        var current = BuildEntries(text, root, []);
        foreach (var mutation in command.Mutations!)
        {
            var steamId = AdminRules.SteamId(mutation.SteamId64);
            var existing = current.Where(e => e.Entry.SteamId64.Length == 17 && e.Entry.SteamId64 == steamId)
                .Select(e => e.Entry).FirstOrDefault();
            switch (mutation.Kind)
            {
                case AdminMutationKind.Remove:
                    if (existing is null) break;
                    if (existing.DisplayName is not { } name) { warnings.Add("Entry for " + steamId + " has no usable key; remove it manually."); break; }
                    text = JsonSpanEditor.RemoveMember(text, JsonSpanEditor.ParseObject(text), [], name);
                    break;
                case AdminMutationKind.Upsert:
                case AdminMutationKind.SetGroups:
                {
                    var groups = mutation.Groups is null ? existing?.Groups ?? [] : AdminRules.Groups(mutation.Groups);
                    var flags = mutation.Flags is null ? existing?.Flags ?? [] : AdminRules.Flags(mutation.Flags);
                    var immunity = mutation.Immunity ?? existing?.Immunity;
                    var value = Value(steamId, groups, flags, immunity);
                    root = JsonSpanEditor.ParseObject(text);
                    if (existing?.DisplayName is { } key)
                    {
                        text = JsonSpanEditor.SetMember(text, root, [], key, value);
                        break;
                    }
                    var displayName = AdminRules.DisplayName(mutation.DisplayName);
                    var wanted = displayName.Length > 0 ? displayName : steamId;
                    if (root.Members?.Any(m => m.Key == wanted) == true)
                        throw new InstanceException("HCSL-ADMIN-CONFLICT", "The display name '" + wanted + "' is already used by another entry; pass --name.");
                    text = JsonSpanEditor.SetMember(text, root, [], wanted, value);
                    break;
                }
            }
            current = BuildEntries(text, JsonSpanEditor.ParseObject(text), []);
        }
        try { JsonSpanEditor.ParseObject(text); }
        catch (FormatException) { throw new InstanceException("HCSL-ADMIN-WRITE", "The edited admin file would not parse; nothing was written."); }
        return text;
    }

    private static string Value(string steamId, string[] groups, string[] flags, int? immunity)
    {
        var builder = new StringBuilder("{ \"identity\": \"").Append(steamId).Append("\", \"groups\": [");
        builder.Append(string.Join(", ", groups.Select(g => "\"" + Escape(g) + "\"")));
        builder.Append(']');
        if (flags.Length > 0) builder.Append(", \"flags\": [").Append(string.Join(", ", flags.Select(f => "\"" + Escape(f) + "\""))).Append(']');
        if (immunity is { } value) builder.Append(", \"immunity\": ").Append(value);
        return builder.Append(" }").ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string[] Applied(AdminCommand command) =>
        [.. command.Mutations!.Select(m => m.Kind + " " + AdminRules.SteamId(m.SteamId64) + (m.Groups is { Length: > 0 } g ? " [" + string.Join(", ", g) + "]" : ""))];

    private static AdminListSnapshot ReadSnapshot(string adminsPath, string groupsPath)
    {
        var warnings = new List<string>();
        var files = new List<string> { AdminsFile, GroupsFile };
        var adminsText = File.Exists(adminsPath) ? File.ReadAllText(adminsPath) : "";
        if (adminsText.Length == 0) warnings.Add("admins.json does not exist yet; the list is empty until the first grant.");
        var groupsText = File.Exists(groupsPath) ? File.ReadAllText(groupsPath) : "";
        if (groupsText.Length == 0) warnings.Add("admin_groups.json is missing; grants need explicit groups (use --init-groups).");
        var groups = ReadGroups(groupsText, warnings);
        var entries = adminsText.Length == 0
            ? []
            : BuildEntries(adminsText, ParseOrThrow(adminsText), warnings).Select(e => e.Entry).ToArray();
        foreach (var duplicate in entries.Where(e => e.SteamId64.Length > 0).GroupBy(e => e.SteamId64).Where(g => g.Count() > 1))
            warnings.Add("Identity " + duplicate.Key + " appears in " + duplicate.Count() + " entries.");
        var snapshot = new AdminListSnapshot(FrameworkId, "CounterStrikeSharp", true, [.. files], groups, entries,
            Hash(adminsPath), [.. warnings]);
        AdminRules.Verify(snapshot);
        return snapshot;
    }

    private static JsonSpanEditor.Node ParseOrThrow(string text)
    {
        try { return JsonSpanEditor.ParseObject(text); }
        catch (FormatException) { throw new InstanceException("HCSL-ADMIN-PARSE", "The admin file is not parseable JSON/JSONC."); }
    }

    private static AdminGroupInfo[] ReadGroups(string text, List<string> warnings)
    {
        if (text.Length == 0) return [];
        JsonDocument document;
        try { document = JsonDocument.Parse(text, Tolerant); }
        catch (JsonException) { warnings.Add("admin_groups.json is not parseable; groups are shown empty."); return []; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) { warnings.Add("admin_groups.json is not an object."); return []; }
            var groups = new List<AdminGroupInfo>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var flags = property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("flags", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).ToArray()
                    : [];
                var immunity = property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("immunity", out var value) && value.TryGetInt32(out var parsed) ? parsed : (int?)null;
                groups.Add(new(property.Name, flags, immunity));
            }
            return [.. groups.OrderBy(g => g.Name, StringComparer.Ordinal)];
        }
    }

    private static List<(AdminEntry Entry, string Raw)> BuildEntries(string text, JsonSpanEditor.Node root, List<string> warnings)
    {
        var result = new List<(AdminEntry, string)>();
        foreach (var member in root.Members ?? [])
        {
            var raw = text[member.ValueStart..member.ValueEnd];
            var problems = new List<string>();
            string? identity = null;
            string[] groups = [], flags = [];
            int? immunity = null;
            try
            {
                using var document = JsonDocument.Parse(raw, Tolerant);
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object) problems.Add("HCSL-ADMIN-PARSE");
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.String)
                    identity = id.GetString();
                if (identity is null) problems.Add("HCSL-ADMIN-ID");
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("groups", out var groupList) && groupList.ValueKind == JsonValueKind.Array)
                    groups = [.. groupList.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!)];
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("flags", out var flagList) && flagList.ValueKind == JsonValueKind.Array)
                    flags = [.. flagList.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!)];
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("immunity", out var immunityValue) && immunityValue.TryGetInt32(out var parsed))
                    immunity = parsed;
            }
            catch (JsonException) { problems.Add("HCSL-ADMIN-PARSE"); }
            var steamId = "";
            if (identity is not null)
            {
                try { steamId = AdminRules.SteamId(identity); }
                catch (InstanceException) { problems.Add("HCSL-ADMIN-ID"); }
            }
            result.Add((new(steamId, member.Key, groups, flags, immunity, [.. problems]), raw));
        }
        _ = warnings;
        return result;
    }

    private static void Backup(string preset, string adminsPath)
    {
        if (!File.Exists(adminsPath)) return;
        var root = Path.Combine(preset, "framework-data", FrameworkId, "admin-backups");
        Safe(root);
        Directory.CreateDirectory(root);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        File.Copy(adminsPath, Path.Combine(root, stamp + "-" + AdminsFile), true);
        foreach (var old in Directory.EnumerateFiles(root, "*-" + AdminsFile).OrderByDescending(f => f).Skip(10))
            try { File.Delete(old); } catch (IOException) { }
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { } }
    }

    private static string PresetPath(string instancesRoot, Guid instanceId, Guid presetId)
    {
        var preset = Path.Combine(instancesRoot, instanceId.ToString("D"), "presets", presetId.ToString("D"));
        Safe(preset);
        if (!Directory.Exists(preset)) throw new InstanceException("HCSL-ADMIN-SOURCE", "The instance or preset no longer exists.");
        return preset;
    }

    private static HashSet<string> EnabledFrameworks(string pluginsPath)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(pluginsPath)) return enabled;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pluginsPath));
            if (!document.RootElement.TryGetProperty("Plugins", out var list) || list.ValueKind != JsonValueKind.Array) return enabled;
            foreach (var plugin in list.EnumerateArray())
            {
                if (!plugin.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var on = !plugin.TryGetProperty("Enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
                if (on) enabled.Add(id.GetString()!);
            }
        }
        catch (JsonException) { }
        return enabled;
    }

    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Safe(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            LocalPaths.RejectLink(item.FullName);
    }
}
