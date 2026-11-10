namespace HCSL.Core;

/// <summary>Enable/disable-time cfg sync is always previewed first; Apply/Cleanup never overwrite unconfirmed files.</summary>
public enum PluginCfgAction { Preview, Apply, CleanupPreview, CleanupApply }

public sealed record PluginCfgCommand(PluginCfgAction Action, Guid InstanceId, Guid PresetId, long InstanceRevision = 0,
    PluginSelection[]? Enable = null, PluginSelection[]? Disable = null, string[]? Overwrite = null, string[]? Delete = null);

/// <summary>One package-owned cfg file relative to csgo/cfg. Content is never returned to the UI.</summary>
public sealed record PluginCfgEntry(string PluginId, string Version, string Path, long Bytes,
    string SourceHash, string? ExistingHash, bool Modified = false);

public sealed record PluginCfgPreview(PluginCfgEntry[] Import, PluginCfgEntry[] Conflicts, PluginCfgEntry[] Identical,
    PluginCfgEntry[] Removable, PluginCfgEntry[] Modified, bool Truncated = false);

public sealed record PluginCfgResult(int Imported, int Overwritten, int Deleted, string[] Warnings);

public static class PluginCfgRules
{
    public const int MaxPreviewEntries = 512;
    public const long MaxFileBytes = 16L * 1024 * 1024;
    public const long MaxTotalBytes = 256L * 1024 * 1024;

    public static string Relative(string relativePath)
    {
        if (!relativePath.StartsWith("csgo/cfg/", StringComparison.OrdinalIgnoreCase))
            throw new InstanceException("HCSL-CFG-PATH", "Package cfg files must live under csgo/cfg.");
        var relative = relativePath["csgo/cfg/".Length..];
        return PresetCfgRules.Path(relative, requireCfgFile: false);
    }
}
