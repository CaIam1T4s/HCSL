using Avalonia.Controls;
using Avalonia.Media;
using HCSL.UI.Shared.Services;

internal static class CenteredShadowTests
{
    public static void Run()
    {
        var source = BoxShadows.Parse("2 8 20 -2 #90000000");
        var centered = CenteredShadow.Center(source);
        Check(centered.Count == source.Count && centered[0].OffsetX == 0 && centered[0].OffsetY == 0 &&
              centered[0].Blur == source[0].Blur && centered[0].Spread == source[0].Spread && centered[0].Color == source[0].Color,
            "Centered elevation must preserve token blur/spread/color.");
        Check(CenteredShadow.Center(default).Count == 0, "Empty elevation must remain empty.");
        var border = new Border();
        CenteredShadow.SetSource(border, source);
        Check(border.BoxShadow.Equals(centered), "The attached source must update the rendered shadow.");
        CenteredShadow.SetSource(border, default);
        Check(border.BoxShadow.Count == 0, "Clearing the source must clear its elevation.");
        Console.WriteLine("PASS gui.shadow.centered-token-preservation-and-attached-updates");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
