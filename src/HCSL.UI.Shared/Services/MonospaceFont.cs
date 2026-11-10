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
            "Cascadia Mono", "Consolas", "DejaVu Sans Mono", "Liberation Mono",
            "Noto Sans Mono", "Ubuntu Mono", "Menlo", "Monaco", "Courier New"
        ];
        var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = candidates.FirstOrDefault(installed.Contains) ?? FontFamily.Default.Name;
        return cached = new FontFamily(name);
    }
}
