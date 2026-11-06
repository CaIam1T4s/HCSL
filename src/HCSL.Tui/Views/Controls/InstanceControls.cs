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
        Driver.SetAttribute(!Enabled ? ColorScheme.Disabled : HasFocus ? ColorScheme.Focus : ColorScheme.Normal);
        Move(0, 0);
        var label = "[" + TerminalLayout.Center(Text.ToString() ?? "", Math.Max(0, Bounds.Width - 4)) + "]";
        Driver.AddStr(" " + label + " ");
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
