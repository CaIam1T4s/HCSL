using System.Text;
using Terminal.Gui;
namespace HCSL.Tui.Views.Controls;

/// <summary>A small Vim-style editor, not a Vim emulator. Native TextView owns insert/selection/Unicode rendering.</summary>
public sealed class VimCfgTextView : TextView
{
    private sealed record Snapshot(string Text, Point Cursor);
    private readonly Stack<Snapshot> undo = new(), redo = new();
    private char prefix;
    public bool Inserting { get; private set; }
    public event Action? ModeChanged;
    public event Action? CommandRequested;
    public event Action<Key>? ShortcutRequested;
    public VimCfgTextView() { WordWrap = false; ReadOnly = true; }
    public void LoadDocument(string value)
    {
        Text = value; CursorPosition = new(0, 0); TopRow = 0;
        undo.Clear(); redo.Clear(); Normal();
    }
    public void Normal()
    {
        if (Inserting && undo.TryPeek(out var entry))
        {
            if (entry.Text == (Text.ToString() ?? "")) undo.Pop(); else redo.Clear();
        }
        Inserting = false; ReadOnly = true; prefix = '\0'; ModeChanged?.Invoke();
    }
    private int LineLength => (Text.ToString() ?? "").Split('\n')[CurrentRow].EnumerateRunes().Count();
    private void Insert() { Remember(false); Inserting = true; ReadOnly = false; prefix = '\0'; ModeChanged?.Invoke(); }
    private void Remember(bool clearRedo = true)
    {
        if (undo.Count >= 50) { var keep = undo.Reverse().Skip(1).ToArray(); undo.Clear(); foreach (var s in keep) undo.Push(s); }
        undo.Push(new(Text.ToString() ?? "", CursorPosition)); if (clearRedo) redo.Clear();
    }
    private void Restore(Stack<Snapshot> source, Stack<Snapshot> target)
    {
        if (source.Count == 0) return;
        target.Push(new(Text.ToString() ?? "", CursorPosition));
        var value = source.Pop(); Text = value.Text; CursorPosition = value.Cursor;
    }
    private void Native(Key key) => base.ProcessKey(new(key, new KeyModifiers()));
    private void Mutate(Action action)
    {
        Remember(); ReadOnly = false;
        try { action(); } finally { ReadOnly = true; }
    }
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        var key = keyEvent.Key;
        if (key is Key.F2 or Key.F6 or Key.F10 || key == (Key.CtrlMask | Key.Q))
        { ShortcutRequested?.Invoke(key); return true; }
        if (key == Key.Esc) { Normal(); return true; }
        if (Inserting) return base.ProcessKey(keyEvent);
        if (key == (Key.CtrlMask | Key.R)) { Restore(redo, undo); return true; }
        var c = (char)(uint)key;
        var previous = prefix; prefix = '\0';
        switch (c)
        {
            case ':': CommandRequested?.Invoke(); return true;
            case 'i': Insert(); return true;
            case 'a': CursorPosition = new(Math.Min(CurrentColumn + 1, LineLength), CurrentRow); Insert(); return true;
            case 'A': Native(Key.End); Insert(); return true;
            case 'I': Native(Key.Home); Insert(); return true;
            case 'o': Native(Key.End); Insert(); Native(Key.Enter); return true;
            case 'O': Native(Key.Home); Insert(); Native(Key.Enter); Native(Key.CursorUp); return true;
            case 'h': CursorPosition = new(Math.Max(0, CurrentColumn - 1), CurrentRow); return true;
            case 'j': Native(Key.CursorDown); return true;
            case 'k': Native(Key.CursorUp); return true;
            case 'l': CursorPosition = new(Math.Min(LineLength, CurrentColumn + 1), CurrentRow); return true;
            case '0': Native(Key.Home); return true;
            case '$': Native(Key.End); return true;
            case 'g': if (previous == 'g') MoveHome(); else prefix = 'g'; return true;
            case 'G': MoveEnd(); return true;
            case 'u': Restore(undo, redo); return true;
            case 'x': Mutate(DeleteCharRight); return true;
            case 'd':
                if (previous == 'd')
                {
                    Mutate(() =>
                    {
                        var lines = (Text.ToString() ?? "").Split('\n').ToList();
                        var row = Math.Clamp(CurrentRow, 0, lines.Count - 1);
                        lines.RemoveAt(row); Text = string.Join("\n", lines);
                        CursorPosition = new(0, Math.Min(row, Math.Max(0, lines.Count - 1)));
                    });
                }
                else prefix = 'd';
                return true;
        }
        // Navigation/focus keys remain native; printable normal-mode keys must never insert text.
        if (key is Key.CursorLeft or Key.CursorRight or Key.CursorUp or Key.CursorDown or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Tab or Key.BackTab)
            return base.ProcessKey(keyEvent);
        return true;
    }
}
