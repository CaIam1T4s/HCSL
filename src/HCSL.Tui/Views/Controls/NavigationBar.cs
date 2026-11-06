using HCSL.Tui.Presentation;
using Terminal.Gui;

namespace HCSL.Tui.Views.Controls;

/// <summary>Tab and arrows select a peer and update its page; Enter descends.</summary>
public sealed class NavigationBar : View
{
    public IReadOnlyList<string> Titles { get; set; } = [];
    public int ActiveIndex { get; set; }
    public int CursorIndex { get; set; }
    public event Action<int>? Activated;
    public event Action<int>? SelectionChanged;
    public const int ItemWidth = 13;

    public NavigationBar()
    {
        CanFocus = true;
        Height = 1;
        Enter += _ => SetNeedsDisplay();
        Leave += _ => SetNeedsDisplay();
    }

    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (Titles.Count == 0) return base.ProcessKey(keyEvent);
        switch (keyEvent.Key)
        {
            case Key.BackTab:
            case Key.CursorLeft: CursorIndex = (CursorIndex + Titles.Count - 1) % Titles.Count; break;
            case Key.Tab:
            case Key.CursorRight: CursorIndex = (CursorIndex + 1) % Titles.Count; break;
            case Key.Home: CursorIndex = 0; break;
            case Key.End: CursorIndex = Titles.Count - 1; break;
            case Key.CursorDown:
            case Key.Enter: Activated?.Invoke(CursorIndex); return true;
            default: return base.ProcessKey(keyEvent);
        }
        ActiveIndex = CursorIndex;
        SelectionChanged?.Invoke(CursorIndex);
        SetNeedsDisplay();
        return true;
    }

    public override bool MouseEvent(MouseEvent mouseEvent)
    {
        if (!mouseEvent.Flags.HasFlag(MouseFlags.Button1Clicked) || Titles.Count == 0) return false;
        SetFocus();
        for (var i = 0; i < Titles.Count; i++)
            if (mouseEvent.X >= i * ItemWidth && mouseEvent.X < (i + 1) * ItemWidth)
            {
                CursorIndex = i;
                ActiveIndex = i;
                SelectionChanged?.Invoke(i);
                Activated?.Invoke(i);
                SetNeedsDisplay();
                return true;
            }
        return false;
    }

    public override void Redraw(Rect bounds)
    {
        for (var i = 0; i < Titles.Count; i++)
        {
            var start = i * ItemWidth;
            if (start >= Bounds.Width) break;
            var cellWidth = Math.Min(ItemWidth, Bounds.Width - start);
            Driver.SetAttribute(HasFocus && CursorIndex == i ? ColorScheme.Focus : ColorScheme.Normal);
            Move(start, 0);
            // One column of highlighted padding outside each bracket; labels stay equal width.
            var label = "[" + TerminalLayout.Center(Titles[i], Math.Max(0, cellWidth - 5)) + "]";
            Driver.AddStr(" " + label + " ");
            Driver.SetAttribute(ColorScheme.Normal);
            Driver.AddStr(" ");
        }
    }
}
