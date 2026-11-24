using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    private GlassMenu? rowMenu;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private PresetDefinition? BrowsedPreset => browsed?.Definition;

    public InstancePresetsPage()
    {
        InitializeComponent();
        DeleteButton.Icon = Lucide.Geometry(LucideIconKind.Trash);
        PresetChoices.AddHandler(InputElement.ContextRequestedEvent, OnPresetContextRequested,
            RoutingStrategies.Bubble, handledEventsToo: true);
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
                rowMenu?.Close();
                DeletePopover.IsOpen = false;
                browsed = null;
                items = next;
                PresetChoices.ItemsSource = items;
            }
        }
        finally { updating = false; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        CreateButton.IsEnabled = !operating && Model?.CanMutate == true;
        DeleteButton.IsEnabled = CanDelete();
    }

    private bool CanDelete() => !operating && Model is { CanMutate: true, Selected.Entry: { } entry }
        && BrowsedPreset is { } preset && entry.Presets.Length > 1
        && preset.Id != entry.Definition?.SelectedPresetId && entry.Presets.Any(p => p.Id == preset.Id);

    private void OpenPresetActions(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HostPresetItem item } anchor) ShowPresetActions(item, anchor);
    }

    private void OnPresetContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        Control? anchor = null;
        if (e.TryGetPosition(PresetChoices, out var point))
        {
            var body = PresetChoices.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(v => v.Name == "PART_ScrollViewer");
            if (body?.TranslatePoint(default, PresetChoices) is not { } bodyOrigin ||
                !new Rect(bodyOrigin, body.Bounds.Size).Contains(point)) return;
            var row = body.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
                c.GetType().Name == "TableRow" && c.TranslatePoint(default, PresetChoices) is { } origin &&
                new Rect(origin, c.Bounds.Size).Contains(point));
            anchor = row?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("preset-row-action"));
        }
        else if (e.Source is Control source && source.Classes.Contains("preset-row-action")) anchor = source;
        if (anchor?.DataContext is not HostPresetItem item) return;
        ShowPresetActions(item, anchor);
        e.Handled = true;
    }

    private void ShowPresetActions(HostPresetItem row, Control anchor)
    {
        if (Model is not { Selected.Entry: { } entry } m || !entry.Presets.Any(p => p.Id == row.Definition.Id)) return;
        DeletePopover.IsOpen = false;
        rowMenu?.Close();
        browsed = row;
        var menu = rowMenu = new GlassMenu();
        var editable = !operating && m.CanMutate;
        var connected = !operating && !m.Busy && m.Server.Connected;
        Add("进入预设", ManagePreset, !operating);
        Add("复制预设", CopyPreset, editable);
        if (row.Definition.Id != entry.Definition?.SelectedPresetId) Add("设为启动预设", SelectPreset, editable);
        Add("CFG 管理", EditCfg, connected);
        Add("打开 CFG 目录", OpenCfg, connected);
        Add("打开 addons 目录", OpenAddons, connected);
        Add("删除预设…", (_, _) =>
        {
            if (!CanDelete()) return;
            DeleteButton.IsEnabled = true;
            DeleteButton.IsConfirmed = false;
            DeletePopover.ShowAt(anchor);
        }, CanDelete());
        menu.Open(anchor);

        void Add(string label, EventHandler<RoutedEventArgs> click, bool enabled)
        {
            var item = new GlassMenuItem { Content = label, IsEnabled = enabled };
            item.Click += click;
            menu.Items.Add(item);
        }
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

    private async void CreatePreset(object? sender, RoutedEventArgs e) => await CreatePresetAsync(copy: false);
    private async void CopyPreset(object? sender, RoutedEventArgs e) => await CreatePresetAsync(copy: true);

    private Task CreatePresetAsync(bool copy) => RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || !m.CanMutate || m.Selected?.Entry.Definition is not { } instance) return;
        var source = copy ? BrowsedPreset : null;
        if (copy && source is null) return;
        await owner.EditPresetAsync(copy ? "复制配置预设" : "新建配置预设", source, copy, async draft =>
            {
                var success = source is not null
                    ? await m.CopyPresetAsync(instance.Id, instance.Revision, source.Id, draft.Name)
                    : await m.CreatePresetAsync(instance.Id, instance.Revision, draft);
                return success ? null : m.Error;
            });
    });

    private async void SelectPreset(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || !m.CanMutate || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
        if (await owner.ChooseAsync("选择配置预设", $"将“{instance.Name}”的当前所选改为“{preset.Name}”？", "取消", "选择") != 1) return;
        if (!await m.SelectPresetAsync(instance.Id, instance.Revision, preset.Id)) Error("选择未保存", m.Error);
    });

    private async void DeletePreset(object? sender, RoutedEventArgs e)
    {
        DeletePopover.IsOpen = false;
        DeleteButton.IsConfirmed = false;
        if (!CanDelete() || Model is not { } m || m.Selected?.Entry.Definition is not { } instance || BrowsedPreset is not { } preset) return;
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
