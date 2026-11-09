using HCSL.Core;
using HCSL.Application.Abstractions;

namespace HCSL.Frontend;

public sealed record HostPresetItem(PresetDefinition Definition, bool Selected)
{
    public override string ToString() => Definition.Name + (Selected ? "（当前所选）" : "") + " · " + ServerMapRules.Label(new(Definition.MapKind, Definition.Map));
}

public sealed partial class HostServerController
{
    private readonly ILocalDirectoryOpener? directoryOpener;
    public string PresetStatusText
    {
        get
        {
            var selectedPreset = Selected?.Entry.Presets.FirstOrDefault(p => p.Id == Selected.Entry.Definition?.SelectedPresetId);
            var configured = selectedPreset is null ? "当前所选预设：无" : $"当前所选预设：{selectedPreset.Name} · 修订 {selectedPreset.Revision} · 下次启动使用";
            var actual = Server.Snapshot?.Preset;
            var running = Server.LiveInstanceId is null ? "运行部署预设：无活动会话" : actual is null
                ? "运行部署预设：宿主未报告（不从当前选择推断）"
                : $"运行部署预设：{actual.Name} · 修订 {actual.Revision} · 初始地图 {ServerMapRules.Label(new(actual.MapKind, actual.Map))}";
            return configured + "\n" + running + "\n修改/选择需要先停服，下次启动按需自动部署；不会立即换图。VAC 偏好暂不提供编辑，实际仍关闭。";
        }
    }
    public Task<bool> CreatePresetAsync(Guid instanceId, long revision, PresetDefinition preset) =>
        WriteInstanceAsync(new(InstanceAction.CreatePreset, instanceId, Revision: revision, Name: preset.Name, Preset: preset));
    public Task<bool> CopyPresetAsync(Guid instanceId, long revision, Guid presetId, string name) =>
        WriteInstanceAsync(new(InstanceAction.CopyPreset, instanceId, presetId, revision, name));
    public Task<bool> UpdatePresetAsync(Guid instanceId, long revision, PresetDefinition preset) =>
        WriteInstanceAsync(new(InstanceAction.UpdatePreset, instanceId, preset.Id, revision, Preset: preset));
    public Task<bool> SelectPresetAsync(Guid instanceId, long revision, Guid presetId) =>
        WriteInstanceAsync(new(InstanceAction.SelectPreset, instanceId, presetId, revision));
    public Task<bool> DeletePresetAsync(Guid instanceId, long revision, Guid presetId) =>
        WriteInstanceAsync(new(InstanceAction.DeletePreset, instanceId, presetId, revision));
    public async Task<bool> OpenPresetCfgAsync(Guid instanceId, Guid presetId)
    {
        if (Busy || !Server.Connected || directoryOpener is null ||
            !Instances.Any(i => i.Entry.Id == instanceId && i.Entry.Presets.Any(p => p.Id == presetId)))
        { Error = "HCSL-PRESET-CFG: 请连接宿主并选择有效预设；当前文件管理器入口不可用。"; Notify(); return false; }
        Busy = true; Error = ""; Notify();
        try
        {
            var path = await instances.PresetCfgDirectoryAsync(instanceId, presetId);
            await directoryOpener.OpenAsync(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = e.Message; return false; }
        finally { Busy = false; Notify(); }
    }
}
