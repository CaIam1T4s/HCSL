using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using TKCode.Arc;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class InstanceContentPage : UserControl
{
    public sealed record Row(FrameworkDefinition Framework, string Name, string State, string? PresetVersion)
    {
        public bool CanDisable => PresetVersion is not null;
    }
    private HostServerController? observed;
    private bool loading, opening;
    private (Guid Instance, Guid Preset)? loaded;

    public InstanceContentPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => Subscribe();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            loaded = null;
            _ = LoadAsync();
        };
    }

    private void Subscribe()
    {
        Unsubscribe();
        observed = (DataContext as ShellViewModel)?.Servers;
        if (observed is not null) observed.PropertyChanged += Changed;
        _ = LoadAsync();
    }

    private void Unsubscribe()
    {
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = null;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    private (HostServerController Model, InstanceDefinition Instance, PresetDefinition Preset)? Scope()
    {
        if (DataContext is ShellViewModel { ManagedPresetId: { } id } shell &&
            shell.Servers.Selected?.Entry is { Definition: { } instance } entry &&
            entry.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            return (shell.Servers, instance, preset);
        return null;
    }

    private async Task LoadAsync()
    {
        if (!IsVisible || loading || opening || Scope() is not { } scope || !scope.Model.Server.Connected) return;
        var key = (scope.Instance.Id, scope.Preset.Id);
        if (loaded == key) return;
        loading = true;
        loaded = key;
        Frameworks.ItemsSource = null;
        try
        {
            var plugins = await scope.Model.PluginsAsync(new(PluginAction.List, key.Item1, key.Item2, FrameworkOverviewOnly: true));
            if (Scope()?.Preset.Id != key.Item2) return;
            var enabled = plugins.Selected.Where(s => s.Enabled && FrameworkRules.Find(s.Id)?.Kind == FrameworkKind.ScriptFramework).ToArray();
            Frameworks.ItemsSource = FrameworkRules.Known.Select(framework =>
            {
                var version = plugins.Selected.FirstOrDefault(s => s.Id == framework.Id && s.Enabled)?.Version;
                var conflict = enabled.FirstOrDefault(s => FrameworkRules.Conflicts(s.Id, framework.Id));
                var state = version ?? (conflict is null ? "未安装" : "与 " +
                    (FrameworkRules.Find(conflict.Id)?.DisplayName ?? conflict.Id) + " 不兼容");
                return new Row(framework, framework.DisplayName, state, version);
            }).ToArray();
        }
        catch (IOException e)
        {
            if (IsVisible && Scope()?.Preset.Id == key.Item2 && TopLevel.GetTopLevel(this) is MainWindow owner)
                owner.ShowInstanceNotification("无法读取框架", e.Message);
        }
        finally
        {
            loading = false;
            if (Scope()?.Preset.Id != key.Item2) { loaded = null; _ = LoadAsync(); }
        }
    }

    private async void DisableFramework(object? sender, RoutedEventArgs e)
    {
        if (opening || sender is not Control { DataContext: Row { CanDisable: true } row } ||
            Scope() is not { } scope || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        opening = true;
        try
        {
            if (!scope.Model.CanEditPlugins) throw new IOException("请停止服务器并等待当前操作完成后再停用框架。");
            var current = await scope.Model.PluginsAsync(new(PluginAction.List, scope.Instance.Id, scope.Preset.Id, PackageMetadataOnly: true));
            var selected = current.Selected.FirstOrDefault(p => p.Id == row.Framework.Id && p.Enabled);
            if (selected?.Version != row.PresetVersion || Scope() != scope) return;
            var disabled = new HashSet<string>(StringComparer.Ordinal) { row.Framework.Id };
            var dependents = new List<PluginSelectionInfo>();
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in current.Selected.Where(p => p.Enabled))
                {
                    var required = FrameworkRules.Find(entry.Id)?.Requires ?? current.Available
                        .FirstOrDefault(a => a.Id == entry.Id && a.Version == entry.Version)?.Framework;
                    if (required is not null && disabled.Contains(required) && disabled.Add(entry.Id))
                    {
                        dependents.Add(entry);
                        changed = true;
                    }
                }
            } while (changed);
            if (dependents.Count > 0 && !await ConfirmDisableAsync(dependents)) return;
            if (Scope() != scope) return;
            var next = current.Selected.Select(p => new PluginSelection(p.Id, p.Version, p.Enabled && !disabled.Contains(p.Id))).ToArray();
            await scope.Model.PluginsAsync(new(PluginAction.Save, scope.Instance.Id, scope.Preset.Id,
                scope.Instance.Revision, current.Hash, next, PackageMetadataOnly: true));
        }
        catch (Exception ex) when (ex is IOException or InstanceException)
        { owner.ShowInstanceNotification("无法停用框架", ex.Message); }
        finally { opening = false; loaded = null; await LoadAsync(); }
    }

    private async Task<bool> ConfirmDisableAsync(IReadOnlyList<PluginSelectionInfo> dependents)
    {
        var table = new Table { Caption = "将一并停用的依赖项", IsSelectable = false,
            ItemsSource = dependents, Height = Math.Clamp(56 + 56 * dependents.Count, 180, 300) };
        // Keep both columns in the same row inside the dialog, below Arc's default compact breakpoint.
        SetTableWidthToken("CompactBreakpointProperty");
        SetTableWidthToken("MinTableWidthProperty");

        void SetTableWidthToken(string field)
        {
            if (typeof(Table).GetField(field, System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)?.GetValue(null) is Avalonia.StyledProperty<double> property)
                table.SetValue(property, 0d);
        }
        AddColumn("插件 / 框架", new GridLength(1, GridUnitType.Star), item => item.Name ?? item.Id);
        AddColumn("版本", new GridLength(112), item => item.Version, right: true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Classes.Add("dialog-actions");
        AddAction("取消", false);
        AddAction("停用", true);
        var dialog = new Dialog { Title = "停用框架", Size = ControlSize.Medium,
            Description = "以下依赖也会同时停用",
            Content = table, Actions = actions };
        return await dialog.ShowAsync(this) is true;

        void AddColumn(string header, GridLength width, Func<PluginSelectionInfo, string> text, bool right = false) =>
            table.Columns.Add(new DataTableColumn { Header = header, Width = width, IsSortable = false, IsNumeric = right,
                CellTemplate = new FuncDataTemplate<PluginSelectionInfo>((item, _) => new TextBlock
                { Text = item is null ? "" : text(item), VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis }) });

        void AddAction(string label, bool primary)
        {
            var button = new TKCode.Arc.Button { Content = label, Variant = primary ? ButtonVariant.Primary : ButtonVariant.Secondary };
            if (primary) button.Classes.Add("dialog-primary");
            Dialog.SetResult(button, primary);
            actions.Children.Add(button);
        }
    }

    private async void OpenFramework(object? sender, RoutedEventArgs e)
    {
        if (opening || sender is not Control { DataContext: Row row } || Scope() is not { } scope ||
            TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        opening = true;
        try { await owner.OpenFrameworkDownloadsAsync(row.Framework.Id, scope.Instance.Id, scope.Preset.Id, row.PresetVersion); }
        finally
        {
            opening = false;
            loaded = null;
            await LoadAsync();
        }
    }
}
