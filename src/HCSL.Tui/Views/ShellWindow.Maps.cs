using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views.Controls;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

public sealed partial class ShellWindow
{
    private readonly List<InstanceActionButton> _mapActions = [];
    private bool _mapBusy;
    private void InitializeMaps()
    {
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var button = new InstanceActionButton { X = 1 + i * 20, Y = Pos.AnchorEnd(2), Width = 19, AutoSize = false, Visible = false };
            button.Clicked += () => { _ = MapActionAsync(index); };
            button.PeerRequested += direction =>
            {
                for (var step = 1; step <= _mapActions.Count; step++)
                {
                    var peer = _mapActions[(index + direction * step + _mapActions.Count) % _mapActions.Count];
                    if (peer.Enabled && peer.CanFocus) { peer.SetFocus(); break; }
                }
            };
            _mapActions.Add(button); _page.Add(button);
        }
    }
    private void RefreshMapPage()
    {
        if (_mapActions.Count == 0) return;
        _mapActions[0].Text = T("保存启动地图", "Startup map"); _mapActions[1].Text = T("即时换图", "Change map"); _mapActions[2].Text = T("地图池", "Map pool");
        _mapActions[0].Enabled = CurrentInstance?.Definition is not null && _server.Connected && !_server.Active && _server.State != ServerState.RecoveryRequired && !_instanceBusy && !_mapBusy;
        _mapActions[1].Enabled = _server.CanSendConsole && !_mapBusy;
        _mapActions[2].Enabled = CurrentInstance?.Definition is not null && _server.Connected && !_mapBusy && !_instanceBusy;
        if (_pageId != "maps") return;
        var entry = CurrentInstance; var preset = entry?.Presets.FirstOrDefault(p => p.Id == entry.Definition?.SelectedPresetId);
        _content.Text = T("当前所选预设启动地图：", "Selected preset startup map: ") + (preset is null ? "—" : ServerMapRules.Label(new(preset.MapKind, preset.Map)))
            + "\n" + _server.SummaryLine + "\n" + _server.DetailText + "\n\n" + T("保存启动地图需先停服；即时换图针对运行实例，不修改预设。\n工坊单图/合集只填 ID，不接受网址。下载/加载以实际日志为准。\n不会自动禁用工坊命令保护；全普通池生成地图组，单合集池由合集原生轮换；混排仍需手工换图；无投票或工坊浏览器。", "Save startup settings while stopped. Live changes target the running instance and do not save the preset.\nWorkshop item/collection: ID only, not a URL. Watch logs for actual progress.\nWorkshop protection stays enabled. An all-standard pool generates a map group; a single collection rotates natively; mixed pools stay manual; no voting/browser.");
    }
    private ServerMapTarget? AskMapTarget(ServerMapTarget initial)
    {
        var values = new[] { initial.Kind == ServerMapKind.Workshop ? "workshop" : "standard", initial.Value };
        while (true)
        {
            var fields = Form(T("地图：standard 普通 / workshop 工坊", "Map: standard / workshop"), (T("类型", "Kind"), values[0]), (T("地图 / ID", "Map / ID"), values[1]));
            if (fields is null) return null;
            values = fields;
            var kind = fields[0].Trim().ToLowerInvariant() switch { "standard" or "普通" => ServerMapKind.Standard, "workshop" or "工坊" => ServerMapKind.Workshop, _ => (ServerMapKind)(-1) };
            var target = new ServerMapTarget(kind, fields[1].Trim());
            try { ServerMapRules.Validate(target); return target; }
            catch (InstanceException e) { MessageBox.ErrorQuery(78, 12, T("地图输入无效", "Invalid map target"), e.Code + ": " + e.Message, T("返回修改", "Edit")); }
        }
    }
    private async Task MapActionAsync(int action)
    {
        if (_mapBusy) return;
        _mapBusy = true; RefreshMapPage();
        try
        {
            if (action == 0)
            {
                if (CurrentInstance is not { Definition: { } d } entry) return;
                var p = entry.Presets.First(x => x.Id == d.SelectedPresetId);
                var target = AskMapTarget(new(p.MapKind, p.Map)); if (target is null) return;
                if (MessageBox.Query(76, 12, T("保存启动地图", "Save startup map"), $"{d.Name} / {p.Name}\n{ServerMapRules.Label(target)}\n" + T("仅下次启动使用；不立即换图。", "Next start only, no live change."), T("取消", "Cancel"), T("保存", "Save")) != 1) return;
                await RefreshInstancesAsync(new(InstanceAction.UpdatePreset, d.Id, p.Id, d.Revision, Preset: p with { Map = target.Value, MapKind = target.Kind }));
            }
            else if (action == 2)
            {
                if (CurrentInstance is not { Definition: { } d } entry) return;
                var preset = entry.Presets.First(p => p.Id == d.SelectedPresetId);
                using var dialog = new MapPoolDialog(new(_instanceClient, _server), d, preset, _options.English);
                dialog.Ready += dialog.Initialize;
                Application.Run(dialog);
            }
            else
            {
                if (_server.Snapshot is not { RunId: { } run, InstanceId: { } id }) return;
                var target = AskMapTarget(new(ServerMapKind.Standard, "de_dust2")); if (target is null) return;
                if (MessageBox.Query(80, 15, T("确认即时换图", "Confirm live map change"), $"Instance: {id}\nRunId: {run}\n{ServerMapRules.ChangeCommand(target)}\n" + T("影响当前玩家，不保存预设；投递不代表加载成功。", "Affects current players, does not save preset. Delivery is not readiness."), T("取消", "Cancel"), T("换图", "Change")) != 1) return;
                var receipt = await new ServerMapClient(_server).ChangeAsync(id, run, target);
                Application.MainLoop.Invoke(() => MessageBox.Query(80, 12, T("换图投递结果", "Map command delivery"), ServerMapClient.ReceiptText(receipt, _options.English), T("返回", "Back")));
            }
        }
        catch (Exception e) when (e is InstanceException or IOException)
        { Application.MainLoop.Invoke(() => MessageBox.ErrorQuery(80, 14, T("地图操作未完成", "Map action failed"), e is InstanceException i ? i.Code + ": " + i.Message : e.Message, T("返回", "Back"))); }
        finally { Application.MainLoop.Invoke(() => { _mapBusy = false; RefreshMapPage(); }); }
    }
}
