using System.Globalization;
using System.Text.RegularExpressions;

namespace HCSL.Core;

public enum AdminAction { List, Apply, Reload }
public enum AdminMutationKind { Upsert, Remove, SetGroups }

/// <summary>How a framework keys its admin entries; the editor must never assume one shape.</summary>
public enum AdminKeyKind { DisplayName, SteamId64 }

/// <summary>How a written list takes effect. Only <see cref="Command"/> can be triggered by HCSL.</summary>
public enum AdminReloadKind { Command, HotReload, Restart }

public enum AdminGroupsLayout { SeparateFile, SameFile, None }

[Flags]
public enum AdminFields { None = 0, Groups = 1, Flags = 2, Immunity = 4, CommandOverrides = 8 }

/// <summary>What one admin source can hold and how it becomes effective; frontends render from this.</summary>
public sealed record AdminSourceCapabilities(string SourceId, string SourceName, AdminKeyKind Key,
    AdminFields Fields, AdminGroupsLayout Groups, AdminReloadKind Reload, string? ReloadCommand);

/// <summary>One intended change. Applied as a minimal text edit, never a whole-file rewrite.</summary>
public sealed record AdminMutation(AdminMutationKind Kind, string SteamId64, string? DisplayName = null,
    string[]? Groups = null, string[]? Flags = null, int? Immunity = null);

/// <summary>One admin entry as read from the framework's own file.</summary>
public sealed record AdminEntry(string SteamId64, string? DisplayName, string[] Groups, string[] Flags,
    int? Immunity, string[] Problems);

public sealed record AdminGroupInfo(string Name, string[] Flags, int? Immunity);

/// <summary>Read-only projection of one admin source. Files are listed but never sent to a frontend.
/// Entries are one page of <see cref="Total"/>; the file hash always describes the whole file.</summary>
public sealed record AdminListSnapshot(string SourceId, string SourceName, bool Writable, string[] Files,
    AdminGroupInfo[] Groups, AdminEntry[] Entries, string Hash, string[] Warnings,
    AdminSourceCapabilities? Capabilities = null, int Total = 0, int Offset = 0, int Limit = 0)
{
    public bool HasMore => Offset + Entries.Length < Total;

    public static AdminListSnapshot Unavailable(string sourceId, string sourceName, string[] warnings) =>
        new(sourceId, sourceName, false, [], [], [], "", warnings);
}

public sealed record AdminCommand(AdminAction Action, Guid InstanceId, Guid PresetId, string? ExpectedHash = null,
    AdminMutation[]? Mutations = null, bool InitGroups = false, bool Root = false,
    int Offset = 0, int Limit = 0, string? Query = null)
{
    public override string ToString() => $"AdminCommand {Action} {InstanceId}/{PresetId}";
}

public sealed record AdminResult(AdminListSnapshot Snapshot, string Code, string[] Applied, string? Reload = null);

/// <summary>
/// Rules shared by every admin source. Identity is always SteamID64 internally; display formats are accepted
/// on input and preserved on output.
/// </summary>
public static class AdminRules
{
    /// <summary>Writes are bounded per request; reads are paged instead of capped, so large servers work.</summary>
    public const int MaxMutationsPerRequest = 256;
    public const int MaxGroups = 256;
    public const int MaxFlagsPerEntry = 64;
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 200;
    public const int MaxNameLength = 64;
    public const string CounterStrikeSharp = "cssharp";
    public const string Source2Mod = "source2mod";
    public const string SwiftlyS2 = "swiftlys2";

    /// <summary>CounterStrikeSharp: display-name keys, separate groups file, reload by console command.</summary>
    public static readonly AdminSourceCapabilities CounterStrikeSharpCapabilities =
        new(CounterStrikeSharp, "CounterStrikeSharp", AdminKeyKind.DisplayName,
            AdminFields.Groups | AdminFields.Flags | AdminFields.Immunity | AdminFields.CommandOverrides,
            AdminGroupsLayout.SeparateFile, AdminReloadKind.Command, "css_admins_reload");

    public static AdminSourceCapabilities? Capabilities(string? sourceId) => sourceId switch
    {
        CounterStrikeSharp => CounterStrikeSharpCapabilities,
        Source2Mod => Source2ModCapabilities,
        SwiftlyS2 => SwiftlyS2Capabilities,
        _ => null
    };

    /// <summary>SwiftlyS2: SteamID keys, only group membership per player, native reader → restart to apply.</summary>
    public static readonly AdminSourceCapabilities SwiftlyS2Capabilities =
        new(SwiftlyS2, "SwiftlyS2", AdminKeyKind.SteamId64, AdminFields.Groups,
            AdminGroupsLayout.SameFile, AdminReloadKind.Restart, null);

    /// <summary>Source2Mod: SteamID keys, groups and permissions in one JSONC file, reload by console command.</summary>
    public static readonly AdminSourceCapabilities Source2ModCapabilities =
        new(Source2Mod, "Source2Mod", AdminKeyKind.SteamId64,
            AdminFields.Groups | AdminFields.Flags | AdminFields.Immunity,
            AdminGroupsLayout.SameFile, AdminReloadKind.Command, "s2m admins reload");

    /// <summary>Page window for a list request; an unset limit uses the default page size.</summary>
    public static (int Offset, int Limit) Page(int offset, int limit)
    {
        if (offset < 0) throw new InstanceException("HCSL-ADMIN-LIMIT", "Offset must be zero or greater.");
        var size = limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize);
        return (offset, size);
    }

    /// <summary>Conservative group written by <c>init-groups</c>: no rcon, cheats or root.</summary>
    public static readonly string[] DefaultGroupFlags =
        ["@css/generic", "@css/kick", "@css/ban", "@css/slay", "@css/changemap", "@css/chat"];
    public const string DefaultGroupName = "#hcsl/admin";

    private static readonly Regex SteamId64Pattern = new(@"\A7656\d{13}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SteamPattern = new(@"\ASTEAM_(?<x>[0-4]):(?<y>[01]):(?<z>\d{1,10})\z", RegexOptions.CultureInvariant);
    private static readonly Regex UnityPattern = new(@"\A\[U:1:(?<account>\d{1,10})\]\z", RegexOptions.CultureInvariant);

    /// <summary>Accept SteamID64, STEAM_X:Y:Z or [U:1:N] and normalise to SteamID64.</summary>
    public static string SteamId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InstanceException("HCSL-ADMIN-ID", "A SteamID64, STEAM_X:Y:Z or [U:1:N] identity is required.");
        var text = value.Trim();
        if (SteamId64Pattern.IsMatch(text)) return text;
        var steam = SteamPattern.Match(text);
        if (steam.Success)
            return (PlayerRosterRules.SteamId64Base
                + long.Parse(steam.Groups["z"].Value, CultureInfo.InvariantCulture) * 2
                + long.Parse(steam.Groups["y"].Value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        var unity = UnityPattern.Match(text);
        if (unity.Success)
            return PlayerRosterRules.SteamId64FromAccount(long.Parse(unity.Groups["account"].Value, CultureInfo.InvariantCulture));
        throw new InstanceException("HCSL-ADMIN-ID", "Identity must be SteamID64, STEAM_X:Y:Z or [U:1:N].");
    }

    public static string Group(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length is 0 or > MaxNameLength || text.Any(char.IsControl) || text.Any(c => c is '"' or '\\'))
            throw new InstanceException("HCSL-ADMIN-GROUP", "A group name is 1-64 characters without quotes, backslashes or control characters.");
        return text;
    }

    public static string Flag(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length is 0 or > MaxNameLength || text.Any(char.IsControl) || text.Any(c => c is '"' or '\\'))
            throw new InstanceException("HCSL-ADMIN-FLAG", "A flag is 1-64 characters without quotes, backslashes or control characters.");
        return text;
    }

    public static string DisplayName(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Length > MaxNameLength || text.Any(char.IsControl))
            throw new InstanceException("HCSL-ADMIN-NAME", "A display name is 1-64 characters without control characters.");
        return text;
    }

    public static string[] Groups(IEnumerable<string>? values)
    {
        var groups = (values ?? []).Select(Group).Distinct(StringComparer.Ordinal).ToArray();
        if (groups.Length > MaxGroups) throw new InstanceException("HCSL-ADMIN-LIMIT", "At most " + MaxGroups + " groups per entry.");
        return groups;
    }

    public static string[] Flags(IEnumerable<string>? values)
    {
        var flags = (values ?? []).Select(Flag).Distinct(StringComparer.Ordinal).ToArray();
        if (flags.Length > MaxFlagsPerEntry) throw new InstanceException("HCSL-ADMIN-LIMIT", "At most " + MaxFlagsPerEntry + " flags per entry.");
        return flags;
    }

    public static void Validate(AdminMutation[]? mutations, bool allowEmpty = false)
    {
        mutations ??= [];
        if (mutations.Length == 0 && !allowEmpty) throw new InstanceException("HCSL-ADMIN-REQUEST", "At least one change is required.");
        if (mutations.Length > MaxMutationsPerRequest) throw new InstanceException("HCSL-ADMIN-LIMIT", "At most " + MaxMutationsPerRequest + " changes per request.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            var id = SteamId(mutation.SteamId64);
            if (!seen.Add(id)) throw new InstanceException("HCSL-ADMIN-DUPLICATE", "The same identity appears more than once in one request.");
            if (!Enum.IsDefined(mutation.Kind)) throw new InstanceException("HCSL-ADMIN-REQUEST", "Unknown change kind.");
            Groups(mutation.Groups);
            Flags(mutation.Flags);
            if (mutation.Immunity is < 0 or > 1000) throw new InstanceException("HCSL-ADMIN-LIMIT", "Immunity must be 0-1000.");
            if (mutation.Kind == AdminMutationKind.Upsert) DisplayName(mutation.DisplayName);
        }
    }

    public static void Verify(AdminListSnapshot snapshot)
    {
        if (snapshot.Groups.Length > MaxGroups) throw new InstanceException("HCSL-ADMIN-LIMIT", "The admin source has more groups than supported.");
    }

    /// <summary>Console command that makes a written admin list take effect, or null when the source needs none.</summary>
    public static string? ReloadCommandFor(string? sourceId) =>
        Capabilities(sourceId) is { Reload: AdminReloadKind.Command } source ? source.ReloadCommand : null;

    /// <summary>Group file used when the framework ships none; content is explicit and reviewable.</summary>
    public static string DefaultGroupsJson() =>
        "{\n  \"" + DefaultGroupName + "\": {\n    \"flags\": ["
        + string.Join(", ", DefaultGroupFlags.Select(f => "\"" + f + "\""))
        + "],\n    \"immunity\": 50\n  }\n}\n";
}
