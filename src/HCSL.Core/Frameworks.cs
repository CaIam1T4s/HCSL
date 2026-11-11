using System.Text.RegularExpressions;

namespace HCSL.Core;

/// <summary>Server-side plugin loader. Only Metamod:Source is a base; scripting frameworks are mutually exclusive.</summary>
public enum FrameworkKind { Base, ScriptFramework, NativePluginHost }

/// <summary>How the server emits enough evidence to mark a loaded map ready.</summary>
public enum FrameworkReadinessProfile { Standard, ModSharp }

/// <summary>Plugin UI grouping. FrameworkPayload packages use Framework and are excluded from plugin groups.</summary>
public enum PluginCategory { Framework, Metamod, CounterStrikeSharp, SwiftlyS2, Source2Mod, ModSharp, Other }

/// <summary>Framework inferred from a managed plugin's .deps.json.</summary>
public enum ManagedPluginFramework { Unknown, SwiftlyS2, ModSharp, Ambiguous }

/// <summary>Persistent data owned by a framework, not by the package archive.</summary>
public enum FrameworkPersistentScope { Preset, Instance }

/// <summary>
/// One framework-owned directory that must survive redeployment. RuntimePath is game-root relative;
/// PersistentRelativePath is relative to the chosen preset or instance.
/// </summary>
public sealed record FrameworkPersistentPath(string RuntimePath, FrameworkPersistentScope Scope,
    string PersistentRelativePath, bool SeedPackageDefaults = true);

/// <summary>
/// One framework HCSL knows how to install. Downloading is limited to this closed catalog on purpose:
/// unknown arbitrary plugins keep the manual import path.
/// </summary>
public sealed record FrameworkDefinition(string Id, string DisplayName, string Repository, FrameworkKind Kind,
    bool Installable, string? Requires = null, FrameworkReadinessProfile Readiness = FrameworkReadinessProfile.Standard,
    FrameworkPersistentPath[]? PersistentPaths = null, string[]? PayloadRoots = null, string[]? MaterializedRoots = null,
    string? PluginRoot = null, string? PluginDataRoot = null);

public sealed record FrameworkVersionInfo(string Id, string DisplayName, bool Base, bool Installable,
    string? InstalledVersion, string? LatestVersion, string? LatestAsset, bool UpdateAvailable,
    string? Requires, string? Error);

public sealed record FrameworkCheckResult(FrameworkVersionInfo[] Frameworks);

/// <summary>Check one framework (for progressive UI) or the whole catalog when Id is null.</summary>
public sealed record FrameworkCheckCommand(string? Id = null);

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
        new("metamod", "Metamod:Source", "alliedmodders/metamod-source", FrameworkKind.Base, Installable: true,
            PluginRoot: "csgo/addons/metamod"),
        new("counterstrikesharp", "CounterStrikeSharp", "roflmuffin/CounterStrikeSharp", FrameworkKind.ScriptFramework, Installable: true, Requires: "metamod",
            PayloadRoots: ["csgo/addons/counterstrikesharp"], PluginRoot: "csgo/addons/counterstrikesharp/plugins"),
        new("source2mod", "Source2Mod", "Source2Mod/source2mod", FrameworkKind.ScriptFramework, Installable: true, Requires: "metamod",
            PayloadRoots: ["csgo/addons/source2mod"], PluginRoot: "csgo/addons/source2mod/plugins"),
        new("swiftlys2", "SwiftlyS2", "swiftly-solution/swiftlys2", FrameworkKind.ScriptFramework, Installable: true,
            PersistentPaths:
            [
                new("csgo/addons/swiftlys2/plugins", FrameworkPersistentScope.Preset, "framework-data/swiftlys2/plugins", false),
                new("csgo/addons/swiftlys2/configs", FrameworkPersistentScope.Preset, "framework-data/swiftlys2/configs")
            ],
            PayloadRoots: ["csgo/addons/swiftlys2"], MaterializedRoots: ["csgo/addons"],
            PluginRoot: "csgo/addons/swiftlys2/plugins", PluginDataRoot: "framework-data/swiftlys2/plugins"),
        new("modsharp", "ModSharp", "Kxnrl/modsharp-public", FrameworkKind.ScriptFramework, Installable: true,
            Readiness: FrameworkReadinessProfile.ModSharp,
            PersistentPaths: [new("sharp/configs", FrameworkPersistentScope.Preset, "framework/modsharp/configs")],
            PayloadRoots: ["sharp"], PluginRoot: "sharp/modules", PluginDataRoot: "framework-data/modsharp/modules")
    ];

    private static readonly Regex MetamodTag = new(@"\A2\.0\.0\.(?<build>\d+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex MetamodAsset = new(@"(?i)\Ammsource-2\.0\.0-git(?<build>\d+)-(?<os>linux|windows)\.(?<ext>tar\.gz|zip)\z", RegexOptions.CultureInvariant);
    private static readonly Regex CssTag = new(@"\Av?(?<version>\d+\.\d+\.\d+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex CssAsset = new(@"(?i)\Acounterstrikesharp-(?<runtime>with-runtime-)?(?<os>linux|windows)-(?<version>\d+\.\d+\.\d+)\.zip\z", RegexOptions.CultureInvariant);
    private static readonly Regex S2mTag = new(@"\Av?(?<version>\d+\.\d+\.\d+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex S2mAsset = new(@"(?i)\Asource2mod_v(?<version>\d+\.\d+\.\d+)-(?<os>linux|windows)\.tar\.gz\z", RegexOptions.CultureInvariant);
    private static readonly Regex SwiftlyTag = new(@"\Av(?<version>\d+\.\d+\.\d+(?:-[A-Za-z]+\.\d+)?)\z", RegexOptions.CultureInvariant);
    private static readonly Regex SwiftlyAsset = new(@"(?i)\Aswiftlys2-(?<os>linux|windows)-v(?<version>\d+\.\d+\.\d+(?:-[A-Za-z]+\.\d+)?)(?<runtime>-with-runtimes)?\.zip\z", RegexOptions.CultureInvariant);
    private static readonly Regex ModSharpTag = new(@"\Agit-(?<build>\d+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex ModSharpAsset = new(@"(?i)\AModSharp-git(?<build>\d+)-(?<os>linux|windows)(?<extensions>-extensions)?\.zip\z", RegexOptions.CultureInvariant);

    public static FrameworkDefinition? Find(string? id) =>
        Known.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));

    public static FrameworkReadinessProfile ReadinessProfileFor(string? id) =>
        Find(id)?.Readiness ?? FrameworkReadinessProfile.Standard;

    public static FrameworkPersistentPath[] PersistentPathsFor(string? id) =>
        Find(id)?.PersistentPaths ?? [];

    public static string[] PayloadRootsFor(string? id) =>
        Find(id)?.PayloadRoots ?? [];

    public static string[] MaterializedRootsFor(string? id) =>
        Find(id)?.MaterializedRoots ?? [];
    public static string? PluginRootFor(string? id) => Find(id)?.PluginRoot;
    public static string? PluginDataRootFor(string? id) => Find(id)?.PluginDataRoot;

    public static PluginCategory CategoryFor(string? framework, bool frameworkPayload)
    {
        if (frameworkPayload) return PluginCategory.Framework;
        return framework switch
        {
            "metamod" => PluginCategory.Metamod,
            "counterstrikesharp" => PluginCategory.CounterStrikeSharp,
            "swiftlys2" => PluginCategory.SwiftlyS2,
            "source2mod" => PluginCategory.Source2Mod,
            "modsharp" => PluginCategory.ModSharp,
            _ => PluginCategory.Other
        };
    }

    /// <summary>Classify a managed plugin from deps.json package/assembly names without executing it.</summary>
    public static ManagedPluginFramework ClassifyManagedDependencies(IEnumerable<string> dependencyNames)
    {
        var names = dependencyNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        var swiftly = names.Any(n => n.Equals("SwiftlyS2.CS2", StringComparison.OrdinalIgnoreCase)
            || n.Equals("SwiftlyS2.Shared", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("SwiftlyS2.", StringComparison.OrdinalIgnoreCase));
        var modsharp = names.Any(n => n.Equals("ModSharp.Sharp.Shared", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Sharp.Shared", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("ModSharp.", StringComparison.OrdinalIgnoreCase));
        if (swiftly && modsharp) return ManagedPluginFramework.Ambiguous;
        return swiftly ? ManagedPluginFramework.SwiftlyS2
            : modsharp ? ManagedPluginFramework.ModSharp : ManagedPluginFramework.Unknown;
    }

    public static string? FrameworkId(this ManagedPluginFramework framework) => framework switch
    {
        ManagedPluginFramework.SwiftlyS2 => "swiftlys2",
        ManagedPluginFramework.ModSharp => "modsharp",
        _ => null
    };

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
        if (string.Equals(id, "source2mod", StringComparison.Ordinal))
        {
            var match = S2mTag.Match(tag);
            return match.Success ? match.Groups["version"].Value : null;
        }
        if (string.Equals(id, "swiftlys2", StringComparison.Ordinal))
        {
            var match = SwiftlyTag.Match(tag);
            return match.Success ? match.Groups["version"].Value : null;
        }
        if (string.Equals(id, "modsharp", StringComparison.Ordinal))
        {
            var match = ModSharpTag.Match(tag);
            return match.Success ? "git" + match.Groups["build"].Value : null;
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
        if (string.Equals(id, "source2mod", StringComparison.Ordinal))
        {
            foreach (var name in assets)
            {
                var match = S2mAsset.Match(name);
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
        if (string.Equals(id, "swiftlys2", StringComparison.Ordinal))
        {
            foreach (var name in assets)
            {
                var match = SwiftlyAsset.Match(name);
                if (!match.Success || !match.Groups["os"].Value.Equals(os, StringComparison.OrdinalIgnoreCase)) continue;
                if (withRuntime != match.Groups["runtime"].Success) continue;
                return name;
            }
            return null;
        }
        if (string.Equals(id, "modsharp", StringComparison.Ordinal))
        {
            foreach (var name in assets)
            {
                var match = ModSharpAsset.Match(name);
                if (match.Success && !match.Groups["extensions"].Success
                    && match.Groups["os"].Value.Equals(os, StringComparison.OrdinalIgnoreCase)) return name;
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
        if (string.Equals(id, "source2mod", StringComparison.Ordinal))
        {
            var match = S2mAsset.Match(asset);
            return match.Success ? match.Groups["version"].Value : null;
        }
        if (string.Equals(id, "swiftlys2", StringComparison.Ordinal))
        {
            var match = SwiftlyAsset.Match(asset);
            return match.Success ? match.Groups["version"].Value : null;
        }
        if (string.Equals(id, "modsharp", StringComparison.Ordinal))
        {
            var match = ModSharpAsset.Match(asset);
            return match.Success ? "git" + match.Groups["build"].Value : null;
        }
        return null;
    }

    /// <summary>Prefer non-prerelease versions; fall back to prereleases only when no stable version exists.</summary>
    public static IEnumerable<string> OrderVersions(string id, IEnumerable<string> versions)
    {
        var all = versions.Distinct(StringComparer.Ordinal).ToArray();
        var comparer = Comparer<string>.Create((a, b) => CompareVersions(id, a, b));
        return all.Where(v => !v.Contains('-')).OrderByDescending(v => v, comparer)
            .Concat(all.Where(v => v.Contains('-')).OrderByDescending(v => v, comparer));
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

    /// <summary>One enabled package projection used by the selection gate.</summary>
    public sealed record SelectedFramework(string Id, string? Framework, bool IsFrameworkPackage = false);

    /// <summary>
    /// Derive the owning framework from game-relative package paths. Most specific markers win:
    /// a CounterStrikeSharp plugin also ships addons/metamod/*.vdf but belongs to CSSharp, not Metamod.
    /// </summary>
    public static string? Resolve(IEnumerable<string> relativePaths)
    {
        var paths = relativePaths.ToArray();
        foreach (var definition in Known)
        {
            if (definition.Kind == FrameworkKind.Base) continue;
            var marker = "csgo/addons/" + definition.Id + "/";
            if (paths.Any(p => p.StartsWith(marker, StringComparison.OrdinalIgnoreCase))) return definition.Id;
            if (definition.Id == "modsharp" && paths.Any(p => p.StartsWith("sharp/", StringComparison.OrdinalIgnoreCase))) return "modsharp";
        }
        if (paths.Any(p => p.StartsWith("csgo/addons/metamod/bin/", StringComparison.OrdinalIgnoreCase))) return "metamod";
        return null;
    }

    /// <summary>True only for a package that ships the framework loader itself, not a sub-plugin using its folder.</summary>
    public static bool IsFrameworkPayload(string? framework, IEnumerable<string> relativePaths)
    {
        var paths = relativePaths.ToArray();
        return framework switch
        {
            "metamod" => paths.Any(p => p.StartsWith("csgo/addons/metamod/bin/", StringComparison.OrdinalIgnoreCase)),
            "counterstrikesharp" => paths.Any(p => p.StartsWith("csgo/addons/counterstrikesharp/bin/", StringComparison.OrdinalIgnoreCase))
                && paths.Any(p => p.EndsWith("/counterstrikesharp.so", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith("/counterstrikesharp.dll", StringComparison.OrdinalIgnoreCase)),
            "source2mod" => paths.Any(p => p.EndsWith("/bin/linuxsteamrt64/source2mod.so", StringComparison.OrdinalIgnoreCase))
                || paths.Any(p => p.Equals("csgo/addons/metamod/source2mod.vdf", StringComparison.OrdinalIgnoreCase)),
            "swiftlys2" => paths.Any(p => p.EndsWith("/bin/linuxsteamrt64/swiftlys2.so", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("/bin/win64/swiftlys2.dll", StringComparison.OrdinalIgnoreCase)),
            "modsharp" => paths.Any(p => p.EndsWith("/bin/libmodsharp.so", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith("/bin/libmodsharp.dll", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    /// <summary>
    /// Reject two scripting frameworks and scripting packages without their framework/base.
    /// Base Metamod and unknown native plugins impose no extra requirement.
    /// </summary>
    public static void ValidateSelections(IEnumerable<SelectedFramework> enabled)
    {
        var selected = enabled.ToArray();
        var scripts = selected
            .Select(x => x.Framework)
            .Where(f => Find(f)?.Kind == FrameworkKind.ScriptFramework)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scripts.Length > 1)
            throw new InstanceException("HCSL-PLUGIN-FRAMEWORK-CONFLICT",
                "A preset can enable only one scripting framework; found " + string.Join(", ", scripts) + ".");
        foreach (var script in scripts)
        {
            if (!selected.Any(x => string.Equals(x.Id, script, StringComparison.Ordinal) && x.IsFrameworkPackage))
                throw new InstanceException("HCSL-PLUGIN-FRAMEWORK-MISSING",
                    "The preset selects a " + script + " plugin but not a valid " + script + " framework package.");
            var definition = Find(script);
            if (definition?.Requires == "metamod"
                && !selected.Any(x => string.Equals(x.Framework, "metamod", StringComparison.Ordinal)))
                throw new InstanceException("HCSL-PLUGIN-FRAMEWORK-MISSING",
                    script + " requires the metamod package to be enabled in the same preset.");
        }
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
