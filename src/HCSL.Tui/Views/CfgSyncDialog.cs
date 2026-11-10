using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

/// <summary>Checkbox list for plugin cfg conflicts/cleanup. Null means cancel; empty means skip.</summary>
public sealed class CfgSyncDialog : Dialog
{
    private readonly List<(string Path, CheckBox Box)> boxes = [];
    private string[]? result;
    private bool decided;
    private CfgSyncDialog(string title, string message, IEnumerable<(string Path, bool Checked, bool Disabled)> items, string applyText, string skipText)
    {
        Title = title; X = 1; Y = 1; Width = Dim.Fill(1); Height = Dim.Fill(1);
        Add(new Label(message) { X = 0, Y = 0, Width = Dim.Fill(), Height = 4 });
        var rows = items.ToArray();
        var scroll = new ScrollView { X = 0, Y = 4, Width = Dim.Fill(), Height = Dim.Fill(4) };
        var column = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Math.Max(1, rows.Length) };
        var y = 0;
        foreach (var item in rows)
        {
            var box = new CheckBox(item.Path) { X = 0, Y = y++, Checked = item.Checked, Enabled = !item.Disabled };
            boxes.Add((item.Path, box)); column.Add(box);
        }
        scroll.Add(column); Add(scroll);
        var apply = new Button(applyText);
        apply.Clicked += () => { decided = true; result = boxes.Where(b => b.Box.Checked).Select(b => b.Path).ToArray(); Application.RequestStop(); };
        var skip = new Button(skipText);
        skip.Clicked += () => { decided = true; result = []; Application.RequestStop(); };
        var cancel = new Button("取消");
        cancel.Clicked += () => { decided = false; result = null; Application.RequestStop(); };
        AddButton(skip); AddButton(apply); AddButton(cancel);
    }
    public static string[]? Choose(string title, string message, IEnumerable<(string Path, bool Checked, bool Disabled)> items, string applyText, string skipText)
    {
        var dialog = new CfgSyncDialog(title, message, items, applyText, skipText);
        Application.Run(dialog);
        var outcome = dialog.decided ? dialog.result : null;
        dialog.Dispose();
        return outcome;
    }
}
