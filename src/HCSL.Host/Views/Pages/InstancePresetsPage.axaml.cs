using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

/// <summary>预设与配置: one preset list plus everything that edits the selected preset's files.</summary>
public partial class InstancePresetsPage : UserControl
{
    private HostServerController? subscribed;
    private HostPresetItem[] items = [];
    private HostPresetItem? browsed;
    private bool operating, updating;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private PresetDefinition? BrowsedPreset => browsed?.Definition;

    public InstancePresetsPage()
    {
        InitializeComponent();
        DeleteButton.Icon = Lucide.Geometry(LucideIconKind.Trash);
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        // Changes that arrive while another section is open still have to be rendered when this page is shown.
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Update(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        Update();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = null;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible) Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        if (Model is not { } m || updating) return;
        updating = true;
        try
        {
            var entry = m.Selected?.Entry;
            var next = (entry?.Presets ?? []).Select(p => new HostPresetItem(p, p.Id == entry?.Definition?.SelectedPresetId)).ToArray();
            if (next.Length != items.Length || next.Where((item, index) => !items[index].Definition.Equals(item.Definition) || items[index].Selected != item.Selected).Any())
            {
                var wanted = browsed?.Definition.Id ?? entry?.Definition?.SelectedPresetId;
                items = next;
                PresetChoices.ItemsSource = items;
                browsed = items.FirstOrDefault(item => item.Definition.Id == wanted) ?? items.FirstOrDefault();
                PresetChoices.SelectedItem = browsed;
            }
        }
        finally { updating = false; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (Model is not { } m) return;
        var editable = !operating && m.CanMutate;
        var preset = BrowsedPreset;
        CreateButton.IsEnabled = editable;
        PresetActions.IsVisible = preset is not null;
        PresetActionsTitle.Text = preset is null ? null : $"已选中：{preset.Name}";
        ManageButton.IsEnabled = preset is not null;
        CopyButton.IsEnabled = EditButton.IsEnabled = editable && preset is not null;
        var canSelect = preset is not null && preset.Id != m.Selected?.Entry.Definition?.SelectedPresetId;
        SelectButton.IsVisible = canSelect;
        SelectButton.IsEnabled = editable && canSelect;
        DeleteButton.IsEnabled = editable && preset is not null && m.Selected?.Entry.Presets.Length > 1
            && preset.Id != m.Selected.Entry.Definition?.SelectedPresetId;
        var connected = !operating && !m.Busy && m.Server.Connected;
        CfgButton.IsEnabled = connected && preset is not null;
        OpenCfgButton.IsEnabled = OpenAddonsButton.IsEnabled = connected && preset is not null;
    }

    private void PresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (updating || PresetChoices.SelectedItem is not HostPresetItem item) return;
        browsed = item;
        UpdateButtons();
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception) { Error("操作未完成", "请检查宿主状态后重试。"); }
        finally { operating = false; Update(); }
    }

    private void ManagePreset(object? sender, RoutedEventArgs e)
    {
        if (BrowsedPreset is not { } preset || DataContext is not ShellViewModel shell) return;
        shell.OpenPresetScope(preset.Id);
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
        var saved = await owner.EditPresetAsync(action switch { 0 => "新建配置预设", 1 => "复制配置预设", _ => "编辑配置预设" },
            source, action == 1, async draft =>
            {
                var success = action switch
                {
                    0 => await m.CreatePresetAsync(instance.Id, instance.Revision, draft),
                    1 => await m.CopyPresetAsync(instance.Id, instance.Revision, source!.Id, draft.Name),
                    _ => await m.UpdatePresetAsync(instance.Id, instance.Revision, draft)
                };
                return success ? null : m.Error;
            });
        if (!saved || action == 2) return;
        var created = m.Selected?.Entry.Presets.FirstOrDefault(p => !oldIds.Contains(p.Id));
        if (created is not null) browsed = items.FirstOrDefault(item => item.Definition.Id == created.Id) ?? browsed;
        Update();
    });

    private async void SelectPreset(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || !m.CanMutate || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
        if (await owner.ChooseAsync("选择配置预设", $"将“{instance.Name}”的当前所选改为“{preset.Name}”？", "取消", "选择") != 1) return;
        if (!await m.SelectPresetAsync(instance.Id, instance.Revision, preset.Id)) Error("选择未保存", m.Error);
    });

    private async void DeletePreset(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
        DeleteButton.IsConfirmed = false;
        if (!m.CanMutate)
        {
            Error("删除未提交", "宿主正忙或服务器运行中。");
            return;
        }
        operating = true;
        DeleteButton.IsEnabled = false;
        try
        {
            if (!await m.DeletePresetAsync(instance.Id, instance.Revision, preset.Id)) Error("删除未完成", m.Error);
            else Owner?.ShowInstanceNotification("预设已删除", preset.Name, ToastKind.Success);
        }
        finally { operating = false; Update(); }
    }

    private async void EditCfg(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
        await owner.EditCfgAsync(m, instance, preset);
    });

    private async void OpenCfg(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
        if (!await m.OpenPresetCfgAsync(instance.Id, preset.Id)) Error("无法打开 CFG 目录", m.Error);
    });

    private async void OpenAddons(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
        if (!await m.OpenPresetDirectoryAsync(instance.Id, preset.Id, "addons")) Error("无法打开预设 addons", m.Error);
    });
}
