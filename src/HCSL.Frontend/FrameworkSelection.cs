using HCSL.Core;
namespace HCSL.Frontend;

public sealed record FrameworkChoice(PluginSelection[] Selections, string? MissingFramework, string[] Warnings);

/// <summary>Single place for switching the preset's scripting framework; GUI and card actions share it.</summary>
public static class FrameworkSelection
{
    public static string? Current(PluginResult result)
    {
        foreach (var selection in result.Selected.Where(s => s.Enabled))
        {
            var package = Package(result, selection.Id, selection.Version);
            if (package?.Framework is { } framework && FrameworkRules.Find(framework)?.Kind == FrameworkKind.ScriptFramework
                && package.FrameworkPayload && string.Equals(selection.Id, framework, StringComparison.Ordinal))
                return framework;
        }
        return null;
    }

    /// <summary>
    /// Disable the old scripting framework and its plugins, enable metamod, and enable the chosen
    /// framework package. Nothing is removed; the caller saves the manifest explicitly.
    /// </summary>
    public static FrameworkChoice Apply(PluginSelection[] current, string? target, PluginResult available)
    {
        if (target is not null && (FrameworkRules.Find(target)?.Kind != FrameworkKind.ScriptFramework))
            throw new InstanceException("HCSL-PLUGIN-FRAMEWORK-ID", "Not a scripting framework.");
        var warnings = new List<string>();
        var needsBase = target is not null && FrameworkRules.Find(target)?.Requires == "metamod";
        PluginSelection? metamod = current.FirstOrDefault(s => s.Id == "metamod");
        var targetPackage = target is null ? null : LatestFramework(available, target);
        if (target is not null && targetPackage is null)
            return new(current, target, ["Framework package is not installed: " + target]);
        var next = new List<PluginSelection>();
        PluginSelection? targetSelection = null;
        foreach (var selection in current)
        {
            if (selection.Id is "metamod")
            {
                metamod = needsBase ? selection with { Enabled = true } : selection;
                continue;
            }
            if (target is not null && selection.Id == target)
            {
                targetSelection = selection with { Enabled = true, Version = targetPackage!.Version };
                continue;
            }
            var package = Package(available, selection.Id, selection.Version);
            if (package?.Framework is { } framework && FrameworkRules.Find(framework)?.Kind == FrameworkKind.ScriptFramework
                && !string.Equals(framework, target, StringComparison.Ordinal))
                next.Add(selection with { Enabled = false });
            else next.Add(selection);
        }
        if (metamod is null && needsBase)
        {
            var basePackage = LatestFramework(available, "metamod");
            if (basePackage is null) warnings.Add("Metamod package is not installed.");
            else metamod = new("metamod", basePackage.Version, true);
        }
        var ordered = new List<PluginSelection>();
        if (metamod is not null) ordered.Add(metamod);
        if (target is not null)
        {
            targetSelection ??= new(target, targetPackage!.Version, true);
            ordered.Add(targetSelection);
        }
        ordered.AddRange(next.Where(s => s.Id != target && s.Id != "metamod"));
        return new([.. ordered], null, [.. warnings]);
    }

    private static PluginPackageInfo? LatestFramework(PluginResult result, string id) =>
        result.Available.Where(p => p.Id == id && p.FrameworkPayload)
            .OrderByDescending(p => p.Version, Comparer<string>.Create((a, b) => FrameworkRules.CompareVersions(id, a, b)))
            .FirstOrDefault();

    private static PluginPackageInfo? Package(PluginResult result, string id, string version) =>
        result.Available.FirstOrDefault(p => p.Id == id && p.Version == version);
}
