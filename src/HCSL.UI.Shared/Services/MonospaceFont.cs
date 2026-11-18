using Avalonia.Media;

namespace HCSL.UI.Shared.Services;

/// <summary>Resolves an installed monospace family once; logs and code views use it.</summary>
public static class MonospaceFont
{
    private static FontFamily? cached;

    public static FontFamily Resolve()
    {
        if (cached is not null) return cached;
        string[] candidates =
        [
            "JetBrains Mono", "JetBrainsMono Nerd Font", "Cascadia Mono", "Consolas",
            "Source Code Pro", "Adwaita Mono", "DejaVu Sans Mono", "Liberation Mono",
            "Noto Sans Mono", "Ubuntu Mono", "Nimbus Mono PS", "Menlo", "Monaco", "Courier New"
        ];
        var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // "monospace" is a fontconfig alias on Linux and resolves even when no family above is installed.
        var name = candidates.FirstOrDefault(installed.Contains) ?? "monospace";
        return cached = new FontFamily(name);
    }
}
