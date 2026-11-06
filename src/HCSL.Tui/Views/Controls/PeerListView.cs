using Terminal.Gui;

namespace HCSL.Tui.Views.Controls;

/// <summary>Menus cycle within their own level; Enter moves into the selected page.</summary>
public sealed class PeerListView : ListView
{
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (keyEvent.Key is Key.Tab or Key.BackTab)
        {
            var count = Source?.Count ?? 0;
            if (count > 0)
                SelectedItem = (SelectedItem + (keyEvent.Key == Key.Tab ? 1 : count - 1)) % count;
            return true;
        }
        return base.ProcessKey(keyEvent);
    }
}
