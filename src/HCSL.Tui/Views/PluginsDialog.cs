using HCSL.Core;
using HCSL.Frontend;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

public sealed class PluginsDialog : Dialog
{
    private readonly PresetPluginClient client;
    private readonly InstanceClient instances;
    private readonly InstanceDefinition instance;
    private readonly PresetDefinition preset;
    private readonly bool english;
    private readonly PluginDraft draft = new();
    private readonly ListView list = new() { X = 0, Y = 4, Width = Dim.Fill(), Height = Dim.Fill(5) };
    private readonly Label status = new() { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 2 };
    private readonly List<Button> actions = [];
    private bool busy, closed;
    public PluginsDialog(PresetPluginClient client, InstanceClient instances, InstanceDefinition instance, PresetDefinition preset, bool english = false)
    {
        this.client = client; this.instances = instances; this.instance = instance; this.preset = preset; this.english = english;
        Title = T("插件", "Plugins") + " | " + instance.Name + " / " + preset.Name;
        X = 1; Y = 1; Width = Dim.Fill(1); Height = Dim.Fill(1);
        Add(new Label(T("顺序即启用顺序；包由 library/plugin-packages 提供，保存不会立即部署。", "Order is enable order. Packages live in library/plugin-packages; saving does not deploy.")) { X = 0, Y = 0, Width = Dim.Fill(), Height = 2 });
        Add(new Label(T("部署只复制到实例运行目录；Metamod/CSS 包必须自带 addons/gameinfo overlay。", "Deployment copies only into the instance runtime; Metamod/CSS packages must ship addons/gameinfo overlays.")) { X = 0, Y = 2, Width = Dim.Fill(), Height = 2 });
        Add(list, status);
        AddAction(T("添加", "Add"), Add);
        AddAction(T("移除", "Remove"), () => Edit(() => { draft.Remove(list.SelectedItem); Refresh(); }));
        AddAction(T("启用/禁用", "Toggle"), () => Edit(() => { draft.Toggle(list.SelectedItem); Refresh(list.SelectedItem); }));
        AddAction(T("上移", "Up"), () => Edit(() => Refresh(draft.Move(list.SelectedItem, -1))));
        AddAction(T("下移", "Down"), () => Edit(() => Refresh(draft.Move(list.SelectedItem, 1))));
        AddAction(T("保存", "Save"), Save);
        AddAction(T("重读", "Reload"), () => { if (Discard()) Reload(); });
        AddAction(T("关闭", "Close"), Close);
    }
    private string T(string zh, string en) => english ? en : zh;
    private void AddAction(string text, Action action) { var button = new Button(text); button.Clicked += action; actions.Add(button); AddButton(button); }
    public void Initialize() => Reload();
    private void Refresh(int index = 0)
    {
        list.SetSource(draft.Plugins.Select((p, i) => $"{i + 1}. [{(p.Enabled ? T("启用", "on") : T("禁用", "off"))}] {Name(p)} ({p.Id}) {p.Version}").ToList());
        if (draft.Plugins.Count > 0) list.SelectedItem = Math.Clamp(index, 0, draft.Plugins.Count - 1);
    }
    private string Name(PluginSelection selection) => draft.Loaded?.Selected.FirstOrDefault(x => x.Id == selection.Id && x.Version == selection.Version)?.Name ?? selection.Id;
    private void Edit(Action action)
    {
        if (busy) return;
        if (!client.CanWrite) { status.Text = T("活动会话只读；请先停服并完成恢复。", "Read-only while active; stop and finish recovery first."); return; }
        try { action(); status.Text = T("草稿未保存。", "Unsaved draft."); }
        catch (InstanceException e) { status.Text = e.Code + ": " + e.Message; }
    }
    private void Add()
    {
        if (busy) return;
        if (!client.CanWrite) { status.Text = T("活动会话只读；请先停服并完成恢复。", "Read-only while active; stop and finish recovery first."); return; }
        var available = draft.Loaded?.Available.Where(x => draft.Plugins.All(p => p.Id != x.Id)).ToArray() ?? [];
        if (available.Length == 0) { status.Text = T("没有可添加的插件包。", "No package available."); return; }
        var chosen = Choose(available); if (chosen is null) return;
        try { draft.Add(chosen.Id, chosen.Version); Refresh(draft.Plugins.Count - 1); status.Text = T("草稿未保存。", "Unsaved draft."); }
        catch (InstanceException e) { status.Text = e.Code + ": " + e.Message; }
    }
    private PluginPackageInfo? Choose(PluginPackageInfo[] available)
    {
        var dialog = new Dialog(T("添加插件", "Add plugin"), 60, 16);
        var view = new ListView { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(3) };
        view.SetSource(available.Select(x => $"{x.Name} ({x.Id}) {x.Version}").ToList());
        dialog.Add(view);
        var ok = new Button(T("添加", "Add")); var cancel = new Button(T("取消", "Cancel"));
        ok.Clicked += () => { if (view.SelectedItem >= 0 && view.SelectedItem < available.Length) Application.RequestStop(); };
        cancel.Clicked += () => { view.SelectedItem = -1; Application.RequestStop(); };
        dialog.AddButton(ok); dialog.AddButton(cancel); dialog.Ready += () => view.SetFocus();
        Application.Run(dialog); var index = view.SelectedItem; dialog.Dispose();
        return index >= 0 && index < available.Length ? available[index] : null;
    }
    private bool Discard() => !draft.Dirty || MessageBox.Query(74, 10, T("未保存插件", "Unsaved plugins"), T("丢弃草稿？", "Discard draft?"), T("取消", "Cancel"), T("丢弃", "Discard")) == 1;
    private void Close() { if (!busy && Discard()) { closed = true; Application.RequestStop(); } }
    private void Reload() => Run(new(PluginAction.List, instance.Id, preset.Id));
    private void Save()
    {
        if (busy) return;
        PluginCommand command;
        try { command = draft.SaveCommand(instance.Id, preset.Id, instance.Revision); }
        catch (IOException e) { status.Text = e.Message; return; }
        var before = draft.Loaded!.Selected.Where(s => s.Enabled)
            .Select(s => new PluginSelection(s.Id, s.Version, true)).ToArray();
        var after = draft.Plugins.Where(p => p.Enabled).ToArray();
        var enabledNew = after.Where(a => !before.Any(b => b.Id == a.Id && b.Version == a.Version)).ToArray();
        var disabled = before.Where(b => !after.Any(a => a.Id == b.Id && a.Version == b.Version)).ToArray();
        RunTask(async () =>
        {
            PluginCfgPreview? preview = null;
            string[]? overwrite = [];
            if (enabledNew.Length > 0)
            {
                preview = await instances.PluginCfgPreviewAsync(new(PluginCfgAction.Preview, instance.Id, preset.Id, instance.Revision, enabledNew, disabled));
                if (preview.Import.Length > 0 || preview.Conflicts.Length > 0)
                {
                    var items = preview.Conflicts.Select(c => (c.Path, false, false))
                        .Concat(preview.Import.Select(c => (c.Path, false, true)));
                    overwrite = CfgSyncDialog.Choose(T("插件 cfg 同步", "Plugin cfg sync"),
                        T($"将导入 {preview.Import.Length} 个新文件；冲突 {preview.Conflicts.Length} 个。勾选=覆盖现有文件。", $"Import {preview.Import.Length} new; {preview.Conflicts.Length} conflicts. Check to overwrite."),
                        items, T("应用", "Apply"), T("仅保存清单", "Save manifest only"));
                    if (overwrite is null) return () => status.Text = T("已取消保存。", "Save cancelled.");
                }
            }
            var result = await client.ExecuteAsync(command);
            var text = T("已读取/保存；重新部署后生效。", "Loaded/saved. Deploy again to apply.");
            if (overwrite is { Length: > 0 } && preview is not null)
            {
                var applied = await instances.PluginCfgApplyAsync(new(PluginCfgAction.Apply, instance.Id, preset.Id, instance.Revision, enabledNew, null, overwrite));
                text += $" cfg: {applied.Imported}+{applied.Overwritten}";
            }
            if (disabled.Length > 0)
            {
                var cleanup = await instances.PluginCfgPreviewAsync(new(PluginCfgAction.CleanupPreview, instance.Id, preset.Id, instance.Revision, null, disabled));
                if (cleanup.Removable.Length > 0 || cleanup.Modified.Length > 0)
                {
                    var items = cleanup.Removable.Select(c => (c.Path, true, false))
                        .Concat(cleanup.Modified.Select(c => (c.Path, false, true)));
                    var delete = CfgSyncDialog.Choose(T("禁用插件 cfg 清理", "Disabled plugin cfg cleanup"),
                        T("默认勾选删除未修改的导入文件；已修改的文件保留。", "Checked files are unmodified imports; modified files are kept."),
                        items, T("删除选中", "Delete selected"), T("跳过", "Skip"));
                    if (delete is { Length: > 0 })
                    {
                        var removed = await instances.PluginCfgApplyAsync(new(PluginCfgAction.CleanupApply, instance.Id, preset.Id, instance.Revision, null, disabled, null, delete));
                        text += $" cfg: -{removed.Deleted}";
                    }
                }
            }
            return () => { draft.Load(result); Refresh(); status.Text = text; };
        });
    }

    private void RunTask(Func<Task<Action>> operation)
    {
        if (busy) return;
        busy = true; list.Enabled = false; foreach (var button in actions) button.Enabled = false;
        _ = RunAsync();
        async Task RunAsync()
        {
            Action apply;
            try { apply = await operation(); }
            catch (Exception e) { var message = e.Message; apply = () => status.Text = message; }
            Application.MainLoop.Invoke(() =>
            {
                if (closed) return;
                busy = false; list.Enabled = true; foreach (var button in actions) button.Enabled = true;
                try { apply(); } catch (Exception e) { status.Text = e.Message; }
            });
        }
    }
    private async void Run(PluginCommand command)
    {
        if (busy) return;
        busy = true; list.Enabled = false; foreach (var button in actions) button.Enabled = false;
        try
        {
            var result = await client.ExecuteAsync(command);
            Application.MainLoop.Invoke(() =>
            {
                if (closed) return;
                draft.Load(result); Refresh();
                var rejected = result.Rejected is { Length: > 0 } items
                    ? " " + T("被拒绝的包 ", "Rejected ") + items.Length + ": " + string.Join("; ", items.Take(3).Select(r => $"{r.Id}/{r.Version} {r.Code}"))
                    : "";
                status.Text = T("已读取/保存；重新部署后生效。", "Loaded/saved. Deploy again to apply.") + rejected;
            });
        }
        catch (Exception e) when (e is IOException or InstanceException)
        { Application.MainLoop.Invoke(() => { if (!closed) status.Text = e.Message; }); }
        finally
        { Application.MainLoop.Invoke(() => { if (closed) return; busy = false; list.Enabled = true; foreach (var button in actions) button.Enabled = true; }); }
    }
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (keyEvent.Key == Key.Esc) { Close(); return true; }
        return base.ProcessKey(keyEvent);
    }
    protected override void Dispose(bool disposing) { closed = true; base.Dispose(disposing); }
}
