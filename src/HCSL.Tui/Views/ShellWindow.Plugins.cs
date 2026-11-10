using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views.Controls;
using Terminal.Gui;
namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

public sealed partial class ShellWindow
{
    private readonly List<InstanceActionButton> _pluginActions = [];
    private bool _pluginBusy;
    private string _pluginSummary = "";
    private string _pluginSummaryKey = "";
    private void InitializePlugins()
    {
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var button = new InstanceActionButton { X = 1 + i * 20, Y = Pos.AnchorEnd(2), Width = 19, AutoSize = false, Visible = false };
            button.Clicked += () => { _ = PluginActionAsync(index); };
            button.PeerRequested += direction =>
            {
                for (var step = 1; step <= _pluginActions.Count; step++)
                {
                    var peer = _pluginActions[(index + direction * step + _pluginActions.Count) % _pluginActions.Count];
                    if (peer.Enabled && peer.CanFocus) { peer.SetFocus(); break; }
                }
            };
            _pluginActions.Add(button); _page.Add(button);
        }
    }
    private void RefreshPluginPage()
    {
        if (_pluginActions.Count == 0) return;
        _pluginActions[0].Text = T("插件管理", "Plugins"); _pluginActions[1].Text = T("刷新", "Refresh");
        var available = CurrentInstance?.Definition is not null && _server.Connected && !_pluginBusy && !_instanceBusy;
        _pluginActions[0].Enabled = available; _pluginActions[1].Enabled = available;
        if (_pageId != "plugins") return;
        var entry = CurrentInstance; var preset = entry?.Presets.FirstOrDefault(p => p.Id == entry.Definition?.SelectedPresetId);
        var key = entry is null || preset is null ? "" : PluginKey(entry.Id, preset.Id);
        if (key != _pluginSummaryKey) { _pluginSummary = ""; _pluginSummaryKey = key; }
        _content.Text = T("当前所选预设：", "Selected preset: ") + (preset is null ? "—" : preset.Name)
            + "\n\n" + T("插件按清单顺序启用；保存不会自动部署，需要重新部署/启动实例。", "Plugins enable in manifest order. Saving does not deploy; deploy or start the instance again.")
            + "\n" + T("包必须由 library/plugin-packages/<id>/<version> 提供，且只复制到实例运行目录；Metamod/CSS 需要正确 addons/gameinfo overlay。", "Packages must exist under library/plugin-packages/<id>/<version> and are copied only into the instance runtime. Metamod/CSS need correct addons/gameinfo overlays.")
            + (_pluginSummary.Length == 0 ? "" : "\n\n" + _pluginSummary)
            + "\n\n" + T("授权服务、依赖解析、远程下载和自动更新尚未接入。", "Access authorization, dependency resolution, remote download and auto-update are not connected.");
    }
    private static string PluginKey(Guid instanceId, Guid presetId) => instanceId.ToString("D") + "/" + presetId.ToString("D");
    private string DescribePlugins(PluginResult result)
    {
        var total = result.AvailableTotal > result.Available.Length ? " / " + result.AvailableTotal : "";
        var text = T("已刷新：可用 ", "Refreshed: available ") + result.Available.Length + total
            + T(" 个包版本，清单启用 ", " package versions, enabled ") + result.Selected.Count(p => p.Enabled) + T(" 个。", ".");
        if (result.Rejected is { Length: > 0 } rejected)
            text += "\n" + T("被拒绝的包 ", "Rejected ") + rejected.Length + ": "
                + string.Join("; ", rejected.Take(3).Select(r => $"{r.Id}/{r.Version} {r.Code}"));
        return text;
    }
    /// <summary>Terminal parity for the GUI directory buttons: show the resolved directories instead of opening them.</summary>
    private async Task<string> DescribePluginPathsAsync(Guid instanceId, Guid presetId)
    {
        try
        {
            var addons = await _instanceClient.PresetDirectoryAsync(instanceId, presetId, "addons");
            var runtime = await _instanceClient.RuntimeDirectoryAsync(instanceId);
            return T("预设 addons：", "Preset addons: ") + addons + "\n"
                + T("运行目录：", "Runtime: ") + runtime + (Directory.Exists(runtime) ? "" : T("（尚未部署）", " (not deployed)"));
        }
        catch (Exception e) when (e is IOException) { return e.Message; }
    }
    /// <summary>The refresh action queries the daemon again; it never just repaints the old text.</summary>
    private async Task RefreshPluginListAsync(Guid instanceId, Guid presetId)
    {
        _pluginBusy = true; RefreshPluginPage();
        try
        {
            var result = await new PresetPluginClient(_instanceClient, _server).ExecuteAsync(new(PluginAction.List, instanceId, presetId));
            var paths = await DescribePluginPathsAsync(instanceId, presetId);
            Application.MainLoop.Invoke(() =>
            {
                _pluginSummary = DescribePlugins(result) + "\n" + paths; _pluginSummaryKey = PluginKey(instanceId, presetId);
                _pluginBusy = false; RefreshPluginPage();
            });
        }
        catch (Exception e)
        {
            Application.MainLoop.Invoke(() =>
            {
                _pluginSummary = e.Message; _pluginSummaryKey = PluginKey(instanceId, presetId);
                _pluginBusy = false; RefreshPluginPage();
            });
        }
    }
    private async Task PluginActionAsync(int action)
    {
        if (_pluginBusy) return;
        if (CurrentInstance is not { Definition: { } d } entry) return;
        var preset = entry.Presets.First(x => x.Id == d.SelectedPresetId);
        if (action == 1) { await RefreshPluginListAsync(d.Id, preset.Id); return; }
        _pluginBusy = true; RefreshPluginPage();
        try
        {
            using var dialog = new PluginsDialog(new(_instanceClient, _server), d, preset, _options.English);
            dialog.Ready += dialog.Initialize;
            Application.Run(dialog);
        }
        finally { Application.MainLoop.Invoke(() => { _pluginBusy = false; RefreshPluginPage(); }); }
    }
}
