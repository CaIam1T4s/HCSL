using HCSL.Core;

namespace HCSL.Frontend.Dev;

/// <summary>Resolves instance/preset selectors (UUID, exact name, or unique prefix) for the dev CLI.</summary>
public static class DevTargets
{
    public static InstanceEntry Resolve(InstanceEntry[] instances, string? selector)
    {
        if (instances.Length == 0) throw new DevUsageException("No instances in this workspace. Create one in the GUI/TUI first.");
        if (string.IsNullOrWhiteSpace(selector))
        {
            if (instances.Length == 1) return instances[0];
            throw new DevUsageException("Several instances exist; pass --instance <UUID|name>. Known: " + Describe(instances)
                + Environment.NewLine + "List them with: hcsl instances");
        }
        var text = selector.Trim();
        if (Guid.TryParseExact(text, "D", out var id))
            return instances.FirstOrDefault(i => i.Id == id)
                ?? throw new DevUsageException($"Instance {id:D} was not found. Known: " + Describe(instances));
        var exact = instances.Where(i => string.Equals(i.Definition?.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) throw new DevUsageException("Instance name '" + text + "' is ambiguous: " + Describe(exact));
        var prefix = instances.Where(i => i.Definition?.Name?.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (prefix.Length == 1) return prefix[0];
        if (prefix.Length > 1) throw new DevUsageException("Instance selector '" + text + "' matches several instances: " + Describe(prefix));
        var contains = instances.Where(i => i.Definition?.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (contains.Length == 1) return contains[0];
        if (contains.Length > 1) throw new DevUsageException("Instance selector '" + text + "' matches several instances: " + Describe(contains));
        throw new DevUsageException($"Instance '{text}' was not found. Known: " + Describe(instances)
            + Environment.NewLine + "List them with: hcsl instances");
    }

    public static PresetDefinition ResolvePreset(InstanceEntry instance, string? selector)
    {
        var selected = instance.Presets.FirstOrDefault(p => p.Id == instance.Definition?.SelectedPresetId)
            ?? instance.Presets.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selector))
            return selected ?? throw new DevUsageException($"Instance {instance.Id:D} has no presets.");
        if (Guid.TryParseExact(selector, "D", out var id))
            return instance.Presets.FirstOrDefault(p => p.Id == id)
                ?? throw new DevUsageException($"Preset {id:D} does not belong to instance {instance.Id:D}. Known: " + DescribePresets(instance));
        var text = selector.Trim();
        var exact = instance.Presets.Where(p => p.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length == 0)
        {
            var prefix = instance.Presets.Where(p => p.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (prefix.Length == 1) return prefix[0];
            if (prefix.Length > 1) throw new DevUsageException("Preset prefix '" + text + "' is ambiguous. Known: " + DescribePresets(instance));
        }
        throw new DevUsageException($"Preset '{text}' is unknown. Known: " + DescribePresets(instance));
    }

    private static string Describe(InstanceEntry[] instances) =>
        string.Join(", ", instances.Select(i => (i.Definition?.Name ?? "?") + "=" + i.Id.ToString("D")));

    private static string DescribePresets(InstanceEntry instance) =>
        string.Join(", ", instance.Presets.Select(p => p.Name + "=" + p.Id.ToString("D")));
}
