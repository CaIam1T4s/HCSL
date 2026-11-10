using HCSL.Core;
using HCSL.Frontend;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

public sealed class PluginsDialog : Dialog
{
    private readonly PresetPluginClient client;
    private readonly InstanceDefinition instance;
    private readonly PresetDefinition preset;
    private readonly bool english;
    private readonly PluginDraft draft = new();
    private readonly ListView list = new() { X = 0, Y = 4, Width = Dim.Fill(), Height = Dim.Fill(5) };
    private readonly Label status = new() { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 2 };
    private readonly List<Button> actions = [];
    private bool busy, closed;
    public PluginsDialog(PresetPluginClient client, InstanceDefinition instance, PresetDefinition preset, bool english = false)
    {
        this.client = client; this.instance = instance; this.preset = preset; this.english = english;
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
        try { Run(draft.SaveCommand(instance.Id, preset.Id, instance.Revision)); }
        catch (IOException e) { status.Text = e.Message; }
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
