using System.Text;
using System.Text.Json;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// SwiftlyS2 admin list (<c>framework-data/swiftlys2/configs/permissions.jsonc</c>, mapped to
/// <c>addons/swiftlys2/configs</c> at deploy time). A player entry is just a list of group names; groups map
/// a name to permission names or parent groups. The framework reads the file in native code, so changes only
/// take effect after a restart — <c>reload</c> is translations-only and hot reload covers C# plugins.
/// </summary>
internal static class SwiftlyS2AdminFile
{
    internal const string FileName = "permissions.jsonc";
    internal static readonly AdminSourceCapabilities Capabilities = AdminRules.SwiftlyS2Capabilities;
    private static readonly JsonDocumentOptions Tolerant = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly string[] PlayerPath = ["Permissions", "Players"];

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
            catch (JsonException) { throw new InstanceException("HCSL-ADMIN-PARSE", "permissions.jsonc is not parseable JSON/JSONC."); }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InstanceException("HCSL-ADMIN-PARSE", "permissions.jsonc root is not an object.");
                if (!document.RootElement.TryGetProperty("Permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add("permissions.jsonc has no Permissions object.");
                }
                else
                {
                    if (permissions.TryGetProperty("PermissionGroups", out var groupObject) && groupObject.ValueKind == JsonValueKind.Object)
                        foreach (var group in groupObject.EnumerateObject())
                            groups.Add(new(group.Name, Strings(group.Value), null));
                    if (permissions.TryGetProperty("Players", out var playerObject) && playerObject.ValueKind == JsonValueKind.Object)
                        foreach (var player in playerObject.EnumerateObject())
                        {
                            var problems = new List<string>();
                            var steamId = "";
                            try { steamId = AdminRules.SteamId(player.Name); }
                            catch (InstanceException) { problems.Add("HCSL-ADMIN-ID"); }
                            var memberships = Strings(player.Value);
                            if (player.Value.ValueKind != JsonValueKind.Array) problems.Add("HCSL-ADMIN-PARSE");
                            // SwiftlyS2 keys players by SteamID; keep the raw key for diagnostics and let the UI
                            // collapse key==SteamID into a single column.
                            entries.Add(new(steamId, player.Name, memberships, [], null, [.. problems]));
                        }
                }
            }
        }
        foreach (var duplicate in entries.Where(e => e.SteamId64.Length > 0).GroupBy(e => e.SteamId64).Where(g => g.Count() > 1))
            warnings.Add("Identity " + duplicate.Key + " appears in " + duplicate.Count() + " entries.");
        var filtered = Filter([.. entries], command.Query);
        var (offset, limit) = AdminRules.Page(command.Offset, command.Limit);
        var page = filtered.Skip(offset).Take(limit).ToArray();
        if (offset > 0 && offset >= filtered.Length && filtered.Length > 0) warnings.Add("Offset is past the end of the filtered list.");
        var snapshot = new AdminListSnapshot(Capabilities.SourceId, Capabilities.SourceName, true, [FileName],
            [.. groups.OrderBy(g => g.Name, StringComparer.Ordinal)], page, Hash(path), [.. warnings], Capabilities, filtered.Length, offset, limit);
        AdminRules.Verify(snapshot);
        return snapshot;
    }

    internal static string Apply(string before, AdminCommand command, out string[] applied, List<string> warnings)
    {
        var text = before.Trim().Length == 0 ? Skeleton() : before;
        var appliedList = new List<string>();
        if (command.InitGroups && command.Mutations is not { Length: > 0 }) { applied = []; return text; }
        foreach (var mutation in command.Mutations!)
        {
            // A key-only removal (unparsable identity) has no SteamID to resolve.
            var keyOnly = mutation.Kind == AdminMutationKind.Remove && string.IsNullOrWhiteSpace(mutation.SteamId64);
            var steamId = keyOnly ? "" : AdminRules.SteamId(mutation.SteamId64);
            if (mutation.Flags is { Length: > 0 } || mutation.Immunity is not null)
                warnings.Add("SwiftlyS2 keeps only group membership per player; permissions and immunity were ignored.");
            switch (mutation.Kind)
            {
                case AdminMutationKind.Remove:
                {
                    // An entry whose key is not a valid SteamID (example placeholder) can only be removed by key.
                    var removalKey = FindKey(text, steamId) ?? mutation.Key;
                    if (removalKey is { Length: > 0 } && HasKey(text, removalKey))
                    {
                        text = JsonSpanEditor.RemoveMember(text, ParseOrThrow(text), PlayerPath, removalKey);
                        appliedList.Add("Remove " + removalKey);
                    }
                    else warnings.Add("Identity " + steamId + " is not in the list.");
                    break;
                }
                case AdminMutationKind.Upsert:
                case AdminMutationKind.SetGroups:
                {
                    var groups = AdminRules.Groups(mutation.Groups);
                    if (groups.Length == 0) warnings.Add("No groups given for " + steamId + "; the entry will have no memberships.");
                    var value = Array(groups);
                    var entryKey = FindKey(text, steamId) ?? steamId;
                    text = EnsurePlayers(text);
                    text = JsonSpanEditor.SetMember(text, ParseOrThrow(text), PlayerPath, entryKey, value);
                    appliedList.Add(mutation.Kind + " " + steamId + (groups.Length > 0 ? " [" + string.Join(", ", groups) + "]" : ""));
                    break;
                }
            }
        }
        ParseOrThrow(text);
        applied = [.. appliedList];
        return text;
    }

    /// <summary>Create Permissions.Players when a hand-written file only carries groups.</summary>
    private static string EnsurePlayers(string text)
    {
        var root = ParseOrThrow(text);
        if (JsonSpanEditor.Find(root, "Permissions") is null) text = JsonSpanEditor.SetMember(text, root, [], "Permissions", "{}");
        root = ParseOrThrow(text);
        if (JsonSpanEditor.Find(root, "Permissions", "Players") is null) text = JsonSpanEditor.SetMember(text, root, ["Permissions"], "Players", "{}");
        return text;
    }

    private static bool HasKey(string text, string key)
    {
        var players = JsonSpanEditor.Find(ParseOrThrow(text), PlayerPath);
        return players?.Value.Members?.Any(m => m.Key == key) == true;
    }

    /// <summary>Existing key for the identity, so a manually written STEAM_X:Y:Z key is edited instead of duplicated.</summary>
    private static string? FindKey(string text, string steamId)
    {
        var players = JsonSpanEditor.Find(ParseOrThrow(text), PlayerPath);
        if (players?.Value.Members is null) return null;
        foreach (var member in players.Value.Members)
        {
            try { if (AdminRules.SteamId(member.Key) == steamId) return member.Key; }
            catch (InstanceException) { }
        }
        return null;
    }

    private static JsonSpanEditor.Node ParseOrThrow(string text)
    {
        try { return JsonSpanEditor.ParseObject(text); }
        catch (FormatException) { throw new InstanceException("HCSL-ADMIN-PARSE", "permissions.jsonc is not parseable JSON/JSONC; nothing was written."); }
    }

    /// <summary>Skeleton with the two objects SwiftlyS2 expects and nothing else; no invented groups.</summary>
    internal static string Skeleton() =>
        "{\n  \"Permissions\": {\n    \"Players\": {},\n    \"PermissionGroups\": {}\n  }\n}\n";

    private static string[] Strings(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

    private static string Array(string[] values) =>
        "[" + string.Join(", ", values.Select(v => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static AdminEntry[] Filter(AdminEntry[] entries, string? query)
    {
        var ordered = entries.OrderBy(e => e.SteamId64, StringComparer.Ordinal).ToArray();
        var text = (query ?? "").Trim();
        if (text.Length == 0) return ordered;
        return [.. ordered.Where(e => e.SteamId64.Contains(text, StringComparison.OrdinalIgnoreCase)
            || e.Groups.Any(g => g.Contains(text, StringComparison.OrdinalIgnoreCase)))];
    }

    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
