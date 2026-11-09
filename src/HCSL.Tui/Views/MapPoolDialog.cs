using HCSL.Core;
using HCSL.Frontend;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

public sealed class MapPoolDialog : Dialog
{
    private readonly PresetMapPoolClient client;
    private readonly InstanceDefinition instance;
    private readonly PresetDefinition preset;
    private readonly bool english;
    private readonly MapPoolDraft draft = new();
    private readonly ListView list = new() { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(5) };
    private readonly TextField entry = new("standard de_dust2") { X = 0, Y = Pos.AnchorEnd(5), Width = Dim.Fill() };
    private readonly Label status = new() { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 2 };
    private readonly List<Button> actions = [];
    private bool busy, closed;
    public MapPoolDialog(PresetMapPoolClient client, InstanceDefinition instance, PresetDefinition preset, bool english = false)
    {
        this.client = client; this.instance = instance; this.preset = preset; this.english = english;
        Title = T("地图池", "Map pool") + " | " + instance.Name + " / " + preset.Name;
        X = 1; Y = 1; Width = Dim.Fill(1); Height = Dim.Fill(1);
        Add(new Label(T("条目：standard 地图名 / workshop 单图ID / collection 合集ID；合集必须单独占池。", "Entry: standard map / workshop itemId / collection collectionId; a collection must be the only entry.")) { X = 0, Y = 0, Width = Dim.Fill() });
        Add(new Label(NativeMapRotation.Status(english)) { X = 0, Y = 1, Width = Dim.Fill(), Height = 2 });
        Add(list, entry, status);
        AddAction(T("添加", "Add"), () => Edit(() => { draft.Add(MapPoolRules.Parse(entry.Text.ToString() ?? "")); Refresh(draft.Maps.Count - 1); }));
        AddAction(T("删除", "Delete"), () => Edit(() => { var i = list.SelectedItem; draft.Remove(i); Refresh(i); }));
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
        list.SetSource(draft.Maps.Select((m, i) => $"{i + 1}. {ServerMapRules.Label(m)}").ToList());
        if (draft.Maps.Count > 0) list.SelectedItem = Math.Clamp(index, 0, draft.Maps.Count - 1);
    }
    private void Edit(Action action)
    {
        if (busy) return;
        if (!client.CanWrite) { status.Text = T("活动会话只读；请先停服并完成恢复。", "Read-only while active; stop and finish recovery first."); return; }
        try { action(); status.Text = T("草稿未保存。", "Unsaved draft."); }
        catch (InstanceException e) { status.Text = e.Code + ": " + e.Message; }
    }
    private bool Discard() => !draft.Dirty || MessageBox.Query(74, 10, T("未保存地图池", "Unsaved map pool"), T("丢弃草稿？", "Discard draft?"), T("取消", "Cancel"), T("丢弃", "Discard")) == 1;
    private void Close() { if (!busy && Discard()) { closed = true; Application.RequestStop(); } }
    private void Reload() => Run(new(MapPoolAction.Read, instance.Id, preset.Id));
    private void Save()
    {
        if (busy) return;
        try { Run(draft.SaveCommand(instance.Id, preset.Id, instance.Revision)); }
        catch (IOException e) { status.Text = e.Message; }
    }
    private async void Run(MapPoolCommand command)
    {
        if (busy) return;
        busy = true; entry.Enabled = list.Enabled = false; foreach (var button in actions) button.Enabled = false;
        try
        {
            var result = await client.ExecuteAsync(command);
            Application.MainLoop.Invoke(() => { if (closed) return; draft.Load(result); Refresh(); status.Text = T("已读取/保存；未换图。全普通池生成地图组，单合集池由合集原生轮换；混排仍需手工换图。", "Loaded/saved. An all-standard pool generates a map group; a single collection rotates natively; mixed pools stay manual."); });
        }
        catch (Exception e) when (e is IOException or InstanceException)
        { Application.MainLoop.Invoke(() => { if (!closed) status.Text = e.Message; }); }
        finally
        { Application.MainLoop.Invoke(() => { if (closed) return; busy = false; entry.Enabled = list.Enabled = true; foreach (var button in actions) button.Enabled = true; }); }
    }
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        if (keyEvent.Key == Key.Esc) { Close(); return true; }
        return base.ProcessKey(keyEvent);
    }
    protected override void Dispose(bool disposing) { closed = true; base.Dispose(disposing); }
}
