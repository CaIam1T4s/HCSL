using HCSL.Core;
using HCSL.Frontend;
using Terminal.Gui;
namespace HCSL.Tui.Views;

public sealed partial class ShellWindow
{
    private readonly InstanceClient _instanceClient = new(new DaemonClient());
    private InstanceEntry[] _instanceRows = [];
    private Guid? _selectedInstanceId;
    private bool _instanceBusy;
    private InstanceEntry? SelectedRow => _instances.SelectedItem >= 0 && _instances.SelectedItem < _instanceRows.Length ? _instanceRows[_instances.SelectedItem] : null;
    private InstanceEntry? CurrentInstance => _instanceRows.FirstOrDefault(e => e.Id == _selectedInstanceId && e.Definition != null);

    private async Task RefreshInstancesAsync(InstanceCommand? command = null)
    {
        if (_instanceBusy) return;
        _instanceBusy = true;
        try
        {
            var catalog = await _instanceClient.ExecuteAsync(command ?? new(InstanceAction.List));
            Application.MainLoop.Invoke(() =>
            {
                var row = SelectedRow?.Id;
                _instanceRows = catalog.Instances;
                _instances.SetSource(_instanceRows.Select(e => e.Definition is { } d ? $"{d.Name}  :{d.GamePort}" : $"{e.Id} [{e.Error}]").ToList());
                // The running instance wins over a stale or empty selection so a reopened UI can name it.
                var running = _server.LiveInstanceId;
                if ((row is not { } keep || _instanceRows.All(e => e.Id != keep)) &&
                    running is { } live && _instanceRows.Any(e => e.Id == live)) row = live;
                _selectedInstanceId = row;
                var index = Array.FindIndex(_instanceRows, e => e.Id == row);
                if (_instanceRows.Length > 0) _instances.SelectedItem = Math.Max(0, index);
                UpdateInstanceSummary();
            });
        }
        catch (Exception e)
        {
            // A background refresh must not steal focus with a modal; explicit commands still report.
            Application.MainLoop.Invoke(() =>
            {
                if (command is not null) MessageBox.ErrorQuery(70, 10, T("实例操作失败", "Instance operation failed"), Describe(e), T("返回", "Back"));
                else _status.Text = T("实例列表读取失败：", "Instance list unavailable: ") + Describe(e);
            });
        }
        finally
        {
            Application.MainLoop.Invoke(() => { _instanceBusy = false; UpdateInstanceActions(); });
        }
    }
    private void UpdateInstanceSummary()
    {
        RefreshHostTitle(); UpdateInstanceActions();
        AttachSelectedInstance();
        var entry = CurrentInstance;
        if (entry?.Definition is not { } d)
        {
            if (_pageId == "presets") _content.Text = T("请先在实例列表选择实例。", "Select an instance from the instance list first.");
            RefreshServerSummary();
            return;
        }
        _instanceButton.Text = T("前往实例列表", "Open instances");
        if (_pageId == "presets")
            _content.Text = string.Join("\n", entry.Presets.Select(p => (p.Id == d.SelectedPresetId ? "● " : "  ") + p.Name + " | " + p.Map)) + "\n\n" + T("操作时选择目标预设；切换只保存选择，不启动或部署服务器。", "Choose a preset when performing an action. Switching saves the selection; it does not deploy or start a server.");
        RefreshServerSummary();
    }
    private string Describe(Exception e) => e.Message.Contains("HCSL-LOCK", StringComparison.Ordinal)
        ? T("服务器运行中，实例修改受工作区锁保护；请先停服再修改。", "A server session holds the workspace lock; stop the server before changing instances.")
        : e.Message.Contains("HCSL-SERVER-RECOVERY", StringComparison.Ordinal)
            ? T("上次服务器会话未正常结束，请先完成恢复。", "The previous server session did not end cleanly; recover it first.")
            : e.Message;
    private string[]? Form(string title, params (string Label, string Value)[] fields)
    {
        var dialog = new Dialog(title, 68, 6 + fields.Length * 2);
        var inputs = new List<TextField>();
        for (var i = 0; i < fields.Length; i++)
        {
            dialog.Add(new Label(fields[i].Label) { X = 1, Y = i * 2 });
            var input = new TextField(fields[i].Value) { X = 20, Y = i * 2, Width = Dim.Fill(2) };
            inputs.Add(input); dialog.Add(input);
        }
        bool saved = false;
        var ok = new Button(T("保存", "Save")) { IsDefault = true };
        ok.Clicked += () => { saved = true; Application.RequestStop(); };
        var cancel = new Button(T("取消", "Cancel")); cancel.Clicked += () => Application.RequestStop();
        dialog.AddButton(ok); dialog.AddButton(cancel);
        Application.Run(dialog);
        var values = saved ? inputs.Select(i => i.Text.ToString() ?? "").ToArray() : null;
        dialog.Dispose(); return values;
    }
    private void InstanceActionClicked(int index)
    {
        if (_instanceBusy) return;
        var entry = SelectedRow; var d = entry?.Definition;
        if (index == 0 && d != null) { _selectedInstanceId = d.Id; UpdateInstanceSummary(); Navigate("presets"); return; }
        if (index is 1 or 3)
        {
            if (index == 3 && d == null) return;
            var values = Form(T("实例", "Instance"), (T("名称", "Name"), index == 1 ? "" : d!.Name), (T("游戏端口", "Game port"), index == 1 ? "27015" : d!.GamePort.ToString()));
            if (values == null) return;
            if (!int.TryParse(values[1], out var port)) { MessageBox.ErrorQuery("HCSL", T("端口必须是整数", "Port must be an integer"), "OK"); return; }
            _ = RefreshInstancesAsync(new(index == 1 ? InstanceAction.Create : InstanceAction.Update, d?.Id ?? default, Revision: d?.Revision ?? 0, Name: values[0], GamePort: port));
        }
        if (index == 4 && d != null && MessageBox.Query(68, 10, T("删除实例", "Delete instance"), T("将实例配置、录像和日志移入回收区。确定？", "Move instance configuration, recordings and logs to the recycle area?"), T("取消", "Cancel"), T("删除", "Delete")) == 1)
            _ = RefreshInstancesAsync(new(InstanceAction.Delete, d.Id, Revision: d.Revision));
    }
    private void PresetAction(int index)
    {
        if (_instanceBusy || CurrentInstance is not { Definition: { } d } entry) return;
        if (index == 0)
        {
            var values = Form(T("新建预设", "New preset"), (T("名称", "Name"), ""));
            if (values != null) _ = RefreshInstancesAsync(new(InstanceAction.CreatePreset, d.Id, Revision: d.Revision, Name: values[0]));
            return;
        }
        var dialog = new Dialog(T("选择预设", "Choose preset"), 64, 16);
        var list = new ListView(entry.Presets.Select(p => p.Name).ToList()) { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(3) };
        dialog.Add(list); bool chosen = false;
        var ok = new Button(T("选择", "Select")); ok.Clicked += () => { chosen = true; Application.RequestStop(); };
        var cancel = new Button(T("取消", "Cancel")); cancel.Clicked += () => Application.RequestStop();
        dialog.AddButton(ok); dialog.AddButton(cancel); Application.Run(dialog);
        var row = list.SelectedItem; dialog.Dispose();
        if (!chosen || row < 0 || row >= entry.Presets.Length) return;
        var p = entry.Presets[row];
        if (index is 1 or 2)
        {
            var values = index == 1
                ? Form(T("复制预设", "Copy preset"), (T("名称", "Name"), p.Name + " copy"))
                : Form(T("编辑预设", "Edit preset"), (T("名称", "Name"), p.Name), (T("地图", "Map"), p.Map), ("game_type", p.GameType.ToString()), ("game_mode", p.GameMode.ToString()), ("VAC (true/false)", p.VacPreferred.ToString()));
            if (values == null) return;
            if (index == 1) _ = RefreshInstancesAsync(new(InstanceAction.CopyPreset, d.Id, p.Id, d.Revision, values[0]));
            else
            {
                if (!int.TryParse(values[2], out var type) || !int.TryParse(values[3], out var mode) || !bool.TryParse(values[4], out var vac))
                { MessageBox.ErrorQuery("HCSL", T("模式需为整数，VAC 为 true/false。", "Modes must be integers; VAC must be true/false."), "OK"); return; }
                _ = RefreshInstancesAsync(new(InstanceAction.UpdatePreset, d.Id, p.Id, d.Revision, Preset: p with { Name = values[0], Map = values[1], GameType = type, GameMode = mode, VacPreferred = vac }));
            }
        }
        else if (index == 4) _ = RefreshInstancesAsync(new(InstanceAction.SelectPreset, d.Id, p.Id, d.Revision));
        else if (index == 3 && MessageBox.Query(T("删除预设", "Delete preset"), p.Name, T("取消", "Cancel"), T("删除", "Delete")) == 1)
            _ = RefreshInstancesAsync(new(InstanceAction.DeletePreset, d.Id, p.Id, d.Revision));
    }
}
