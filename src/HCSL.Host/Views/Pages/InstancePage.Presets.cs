using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;

namespace HCSL.Host.Views.Pages;

public partial class InstancePage
{
    private bool updatingPresets;
    private Guid? presetInstance;
    private PresetDefinition[]? presetCatalog;
    private PresetDefinition? BrowsedPreset => (PresetSelect.SelectedItem as HostPresetItem)?.Definition;
    private void UpdatePresets()
    {
        if (Model is not { } m) return;
        var entry = m.Selected?.Entry;
        if (presetInstance != entry?.Id || !ReferenceEquals(presetCatalog, entry?.Presets))
        {
            var wanted = presetInstance == entry?.Id ? BrowsedPreset?.Id : null;
            presetInstance = entry?.Id; presetCatalog = entry?.Presets;
            var items = (entry?.Presets ?? []).Select(p => new HostPresetItem(p, p.Id == entry?.Definition?.SelectedPresetId)).ToArray();
            updatingPresets = true;
            try
            {
                PresetSelect.ItemsSource = items;
                PresetSelect.SelectedItem = items.FirstOrDefault(p => p.Definition.Id == wanted)
                    ?? items.FirstOrDefault(p => p.Selected) ?? items.FirstOrDefault();
            }
            finally { updatingPresets = false; }
        }
        PresetStatusText.Text = m.PresetStatusText;
        UpdatePresetButtons();
    }
    private void UpdatePresetButtons()
    {
        if (Model is not { } m) return;
        var editable = !operating && !watchingStart && m.CanMutate;
        var p = BrowsedPreset;
        PresetSelect.IsEnabled = !operating && !m.Busy;
        CreatePresetButton.IsEnabled = editable;
        CopyPresetButton.IsEnabled = EditPresetButton.IsEnabled = editable && p is not null;
        SelectPresetButton.IsEnabled = editable && p is not null && p.Id != m.Selected?.Entry.Definition?.SelectedPresetId;
        DeletePresetButton.IsEnabled = editable && p is not null && m.Selected?.Entry.Presets.Length > 1
            && p.Id != m.Selected.Entry.Definition?.SelectedPresetId;
        EditCfgButton.IsEnabled = !operating && !watchingStart && !m.Busy && m.Server.Connected && p is not null;
        PluginsButton.IsEnabled = !operating && !watchingStart && !m.Busy && m.Server.Connected && p is not null;
        MapPoolButton.IsEnabled = !operating && !watchingStart && !m.Busy && m.Server.Connected && p is not null;
        OpenCfgButton.IsEnabled = !operating && !m.Busy && m.Server.Connected && p is not null;
    }
    private async void CreatePreset(object? sender, RoutedEventArgs e) => await EditPresetCoreAsync(0);
    private async void CopyPreset(object? sender, RoutedEventArgs e) => await EditPresetCoreAsync(1);
    private async void EditPreset(object? sender, RoutedEventArgs e) => await EditPresetCoreAsync(2);
    private Task EditPresetCoreAsync(int action) => RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || !m.CanMutate || m.Selected?.Entry.Definition is not { } instance) return;
        var source = action == 0 ? null : BrowsedPreset;
        if (action != 0 && source is null) return;
        var oldIds = m.Selected.Entry.Presets.Select(p => p.Id).ToHashSet();
        var saved = await owner.EditPresetAsync(action switch { 0 => "新建配置预设", 1 => "复制配置预设", _ => "编辑配置预设" }, source, action == 1, async draft =>
        {
            var success = action switch
            {
                0 => await m.CreatePresetAsync(instance.Id, instance.Revision, draft),
                1 => await m.CopyPresetAsync(instance.Id, instance.Revision, source!.Id, draft.Name),
                _ => await m.UpdatePresetAsync(instance.Id, instance.Revision, draft)
            };
            return success ? null : m.Error;
        });
        if (saved && action != 2 && PresetSelect.ItemsSource is IEnumerable<HostPresetItem> items)
            PresetSelect.SelectedItem = items.FirstOrDefault(p => !oldIds.Contains(p.Definition.Id)) ?? PresetSelect.SelectedItem;
    });
    private async void SelectPreset(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || !m.CanMutate || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } p) return;
        if (await owner.ChooseAsync("选择配置预设", $"将“{instance.Name}”的当前所选改为“{p.Name}”？\n下次启动按需自动部署；不会立即换图。", "取消", "选择") == 1)
            if (!await m.SelectPresetAsync(instance.Id, instance.Revision, p.Id)) await owner.ChooseAsync("选择未保存", m.Error, "返回");
    });
    private async void DeletePreset(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || !m.CanMutate || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } p) return;
        if (await owner.ChooseAsync("删除配置预设", $"删除“{p.Name}”？\n目录移入回收区，不直接擦除。当前所选或唯一预设不能删除。实例密码不受影响。", "取消", "删除") == 1)
            if (!await m.DeletePresetAsync(instance.Id, instance.Revision, p.Id)) await owner.ChooseAsync("删除未完成", m.Error, "返回");
    });
    private async void EditPresetCfg(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } p) return;
        await owner.EditCfgAsync(m, instance, p);
    });
    private async void EditPresetPlugins(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } p) return;
        await owner.EditPluginsAsync(m, instance, p);
    });
    private async void EditPresetMapPool(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } p) return;
        await owner.EditMapPoolAsync(m, instance, p);
    });
    private async void OpenPresetCfg(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } p) return;
        if (!await m.OpenPresetCfgAsync(instance.Id, p.Id)) await owner.ChooseAsync("无法打开 CFG 目录", m.Error, "返回");
    });
}
