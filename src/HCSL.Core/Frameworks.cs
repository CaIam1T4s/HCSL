using System.Text.RegularExpressions;

namespace HCSL.Core;

/// <summary>Server-side plugin loader. Only Metamod:Source is a base; scripting frameworks are mutually exclusive.</summary>
public enum FrameworkKind { Base, ScriptFramework, NativePluginHost }

/// <summary>
/// One framework HCSL knows how to install. Downloading is limited to this closed catalog on purpose:
/// unknown arbitrary plugins keep the manual import path.
/// </summary>
public sealed record FrameworkDefinition(string Id, string DisplayName, string Repository, FrameworkKind Kind,
    bool Installable, string? Requires = null);

public sealed record FrameworkVersionInfo(string Id, string DisplayName, bool Base, bool Installable,
    string? InstalledVersion, string? LatestVersion, string? LatestAsset, bool UpdateAvailable,
    string? Requires, string? Error);

public sealed record FrameworkCheckResult(FrameworkVersionInfo[] Frameworks);

/// <summary>Install the latest (or a specific) release of one catalog framework into the local package library.</summary>
public sealed record FrameworkInstallCommand(string Id, string? Version = null, bool WithRuntime = true);

public sealed record FrameworkInstallResult(string Id, string Version, string PackagePath, int Files, long Bytes,
    string Sha256, bool DigestVerified, string[] Warnings);

/// <summary>Closed catalog plus pure rules for tag parsing, asset selection and version comparison.</summary>
public static class FrameworkRules
{
    public const long MaxDownloadBytes = 1024L * 1024 * 1024;
    public const int MaxCatalogFrameworks = 8;

    public static readonly FrameworkDefinition[] Known =
    [
        new("metamod", "Metamod:Source", "alliedmodders/metamod-source", FrameworkKind.Base, Installable: true),
        new("counterstrikesharp", "CounterStrikeSharp", "roflmuffin/CounterStrikeSharp", FrameworkKind.ScriptFramework, Installable: true, Requires: "metamod"),
        new("source2mod", "Source2Mod", "Source2Mod/source2mod", FrameworkKind.ScriptFramework, Installable: false, Requires: "metamod"),
        new("swiftlys2", "SwiftlyS2", "SwiftlyS2/SwiftlyS2", FrameworkKind.ScriptFramework, Installable: false, Requires: "metamod"),
        new("modsharp", "ModSharp", "Kxnrl/modsharp-public", FrameworkKind.ScriptFramework, Installable: false, Requires: "metamod")
    ];

    private static readonly Regex MetamodTag = new(@"\A2\.0\.0\.(?<build>\d+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex MetamodAsset = new(@"(?i)\Ammsource-2\.0\.0-git(?<build>\d+)-(?<os>linux|windows)\.(?<ext>tar\.gz|zip)\z", RegexOptions.CultureInvariant);
    private static readonly Regex CssTag = new(@"\Av?(?<version>\d+\.\d+\.\d+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex CssAsset = new(@"(?i)\Acounterstrikesharp-(?<runtime>with-runtime-)?(?<os>linux|windows)-(?<version>\d+\.\d+\.\d+)\.zip\z", RegexOptions.CultureInvariant);

    public static FrameworkDefinition? Find(string? id) =>
        Known.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    public static bool IsBase(string id) => string.Equals(id, "metamod", StringComparison.Ordinal);

    /// <summary>True when both are scripting frameworks; the base may always coexist.</summary>
    public static bool Conflicts(string? left, string? right)
    {
        if (left is null || right is null || left == right) return false;
        var a = Find(left); var b = Find(right);
        return a?.Kind == FrameworkKind.ScriptFramework && b?.Kind == FrameworkKind.ScriptFramework;
    }

    /// <summary>Map a release tag to the version stored in the package. Unknown formats are rejected, never guessed.</summary>
    public static string? VersionFromTag(string id, string tag)
    {
        if (IsBase(id))
        {
            var match = MetamodTag.Match(tag);
            return match.Success ? "2.0.0-dev+" + match.Groups["build"].Value : null;
        }
        if (string.Equals(id, "counterstrikesharp", StringComparison.Ordinal))
        {
            var match = CssTag.Match(tag);
            return match.Success ? match.Groups["version"].Value : null;
        }
        return null;
    }

    /// <summary>Choose the release asset for the current OS. Returns null when no asset matches exactly.</summary>
    public static string? SelectAsset(string id, IEnumerable<string> assets, bool windows, bool withRuntime)
    {
        var os = windows ? "windows" : "linux";
        if (IsBase(id))
        {
            foreach (var name in assets)
            {
                var match = MetamodAsset.Match(name);
                if (match.Success && match.Groups["os"].Value.Equals(os, StringComparison.OrdinalIgnoreCase)) return name;
            }
            return null;
        }
        if (string.Equals(id, "counterstrikesharp", StringComparison.Ordinal))
        {
            foreach (var name in assets)
            {
                var match = CssAsset.Match(name);
                if (!match.Success || !match.Groups["os"].Value.Equals(os, StringComparison.OrdinalIgnoreCase)) continue;
                var runtime = match.Groups["runtime"].Success;
                if (withRuntime != runtime) continue;
                return name;
            }
            return null;
        }
        return null;
    }

    /// <summary>Version embedded in an asset name, for releases whose tag is not the package version.</summary>
    public static string? VersionFromAsset(string id, string asset)
    {
        if (IsBase(id))
        {
            var match = MetamodAsset.Match(asset);
            return match.Success ? "2.0.0-dev+" + match.Groups["build"].Value : null;
        }
        if (string.Equals(id, "counterstrikesharp", StringComparison.Ordinal))
        {
            var match = CssAsset.Match(asset);
            return match.Success ? match.Groups["version"].Value : null;
        }
        return null;
    }

    /// <summary>Compare two package versions without treating build numbers as text (1490 must beat 999).</summary>
    public static int CompareVersions(string id, string left, string right)
    {
        if (IsBase(id))
        {
            var a = ParseBuild(left); var b = ParseBuild(right);
            if (a is not null && b is not null) return a.Value.CompareTo(b.Value);
        }
        var pa = NumericParts(left); var pb = NumericParts(right);
        var count = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < count; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return string.CompareOrdinal(left, right);
    }

    private static int? ParseBuild(string version)
    {
        var match = Regex.Match(version, @"\+(\d+)\z", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var build) ? build : null;
    }

    private static int[] NumericParts(string version)
    {
        var parts = new List<int>();
        foreach (Match match in Regex.Matches(version, @"\d+", RegexOptions.CultureInvariant))
            if (int.TryParse(match.Value, out var value)) parts.Add(value);
        return [.. parts];
    }
}
