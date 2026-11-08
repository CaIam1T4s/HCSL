using Terminal.Gui;

namespace HCSL.Tui.Presentation;
using Application = global::Terminal.Gui.Application;

/// <summary>Fixed dark palette using only ncurses base colors (0-7).</summary>
public static class ThemeCatalog
{
    public static ColorScheme Create(bool readOnlyContent = false)
    {
        var gray = Application.Driver.MakeAttribute(Color.Gray, Color.Black);
        var normal = readOnlyContent ? gray : CreateNormal(gray);
        var focus = readOnlyContent ? normal : CreateFocus(gray);
        return new ColorScheme
        {
            Normal = normal,
            Focus = focus,
            // Keep automatic button mnemonics readable without extended bright colors.
            HotNormal = normal,
            HotFocus = focus,
            Disabled = readOnlyContent ? normal : CreateDisabled()
        };
    }

    private static Terminal.Gui.Attribute CreateDisabled()
    {
        if (Application.Driver.GetType().Name == "CursesDriver")
        {
            // A_DIM applies low intensity without using color IDs 8-15.
            // Use the default pair: linux terminfo ncv forbids DIM with color pairs,
            // so allocate no pair at all - pair zero is white-on-black, dimmed.
            const int dim = 1 << 20;
            return new Terminal.Gui.Attribute(dim);
        }
        return Application.Driver.MakeAttribute(Color.DarkGray, Color.Black);
    }

    private static Terminal.Gui.Attribute CreateNormal(Terminal.Gui.Attribute gray)
    {
        if (Application.Driver.GetType().Name == "CursesDriver")
        {
            // Use bold on an existing base pair, not an unsupported bright color ID.
            // Enabled controls are white; disabled controls use a separate low-intensity style.
            const int bold = 1 << 21;
            return new Terminal.Gui.Attribute((int)gray | bold, Color.White, Color.Black);
        }
        return Application.Driver.MakeAttribute(Color.White, Color.Black);
    }

    private static Terminal.Gui.Attribute CreateFocus(Terminal.Gui.Attribute normal)
    {
        if (Application.Driver.GetType().Name == "CursesDriver")
        {
            // ncurses A_REVERSE swaps the existing gray-on-black pair on screen.
            // This preserves the gray background without allocating pair 112,
            // which exceeds TERM=linux's 64-pair limit in Terminal.Gui v1.
            const int reverseVideo = 1 << 18;
            return new Terminal.Gui.Attribute((int)normal | reverseVideo, Color.Black, Color.Gray);
        }
        return Application.Driver.MakeAttribute(Color.Black, Color.Gray);
    }
}
