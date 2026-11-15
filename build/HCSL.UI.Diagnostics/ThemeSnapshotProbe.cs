using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace HCSL.UI.Diagnostics;

/// <summary>Independent renders using Arc's capture formula; these are not the private bitmap owned by Arc.</summary>
public static class ThemeSnapshotProbe
{
    public static void Capture(TopLevel window, string directory, string label)
    {
        Directory.CreateDirectory(directory);
        var size = window.Bounds.Size;
        foreach (var (name, scale) in new[] { ("native", window.RenderScaling), ("96dpi", 1d) })
        {
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(directory, $"{label}-{name}.png"), PngBitmapEncoderOptions.Default);
            File.WriteAllText(Path.Combine(directory, $"{label}-{name}.txt"),
                $"Independent RenderTargetBitmap.Render(window), not Arc's private snapshot.\n" +
                $"Window DIPs={size}; window scaling={window.RenderScaling}; pixels={pixels}; bitmap DPI={bitmap.Dpi}; bitmap DIPs={bitmap.Size}\n" +
                $"Expected Arc destination size={size}; native pixel source rectangle=0,0,{pixels.Width},{pixels.Height}\n");
        }
    }
}
