using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Shared category labels and stable display order for GUI, TUI and the dev CLI.</summary>
public static class PluginPresentation
{
    public static readonly PluginCategory[] CategoryOrder =
    [
        PluginCategory.Framework,
        PluginCategory.Metamod,
        PluginCategory.CounterStrikeSharp,
        PluginCategory.Source2Mod,
        PluginCategory.SwiftlyS2,
        PluginCategory.ModSharp,
        PluginCategory.Other
    ];

    public static string CategoryLabel(PluginCategory category, bool english = false) => category switch
    {
        PluginCategory.Framework => english ? "Framework" : "框架本体",
        PluginCategory.Metamod => "Metamod",
        PluginCategory.CounterStrikeSharp => "CounterStrikeSharp",
        PluginCategory.Source2Mod => "Source2Mod",
        PluginCategory.SwiftlyS2 => "SwiftlyS2",
        PluginCategory.ModSharp => "ModSharp",
        _ => english ? "Other" : "其他"
    };

    public static string FrameworkLabel(string? framework) => framework switch
    {
        "swiftlys2" => "SwiftlyS2",
        "modsharp" => "ModSharp",
        "metamod" => "Metamod",
        _ => "—"
    };

    public static string PackageLabel(PluginPackageInfo package, bool english = false) =>
        "[" + CategoryLabel(package.Category, english) + "] " + package.Name + " (" + package.Id + ") " + package.Version;

    /// <summary>Groups packages in CategoryOrder while preserving the package order inside each category.</summary>
    public static (PluginCategory Category, PluginPackageInfo[] Packages)[] Grouped(IEnumerable<PluginPackageInfo> packages)
    {
        var all = packages.ToArray();
        return CategoryOrder
            .Select(category => (Category: category, Packages: all.Where(package => package.Category == category).ToArray()))
            .Where(group => group.Packages.Length > 0)
            .ToArray();
    }
}
