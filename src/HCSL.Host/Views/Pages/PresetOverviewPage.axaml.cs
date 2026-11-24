using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class PresetOverviewPage : UserControl
{
    private bool operating;
    private PresetDefinition? loaded;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetOverviewPage()
    {
        InitializeComponent();
        MapKindInput.ItemsSource = new[] { "普通地图", "工坊地图 ID" };
        DataContextChanged += (_, _) => LoadFields();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) LoadFields(); };
    }

    private (HostServerController Servers, InstanceDefinition Instance, PresetDefinition Preset)? Scope()
    {
        if (DataContext is ShellViewModel { ManagedPresetId: { } id } shell &&
            shell.Servers.Selected?.Entry is { Definition: { } instance } entry &&
            entry.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            return (shell.Servers, instance, preset);
        return null;
    }

    private void LoadFields()
    {
        loaded = Scope()?.Preset;
        NameInput.Text = loaded?.Name;
        MapInput.Text = loaded?.Map;
        MapKindInput.SelectedIndex = (int)(loaded?.MapKind ?? ServerMapKind.Standard);
        TypeInput.Text = loaded?.GameType.ToString();
        ModeInput.Text = loaded?.GameMode.ToString();
    }

    private async void SavePreset(object? sender, RoutedEventArgs e)
    {
        if (Scope() is not { } scope || !scope.Servers.CanMutate || loaded?.Id != scope.Preset.Id) return;
        await RunAsync(async () =>
        {
            if (loaded != scope.Preset)
                throw new IOException("预设已在其他位置修改，请重新打开基本信息后再编辑。");
            if (!int.TryParse(TypeInput.Text, out var type) || !int.TryParse(ModeInput.Text, out var mode))
                throw new IOException("game_type 和 game_mode 必须为整数。");
            var draft = scope.Preset with
            {
                Name = NameInput.Text?.Trim() ?? "", Map = MapInput.Text?.Trim() ?? "",
                MapKind = (ServerMapKind)MapKindInput.SelectedIndex, GameType = type, GameMode = mode
            };
            InstanceRules.Preset(draft);
            if (!await scope.Servers.UpdatePresetAsync(scope.Instance.Id, scope.Instance.Revision, draft))
                throw new IOException(scope.Servers.Error);
            if (Scope()?.Preset.Id == draft.Id) LoadFields();
            Owner?.ShowInstanceNotification("预设已保存");
        });
    }

    private async void OpenCfg(object? sender, RoutedEventArgs e) => await OpenDirectoryAsync(cfg: true);
    private async void OpenAddons(object? sender, RoutedEventArgs e) => await OpenDirectoryAsync(cfg: false);

    private Task OpenDirectoryAsync(bool cfg) => RunAsync(async () =>
    {
        if (Scope() is not { } scope || !scope.Servers.Server.Connected || scope.Servers.Busy) return;
        var ok = cfg ? await scope.Servers.OpenPresetCfgAsync(scope.Instance.Id, scope.Preset.Id)
            : await scope.Servers.OpenPresetDirectoryAsync(scope.Instance.Id, scope.Preset.Id, "addons");
        if (!ok) throw new IOException(scope.Servers.Error);
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        try { await action(); }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
        { Owner?.ShowInstanceNotification("操作未完成", e.Message); }
        finally { operating = false; }
    }
}
