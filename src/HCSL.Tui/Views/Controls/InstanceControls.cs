using Terminal.Gui;
using HCSL.Tui.Presentation;

namespace HCSL.Tui.Views.Controls;

public sealed class InstanceListView : ListView
{
    public event Action<bool>? ActionsRequested;

    public override bool ProcessKey(KeyEvent keyEvent)
    {
        switch (keyEvent.Key)
        {
            case Key.CursorRight:
            case Key.Enter:
                ActionsRequested?.Invoke(true);
                return true;
            case Key.Tab:
            case Key.BackTab:
                // Tab is intentionally not a shortcut out of the instance list.
                return true;
            case Key.CursorUp:
                if ((Source?.Count ?? 0) == 0 || SelectedItem <= 0) return true;
                break;
            case Key.CursorDown:
                if ((Source?.Count ?? 0) == 0 || SelectedItem >= Source!.Count - 1) return true;
                break;
        }
        return base.ProcessKey(keyEvent);
    }
}

public sealed class InstanceActionButton : Button
{
    public event Action<int>? PeerRequested;

    public override void Redraw(Rect bounds)
    {
        var state = !Enabled ? ColorScheme.Disabled : HasFocus ? ColorScheme.Focus : ColorScheme.Normal;
        var width = Math.Max(0, Bounds.Width);
        var label = " [" + TerminalLayout.Center(Text.ToString() ?? "", Math.Max(0, width - 4)) + "] ";
        // Clear the whole slot with the ordinary background first: a disabled button must read as dim text,
        // not as a filled block sitting next to its neighbours.
        Driver.SetAttribute(ColorScheme.Normal);
        Move(0, 0);
        Driver.AddStr(new string(' ', width));
        Driver.SetAttribute(state);
        Move(0, 0);
        Driver.AddStr(label.Length >= width ? label[..width] : label);
    }

    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (keyEvent.Key is Key.Tab or Key.BackTab)
        {
            PeerRequested?.Invoke(keyEvent.Key == Key.Tab ? 1 : -1);
            return true;
        }
        return base.ProcessKey(keyEvent);
    }
}
