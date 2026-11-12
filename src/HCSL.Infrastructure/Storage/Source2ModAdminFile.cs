using System.Text;
using System.Text.Json;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Source2Mod admin list (<c>addons/source2mod/configs/permissions.json</c>). The file is JSONC with comments,
/// keys are SteamIDs, and a player's grants are group names plus dotted permissions; groups live in the same
/// file, so edits must never rewrite the document.
/// </summary>
internal static class Source2ModAdminFile
{
    internal const string FileName = "permissions.json";
    private static readonly JsonDocumentOptions Tolerant = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    internal static readonly AdminSourceCapabilities Capabilities = AdminRules.Source2ModCapabilities;

    internal static AdminListSnapshot Read(string path, AdminCommand command)
    {
        var warnings = new List<string>();
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (text.Length == 0) warnings.Add(FileName + " does not exist yet; the list is empty until the first grant.");
        var groups = new List<AdminGroupInfo>();
        var entries = new List<AdminEntry>();
        if (text.Length > 0)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(text, Tolerant); }
            catch (JsonException) { throw new InstanceException("HCSL-ADMIN-PARSE", "permissions.json is not parseable JSON/JSONC."); }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new InstanceException("HCSL-ADMIN-PARSE", "permissions.json root is not an object.");
                if (root.TryGetProperty("Groups", out var groupObject) && groupObject.ValueKind == JsonValueKind.Object)
                    foreach (var group in groupObject.EnumerateObject())
                    {
                        var permissions = group.Value.ValueKind == JsonValueKind.Object
                            && group.Value.TryGetProperty("Permissions", out var list) && list.ValueKind == JsonValueKind.Array
                            ? list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray()
                            : [];
                        var immunity = group.Value.ValueKind == JsonValueKind.Object
                            && group.Value.TryGetProperty("Immunity", out var value) && value.TryGetInt32(out var parsed) ? parsed : (int?)null;
                        groups.Add(new(group.Name, permissions, immunity));
                    }
                if (root.TryGetProperty("Admins", out var adminObject) && adminObject.ValueKind == JsonValueKind.Object)
                    foreach (var admin in adminObject.EnumerateObject())
                    {
                        var problems = new List<string>();
                        var steamId = "";
                        try { steamId = AdminRules.SteamId(admin.Name); }
                        catch (InstanceException) { problems.Add("HCSL-ADMIN-ID"); }
                        var groupsOf = Array.Empty<string>();
                        var permissions = Array.Empty<string>();
                        int? immunity = null;
                        if (admin.Value.ValueKind != JsonValueKind.Object) problems.Add("HCSL-ADMIN-PARSE");
                        else
                        {
                            if (admin.Value.TryGetProperty("Groups", out var groupList) && groupList.ValueKind == JsonValueKind.Array)
                                groupsOf = [.. groupList.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!)];
                            if (admin.Value.TryGetProperty("Permissions", out var permissionList) && permissionList.ValueKind == JsonValueKind.Array)
                                permissions = [.. permissionList.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)];
                            if (admin.Value.TryGetProperty("Immunity", out var immunityValue) && immunityValue.TryGetInt32(out var parsed)) immunity = parsed;
                        }
                        // The key IS the SteamID here; keep it for diagnostics (e.g. an unparsable key) and let the
                        // UI collapse key==SteamID into a single column instead of printing it twice.
                        entries.Add(new(steamId, admin.Name, groupsOf, permissions, immunity, [.. problems]));
                    }
            }
        }
        foreach (var duplicate in entries.Where(e => e.SteamId64.Length > 0).GroupBy(e => e.SteamId64).Where(g => g.Count() > 1))
            warnings.Add("Identity " + duplicate.Key + " appears in " + duplicate.Count() + " entries.");
        var filtered = Filter([.. entries], command.Query);
        var (offset, limit) = AdminRules.Page(command.Offset, command.Limit);
        var page = filtered.Skip(offset).Take(limit).ToArray();
        if (offset > 0 && offset >= filtered.Length && filtered.Length > 0) warnings.Add("Offset is past the end of the filtered list.");
        if (groups.Count == 0) warnings.Add("No groups are defined yet; grants need an existing group or explicit permissions (admin.*, *).");
        var snapshot = new AdminListSnapshot(Capabilities.SourceId, Capabilities.SourceName, true, [FileName], [.. groups.OrderBy(g => g.Name, StringComparer.Ordinal)],
            page, Hash(path), [.. warnings], Capabilities, filtered.Length, offset, limit);
        AdminRules.Verify(snapshot);
        return snapshot;
    }

    internal static string Apply(string path, AdminCommand command, out string[] applied, List<string> warnings)
    {
        var before = File.Exists(path) ? File.ReadAllText(path) : "";
        var text = command.InitGroups && before.Trim().Length == 0 ? Skeleton() : before;
        if (text.Trim().Length == 0) text = Skeleton();
        var appliedList = new List<string>();
        foreach (var mutation in command.Mutations!)
        {
            // A key-only removal (unparsable identity) has no SteamID to resolve.
            var keyOnly = mutation.Kind == AdminMutationKind.Remove && string.IsNullOrWhiteSpace(mutation.SteamId64);
            var steamId = keyOnly ? "" : AdminRules.SteamId(mutation.SteamId64);
            var root = ParseOrThrow(text);
            var admins = EnsureObject(ref text, "Admins");
            root = ParseOrThrow(text);
            var existing = FindAdmin(text, root, steamId);
            switch (mutation.Kind)
            {
                case AdminMutationKind.Remove:
                {
                    // Fall back to the raw key so an entry without a usable SteamID can still be removed.
                    var removalKey = existing?.Key ?? mutation.Key;
                    if (removalKey is { Length: > 0 } && HasKey(text, removalKey))
                    {
                        text = JsonSpanEditor.RemoveMember(text, ParseOrThrow(text), ["Admins"], removalKey);
                        appliedList.Add("Remove " + removalKey);
                    }
                    else warnings.Add("Identity " + steamId + " is not in the list.");
                    break;
                }
                case AdminMutationKind.Upsert:
                case AdminMutationKind.SetGroups:
                {
                    var groups = mutation.Groups is null ? existing?.Entry.Groups ?? [] : AdminRules.Groups(mutation.Groups);
                    var permissions = mutation.Flags is null ? existing?.Entry.Flags ?? [] : AdminRules.Flags(mutation.Flags);
                    var immunity = mutation.Immunity ?? existing?.Entry.Immunity;
                    var value = Value(groups, permissions, immunity);
                    var name = existing?.Key ?? steamId;
                    text = JsonSpanEditor.SetMember(text, ParseOrThrow(text), ["Admins"], name, value);
                    appliedList.Add(mutation.Kind + " " + steamId + (groups.Length > 0 ? " [" + string.Join(", ", groups) + "]" : ""));
                    break;
                }
            }
            _ = admins;
        }
        ParseOrThrow(text);
        applied = [.. appliedList];
        return text;
    }

    /// <summary>True when the Admins object already contains this exact key.</summary>
    private static bool HasKey(string text, string key)
    {
        var admins = JsonSpanEditor.Find(ParseOrThrow(text), "Admins");
        return admins?.Value.Members?.Any(m => m.Key == key) == true;
    }

    /// <summary>Admin entry whose key normalises to the requested SteamID (the key text itself is preserved).</summary>
    private static (string Key, AdminEntry Entry)? FindAdmin(string text, JsonSpanEditor.Node root, string steamId)
    {
        var admins = JsonSpanEditor.Find(root, "Admins");
        if (admins?.Value.Members is null) return null;
        foreach (var member in admins.Value.Members)
        {
            string normalized;
            try { normalized = AdminRules.SteamId(member.Key); }
            catch (InstanceException) { continue; }
            if (normalized != steamId) continue;
            var raw = text[member.ValueStart..member.ValueEnd];
            var groups = Array.Empty<string>();
            var permissions = Array.Empty<string>();
            int? immunity = null;
            try
            {
                using var document = JsonDocument.Parse(raw, Tolerant);
                var value = document.RootElement;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("Groups", out var list) && list.ValueKind == JsonValueKind.Array)
                        groups = [.. list.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!)];
                    if (value.TryGetProperty("Permissions", out var permissionList) && permissionList.ValueKind == JsonValueKind.Array)
                        permissions = [.. permissionList.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)];
                    if (value.TryGetProperty("Immunity", out var immunityValue) && immunityValue.TryGetInt32(out var parsed)) immunity = parsed;
                }
            }
            catch (JsonException) { }
            return (member.Key, new(steamId, member.Key, groups, permissions, immunity, []));
        }
        return null;
    }

    /// <summary>Create a missing top-level object ("Admins") so the first grant has somewhere to go.</summary>
    private static JsonSpanEditor.Node EnsureObject(ref string text, string name)
    {
        var root = ParseOrThrow(text);
        if (JsonSpanEditor.Find(root, name) is not null) return root;
        text = JsonSpanEditor.SetMember(text, root, [], name, "{}");
        return ParseOrThrow(text);
    }

    private static JsonSpanEditor.Node ParseOrThrow(string text)
    {
        try { return JsonSpanEditor.ParseObject(text); }
        catch (FormatException) { throw new InstanceException("HCSL-ADMIN-PARSE", "permissions.json is not parseable JSON/JSONC; nothing was written."); }
    }

    private static string Value(string[] groups, string[] permissions, int? immunity)
    {
        var builder = new StringBuilder("{");
        if (groups.Length > 0) builder.Append(" \"Groups\": [").Append(string.Join(", ", groups.Select(g => "\"" + Escape(g) + "\""))).Append("],");
        builder.Append(" \"Permissions\": [").Append(string.Join(", ", permissions.Select(p => "\"" + Escape(p) + "\""))).Append(']');
        if (immunity is { } value) builder.Append(", \"Immunity\": ").Append(value);
        return builder.Append(" }").ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Skeleton with no invented privileges: groups and admins stay empty.</summary>
    internal static string Skeleton() =>
        "{\n  \"Groups\": {},\n\n  // Keyed by SteamID64, STEAM_X:Y:Z or [U:1:N].\n  \"Admins\": {},\n\n  \"Overrides\": {}\n}\n";

    private static AdminEntry[] Filter(AdminEntry[] entries, string? query)
    {
        var ordered = entries.OrderBy(e => e.DisplayName ?? e.SteamId64, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SteamId64, StringComparer.Ordinal).ToArray();
        var text = (query ?? "").Trim();
        if (text.Length == 0) return ordered;
        return [.. ordered.Where(e =>
            e.SteamId64.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (e.DisplayName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
            || e.Groups.Any(g => g.Contains(text, StringComparison.OrdinalIgnoreCase))
            || e.Flags.Any(f => f.Contains(text, StringComparison.OrdinalIgnoreCase)))];
    }

    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
