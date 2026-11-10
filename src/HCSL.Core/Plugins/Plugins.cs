using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HCSL.Core;

/// <summary>One package version selected by a preset. Array order is the intended load order.</summary>
public sealed record PluginSelection(string Id, string Version, bool Enabled = true);

public enum PluginAction { List, Save }

/// <summary>Plugin manifest editing. ExpectedHash protects against concurrent external edits.</summary>
public sealed record PluginCommand(PluginAction Action, Guid InstanceId, Guid PresetId,
    long InstanceRevision = 0, string? ExpectedHash = null, PluginSelection[]? Plugins = null)
{
    public override string ToString() => $"PluginCommand {Action} {InstanceId}/{PresetId}";
}

public sealed record PluginManifest(int FormatVersion, long Revision, PluginSelection[] Plugins);

/// <summary>Import a plugin release (directory or archive) into the local library. The source is read, never executed.</summary>
public sealed record PluginImportCommand(Guid InstanceId, Guid PresetId, string SourceDirectory, string Id, string Name, string Version,
    string? MetaPlugin = null, bool Enable = false, bool Force = false, long InstanceRevision = 0, string? ArchivePath = null,
    bool CfgIntoPreset = false);

/// <summary>Read-only probe of a folder/archive: detected identity and layout for the import form.</summary>
public sealed record PluginImportProbeCommand(string SourcePath);

public sealed record PluginImportPreview(string SourcePath, string Kind, string Id, string Name, string Version,
    string? MetaPlugin, int Files, long Bytes, string[] Warnings);

/// <summary>Result of an import. Warnings are advisory; the package is already written when this is returned.</summary>
public sealed record PluginImportResult(string PackagePath, string Id, string Version, int Files, long Bytes,
    bool CurrentPlatformSupported, string[] Warnings, bool Enabled, int ImportedCfg = 0, int KeptCfg = 0);

/// <summary>A validated package available under &lt;library&gt;/plugin-packages.</summary>
public sealed record PluginPackageInfo(string Id, string Name, string Version, [property: JsonIgnore] string Hash, int Files, long Bytes,
    string? Framework = null, bool FrameworkPayload = false);

/// <summary>Selected entry joined with package availability. Never contains file contents.</summary>
public sealed record PluginSelectionInfo(string Id, string? Name, string Version, bool Enabled, bool Available, string? Error = null);

/// <summary>A directory under plugin-packages that was not listed, with the stable reason code.</summary>
public sealed record PluginPackageRejection(string Id, string Version, string Code);

public sealed record PluginResult(PluginPackageInfo[] Available, PluginSelectionInfo[] Selected, long Revision, string Hash,
    PluginPackageRejection[]? Rejected = null, int AvailableTotal = 0);

public static class PluginRules
{
    public const int MaxPlugins = 32;
    public const int MaxPackageFiles = 4096;
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const int MaxManifestBytes = 1024 * 1024;
    /// <summary>Upper bound for one List response; the UI reports the total when more versions exist.</summary>
    public const int MaxListedPackages = 256;
    private static readonly Regex IdPattern = new(@"\A[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\z", RegexOptions.CultureInvariant);

    public static string Id(string? id)
    {
        if (id is null || !IdPattern.IsMatch(id)) throw new InstanceException("HCSL-PLUGIN-ID", "Plugin ID must be 1–64 lowercase letters, digits, dots, underscores or hyphens.");
        return id;
    }

    public static string Version(string? version)
    {
        if (version is null || !VersionPattern.IsMatch(version)) throw new InstanceException("HCSL-PLUGIN-VERSION", "Plugin version contains unsupported characters.");
        return version;
    }

    public static void Validate(PluginSelection[]? plugins)
    {
        plugins ??= [];
        if (plugins.Length > MaxPlugins) throw new InstanceException("HCSL-PLUGIN-LIMIT", "At most 32 plugin selections are supported.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            Id(plugin.Id); Version(plugin.Version);
            if (!ids.Add(plugin.Id)) throw new InstanceException("HCSL-PLUGIN-DUPLICATE", "A plugin may be selected only once per preset.");
        }
    }

    /// <summary>Package-relative paths are game-root relative, e.g. csgo/addons/metamod.</summary>
    public static string RelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Any(char.IsControl))
            throw new InstanceException("HCSL-PLUGIN-PATH", "Invalid package path.");
        var parts = path.Split('/');
        if (parts.Any(p => p is "" or "." or "..") || parts.Length == 0)
            throw new InstanceException("HCSL-PLUGIN-PATH", "Package paths may not traverse directories.");
        if (parts[0] != "csgo" && parts[0] != "core" && parts[0] != "csgo_lv")
            throw new InstanceException("HCSL-PLUGIN-PATH", "Package files must stay under the game content roots.");
        return string.Join('/', parts);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True for the root itself or a path below it, using platform path-case rules.</summary>
    public static bool IsPathUnder(string path, string root) =>
        path.Equals(root, PathComparison) || path.StartsWith(root + "/", PathComparison);

    /// <summary>Mapped persistent directories that a package may never write.</summary>
    public static bool IsReservedDeploymentPath(string path) =>
        IsPathUnder(path, "csgo/cfg") || IsPathUnder(path, "csgo/logs") || IsPathUnder(path, "csgo/recordings");

    /// <summary>True for a file inside csgo/addons (not the addons root itself).</summary>
    public static bool IsAddonFile(string path) =>
        IsPathUnder(path, "csgo/addons") && !path.Equals("csgo/addons", PathComparison);

    /// <summary>A file cannot take the place of the mapped addons directory.</summary>
    public static bool IsAddonsRoot(string path) => path.Equals("csgo/addons", PathComparison);
}
