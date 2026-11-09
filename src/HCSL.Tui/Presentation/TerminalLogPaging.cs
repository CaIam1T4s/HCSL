using Terminal.Gui;

namespace HCSL.Tui.Presentation;

/// <summary>Moves only the log viewport; never changes keyboard focus.</summary>
public static class TerminalLogPaging
{
    public static int Direction(Key key) => key switch
    {
        Key.PageUp or (Key.CtrlMask | Key.PageUp) or Key.F3 => -1,
        Key.PageDown or (Key.CtrlMask | Key.PageDown) or Key.F4 => 1,
        _ => 0
    };

    public static int Page(TextView log, int direction)
    {
        var height = Math.Max(1, log.Bounds.Height);
        var top = Math.Clamp(log.TopRow + Math.Sign(direction) * Math.Max(1, height - 1),
            0, Math.Max(0, log.Lines - height));
        // Set the caret before TopRow, as TextView may scroll when its caret changes.
        if (log.Lines > 0) log.CursorPosition = new Point(0, top);
        log.TopRow = top;
        log.SetNeedsDisplay();
        return log.TopRow;
    }
}
