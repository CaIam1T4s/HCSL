using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views.Controls;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

/// <summary>Presentation only. All file access and exec checks use the shared Frontend client.</summary>
public sealed class CfgEditorDialog : Dialog
{
    private readonly PresetCfgClient client;
    private readonly ServerController server;
    private readonly InstanceDefinition instance;
    private readonly PresetDefinition preset;
    private readonly bool english;
    private readonly ListView list = new() { X = 0, Y = 2, Width = 24, Height = Dim.Fill(4) };
    private readonly VimCfgTextView editor = new() { X = 25, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(4) };
    private readonly Label mode = new() { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill() };
    private readonly Label status = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
    private readonly TextField command = new("") { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Visible = false };
    private PresetCfgFile[] files = [];
    private string? name, hash;
    private string baseline = "";
    private bool busy, closed;
    public VimCfgTextView Editor => editor;
    public string? CurrentName => name;
    public bool Dirty => Body != baseline;
    private string Body => editor.Text.ToString() ?? "";
    private string T(string zh, string en) => english ? en : zh;
    public CfgEditorDialog(PresetCfgClient client, ServerController server, InstanceDefinition instance, PresetDefinition preset, bool english = false)
    {
        this.client = client; this.server = server; this.instance = instance; this.preset = preset; this.english = english;
        Title = "CFG | " + instance.Name + " / " + preset.Name;
        X = 1; Y = 1; Width = Dim.Fill(1); Height = Dim.Fill(1);
        Add(new Label(T("选文件后 Enter 读取；F6 文件列表 / F2 命令栏；:help 帮助。CFG 是明文。", "Enter reads selected file; F6 list / F2 command; :help. CFG is plaintext.")) { X = 0, Y = 0, Width = Dim.Fill() });
        var help = new Label("i/a/o INSERT | Esc NORMAL | hjkl gg/G 0/$ x dd u Ctrl-R | :w :q :wq :new name.cfg :exec")
        { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill() };
        Add(list, editor, mode, help, command, status);
        editor.ModeChanged += UpdateMode;
        editor.TextChanged += UpdateMode;
        editor.CommandRequested += OpenCommand;
        editor.ShortcutRequested += key =>
        {
            if (key == Key.F2) OpenCommand();
            else if (key == Key.F6 && !busy) { editor.Normal(); list.SetFocus(); }
            else if (key == Key.F10 || key == (Key.CtrlMask | Key.Q)) RequestClose(false);
        };
        list.OpenSelectedItem += _ => ReadSelected();
        command.KeyPress += e =>
        {
            if (e.KeyEvent.Key == Key.Esc) { EndCommand(); e.Handled = true; }
            else if (e.KeyEvent.Key == Key.Enter)
            {
                var text = command.Text.ToString() ?? ""; EndCommand(); Execute(text); e.Handled = true;
            }
        };
        KeyPress += e =>
        {
            if (e.KeyEvent.Key == Key.F2) { OpenCommand(); e.Handled = true; }
            else if (e.KeyEvent.Key == Key.F6) { if (!busy) { editor.Normal(); list.SetFocus(); } e.Handled = true; }
            else if (e.KeyEvent.Key == Key.Esc) { EndCommand(); e.Handled = true; }
            else if (e.KeyEvent.Key == Key.F10 || e.KeyEvent.Key == (Key.CtrlMask | Key.Q)) { RequestClose(false); e.Handled = true; }
        };
        UpdateMode();
    }
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        var key = keyEvent.Key;
        // Dialog handles Escape before child TextView; intercept it here as a mode change, not close.
        if (key == Key.Esc) { EndCommand(); return true; }
        if (key == Key.F2) { OpenCommand(); return true; }
        if (key == Key.F6) { if (!busy) { editor.Normal(); list.SetFocus(); } return true; }
        if (key == Key.F10 || key == (Key.CtrlMask | Key.Q)) { RequestClose(false); return true; }
        if (key == Key.Enter && command.HasFocus)
        { var text = command.Text.ToString() ?? ""; EndCommand(); Execute(text); return true; }
        return base.ProcessKey(keyEvent);
    }
    protected override void Dispose(bool disposing)
    {
        closed = true;
        if (disposing) { baseline = ""; editor.LoadDocument(""); command.Text = ""; }
        base.Dispose(disposing);
    }
    public void Initialize() => RefreshFiles();
    private void UpdateMode() => mode.Text = (editor.Inserting ? "-- INSERT -- " : "-- NORMAL -- ") + (name ?? "[new]") + (Dirty ? " [+]" : "") + (busy ? " [IPC…]" : "");
    private PresetCfgCommand C(PresetCfgAction action, string? target = null, string? content = null, string? newName = null) =>
        new(action, instance.Id, preset.Id, instance.Revision, target ?? name, content, hash, newName);
    private bool Confirm(string text) => MessageBox.Query(76, 12, "CFG", text, T("取消", "Cancel"), T("确认", "Confirm")) == 1;
    private bool Discard() => !Dirty || Confirm(T("有未保存草稿，丢弃？", "Discard unsaved draft?"));
    private void SetFiles(PresetCfgFile[] value)
    {
        files = value;
        list.SetSource(files.Select(f => f.Name + " (" + f.Bytes + ")").ToList());
    }
    private void OpenCommand()
    {
        if (busy) return;
        editor.Normal(); command.Text = ":"; command.Visible = true; command.SetFocus(); command.CursorPosition = 1;
    }
    private void EndCommand() { command.Visible = false; command.Text = ""; editor.Normal(); editor.SetFocus(); }
    private void Run(Func<Task<Action>> operation)
    {
        if (busy || closed) return;
        busy = true; editor.Enabled = list.Enabled = command.Enabled = false; UpdateMode();
        _ = RunAsync();
        async Task RunAsync()
        {
            Action apply;
            try { apply = await operation(); }
            catch (Exception e) { var message = e.Message; apply = () => status.Text = message; }
            Application.MainLoop?.Invoke(() =>
            {
                if (closed) return;
                busy = false; editor.Enabled = list.Enabled = command.Enabled = true;
                try { apply(); } catch (Exception e) { status.Text = e.Message; }
                UpdateMode(); if (!closed) editor.SetFocus();
            });
        }
    }
    private void RefreshFiles() => Run(async () =>
    {
        var result = await client.ExecuteAsync(C(PresetCfgAction.List));
        return () => { SetFiles(result.Files); status.Text = T("列表已更新；不会覆盖草稿。", "List refreshed; draft unchanged."); };
    });
    private void ReadSelected()
    {
        if (busy || list.SelectedItem < 0 || list.SelectedItem >= files.Length || !Discard()) return;
        Read(files[list.SelectedItem].Name);
    }
    private void Read(string file) => Run(async () =>
    {
        var result = await client.ExecuteAsync(C(PresetCfgAction.Read, file));
        return () =>
        {
            name = result.Name; hash = result.Hash; baseline = result.Content ?? ""; editor.LoadDocument(baseline);
            status.Text = T("已读取；运行中只读，保存需停服。", "Loaded; stop before saving.");
        };
    });
    public void Execute(string input)
    {
        if (busy || closed) return;
        var parts = input.Trim().TrimStart(':').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var arg = parts.Length == 2 ? parts[1].Trim() : "";
        if (arg.Length != 0 && parts[0] is "q" or "q!" or "wq" or "ls" or "exec" or "delete" or "help")
        { status.Text = T("此命令不接受参数。", "This command takes no argument."); return; }
        switch (parts[0])
        {
            case "help":
                MessageBox.Query(88, 19, "CFG / Vim subset", T(
                    "普通：hjkl、0/$、gg/G、x、dd、u、Ctrl-R。i/a/I/A/o/O 插入，Esc 返回。\n:w [文件名] 保存（另存不覆盖）；:q 询问草稿；:q! 丢弃；:wq 保存后关闭。\n:new [文件名] 新建；:e [文件名] 读取；:ls 刷新；:rename 新名；:delete 回收删除。\n:exec 对已保存文件确认执行；保存不执行。不支持完整 Vim/搜索/寄存器/计数/管道。\nF6 文件列表，F2 命令栏。失败保留草稿，运行/恢复中拒绝写入。",
                    "NORMAL: hjkl, 0/$, gg/G, x, dd, u, Ctrl-R. i/a/I/A/o/O insert; Esc returns.\n:w [name] save (save-as never overwrites); :q asks; :q! discards; :wq saves then closes.\n:new [name], :e [name], :ls, :rename name, :delete (trash).\n:exec confirms the saved file; saving does not execute. Not full Vim; no search/registers/counts/pipes.\nF6 file list, F2 command. Errors preserve draft; stop/recover before writes."), "OK"); break;
            case "ls": RefreshFiles(); break;
            case "e": if (arg.Length != 0) { if (Discard()) Read(arg); } else ReadSelected(); break;
            case "new":
                if (Discard()) { name = arg.Length == 0 ? null : arg; hash = null; baseline = ""; editor.LoadDocument(""); status.Text = T("新建；:w 文件名.cfg 保存。", "New; :w filename.cfg saves."); }
                break;
            case "w": Save(arg, false); break;
            case "wq": if (arg.Length == 0) Save("", true); else status.Text = T(":wq 不接受参数。", ":wq takes no argument."); break;
            case "q": RequestClose(false); break;
            case "q!": RequestClose(true); break;
            case "rename": Rename(arg); break;
            case "delete": Delete(); break;
            case "exec": Exec(); break;
            default: status.Text = T("未知命令；:help 查看支持范围（不执行 shell）。", "Unknown command; :help lists supported commands (no shell)."); break;
        }
        UpdateMode();
    }
    private void Save(string argument, bool close)
    {
        var target = argument.Length == 0 ? name : argument;
        if (string.IsNullOrWhiteSpace(target)) { status.Text = T("请输入 :w 文件名.cfg", "Use :w filename.cfg"); return; }
        var body = Body;
        var expected = target == name ? hash : null;
        var request = new PresetCfgCommand(PresetCfgAction.Save, instance.Id, preset.Id, instance.Revision, target, body, expected);
        Run(async () =>
        {
            var result = await client.ExecuteAsync(request);
            return () =>
            {
                name = result.Name; hash = result.Hash; baseline = body;
                status.Text = T("已保存，未执行。", "Saved, not executed.");
                if (close) RequestClose(true); else RefreshFiles();
            };
        });
    }
    private void Rename(string target)
    {
        if (name is null || hash is null || target.Length == 0) { status.Text = T("先读取文件，再 :rename 新名.cfg", "Read a file, then :rename new.cfg"); return; }
        if (!Confirm(T("重命名，不保存文本草稿，不修改其他 CFG 的引用：", "Rename without saving draft or updating other CFG references: ") + name + " → " + target)) return;
        var request = C(PresetCfgAction.Rename, newName: target);
        Run(async () => { await client.ExecuteAsync(request); return () => { name = target; status.Text = T("已重命名；草稿保留。", "Renamed; draft retained."); RefreshFiles(); }; });
    }
    private void Delete()
    {
        if (name is null || hash is null) { status.Text = T("先读取已保存文件。", "Read a saved file first."); return; }
        if (!Confirm(T("回收删除文件及丢弃当前草稿？", "Move file to trash and discard current draft? ") + "\n" + name)) return;
        var request = C(PresetCfgAction.Delete);
        Run(async () => { await client.ExecuteAsync(request); return () => { name = hash = null; baseline = ""; editor.LoadDocument(""); status.Text = T("已移到 .cfg-trash。", "Moved to .cfg-trash."); RefreshFiles(); }; });
    }
    private void Exec()
    {
        if (name is null || hash is null || Dirty || !client.CanExec(instance.Id, preset.Id) || server.RunId is not { } run)
        { status.Text = T("先保存/读取文件，并确认当前运行实例与预设匹配。", "Save/read first; the running instance and preset must match."); return; }
        var file = name; var version = hash;
        if (!Confirm(instance.Name + " / " + preset.Name + "\nInstanceId " + instance.Id + "\nPresetId " + preset.Id + "\nRunId " + run + "\nexec " + file + T("\n可能改变游戏状态；不代表执行成功。", "\nMay change game state; delivery is not execution."))) return;
        Run(async () => { var result = await client.ExecAsync(instance.Id, preset.Id, run, file, version); return () => status.Text = result; });
    }
    private void RequestClose(bool force)
    {
        if (busy) { status.Text = T("等待当前 IPC 操作完成。", "Wait for the current IPC operation."); return; }
        if (!force && !Discard()) return;
        closed = true; name = hash = null; baseline = ""; editor.LoadDocument(""); command.Text = "";
        Application.RequestStop();
    }
}
