using HCSL.Core;
using HCSL.Frontend;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

/// <summary>Read-only CFG tree. Navigation and exec only; file writes stay outside HCSL.</summary>
public sealed class CfgBrowserDialog : Dialog
{
    private readonly PresetCfgClient client;
    private readonly ServerController server;
    private readonly InstanceDefinition instance;
    private readonly PresetDefinition preset;
    private readonly bool english;
    private readonly ListView list = new() { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(5) };
    private readonly Label path = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = 2 };
    private readonly Label status = new() { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
    private readonly Label help = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private PresetCfgFile[] entries = [];
    private string current = "";
    private bool busy, closed;
    public string CurrentPath => current;
    public PresetCfgFile[] Entries => entries;
    public ListView List => list;
    public void NavigateUp() => Up();
    private string T(string zh, string en) => english ? en : zh;
    public CfgBrowserDialog(PresetCfgClient client, ServerController server, InstanceDefinition instance, PresetDefinition preset, bool english = false)
    {
        this.client = client; this.server = server; this.instance = instance; this.preset = preset; this.english = english;
        Title = "CFG | " + instance.Name + " / " + preset.Name;
        X = 1; Y = 1; Width = Dim.Fill(1); Height = Dim.Fill(1);
        Add(new Label(T("只读浏览：目录与 .cfg。E/Enter 执行选中 cfg；Backspace 返回上级。", "Read-only: directories and .cfg. E/Enter execs a cfg; Backspace goes up."))
        { X = 0, Y = 0, Width = Dim.Fill() });
        help.Text = T("Enter 进入目录 | Backspace 上级 | E 执行 | R 刷新 | Esc 关闭", "Enter dir | Backspace up | E exec | R refresh | Esc close");
        Add(path, list, status, help);
        list.OpenSelectedItem += _ => OpenSelected();
        KeyPress += e =>
        {
            if (e.KeyEvent.Key == Key.Esc) { Close(); e.Handled = true; }
            else if (e.KeyEvent.Key is Key.Backspace or Key.CursorLeft) { Up(); e.Handled = true; }
            else if (e.KeyEvent.Key == Key.F5 || e.KeyEvent.Key == Key.R) { Refresh(); e.Handled = true; }
            else if (e.KeyEvent.Key == Key.E) { ExecSelected(); e.Handled = true; }
        };
    }
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (keyEvent.Key == Key.Esc) { Close(); return true; }
        if (keyEvent.Key is Key.Backspace or Key.CursorLeft) { Up(); return true; }
        if (keyEvent.Key == Key.F5 || keyEvent.Key == Key.R) { Refresh(); return true; }
        if (keyEvent.Key == Key.E) { ExecSelected(); return true; }
        return base.ProcessKey(keyEvent);
    }
    protected override void Dispose(bool disposing) { closed = true; base.Dispose(disposing); }
    public void Initialize() => Refresh();
    private void Close() { if (!busy) { closed = true; Application.RequestStop(); } }
    private PresetCfgCommand C(PresetCfgAction action, string? name = null) =>
        new(action, instance.Id, preset.Id, instance.Revision, name, Path: current);
    private void Run(Func<Task<Action>> operation)
    {
        if (busy || closed) return;
        busy = true; list.Enabled = false;
        _ = RunAsync();
        async Task RunAsync()
        {
            Action apply;
            try { apply = await operation(); }
            catch (Exception e) { var message = e.Message; apply = () => status.Text = message; }
            Application.MainLoop?.Invoke(() =>
            {
                if (closed) return;
                busy = false; list.Enabled = true;
                try { apply(); } catch (Exception e) { status.Text = e.Message; }
                if (!closed) list.SetFocus();
            });
        }
    }
    private void Refresh() => Run(async () =>
    {
        var result = await client.ExecuteAsync(C(PresetCfgAction.List));
        return () =>
        {
            entries = result.Files; current = result.Path ?? "";
            path.Text = current.Length == 0 ? T("根目录", "Root") : "cfg/" + current;
            list.SetSource(entries.Select(e => e.Kind == PresetCfgKind.Directory ? "[DIR] " + e.Name : e.Name + " (" + e.Bytes + ")").ToList());
            status.Text = result.Truncated
                ? T("列表被截断：", "Truncated: ") + result.Total
                : T("目录 ", "Dirs ") + entries.Count(e => e.Kind == PresetCfgKind.Directory) + " / .cfg " + entries.Count(e => e.Kind == PresetCfgKind.File);
        };
    });
    private void OpenSelected()
    {
        if (busy || list.SelectedItem < 0 || list.SelectedItem >= entries.Length) { Refresh(); return; }
        var selected = entries[list.SelectedItem];
        if (selected.Kind == PresetCfgKind.Directory) { current = selected.RelativePath ?? selected.Name; Refresh(); }
        else status.Text = T("只读；E 执行该 cfg。", "Read-only; E execs this cfg.");
    }
    private void Up()
    {
        if (busy || current.Length == 0) return;
        current = current.Contains('/') ? current[..current.LastIndexOf('/')] : "";
        Refresh();
    }
    private void ExecSelected()
    {
        if (busy || list.SelectedItem < 0 || list.SelectedItem >= entries.Length) return;
        var selected = entries[list.SelectedItem];
        if (selected.Kind != PresetCfgKind.File) { status.Text = T("先选择一个 .cfg。", "Select a .cfg first."); return; }
        var target = selected.RelativePath ?? selected.Name;
        if (!client.CanExec(instance.Id, preset.Id) || server.RunId is not { } run)
        { status.Text = T("运行中的实例/预设必须与此页面一致。", "Running instance/preset must match."); return; }
        Run(async () =>
        {
            var read = await client.ExecuteAsync(C(PresetCfgAction.Read, target));
            var confirm = MessageBox.Query(78, 12, "exec", instance.Name + " / " + preset.Name + "\nRunId " + run + "\nexec " + target
                + T("\n可能改变游戏状态；不代表执行成功。", "\nMay change game state; delivery is not execution."), T("取消", "Cancel"), T("执行", "Exec"));
            if (confirm != 1) return () => status.Text = T("已取消。", "Cancelled.");
            var result = await client.ExecAsync(instance.Id, preset.Id, run, target, read.Hash ?? "");
            return () => status.Text = result.Message;
        });
    }
}
