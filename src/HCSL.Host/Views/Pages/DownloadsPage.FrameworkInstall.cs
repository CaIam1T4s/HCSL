using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using ArcCheckbox = TKCode.Arc.Checkbox;

namespace HCSL.Host.Views.Pages;

public partial class DownloadsPage
{
    private FrameworkReleaseEntry? installRelease;
    private readonly List<(FrameworkDefinition Definition, string Version, ArcCheckbox Check)> installDependencies = [];

    private HostServerController? Servers => (DataContext as ShellViewModel)?.Servers;

    /// <summary>
    /// Fills the install dialog for one release. Dependencies are resolved through the same read-only
    /// catalog call the version list uses, so the version shown here matches what would be installed.
    /// </summary>
    private async Task OpenInstallDialogAsync(FrameworkReleaseEntry release)
    {
        if (catalogId is not { } id)
            return;
        installRelease = release;
        InstallDialog.Title = "安装 " + (FrameworkRules.Find(id)?.DisplayName ?? id);
        InstallChannel.Content = release.Prerelease ? "预发布版" : "正式版";
        InstallChannel.Tone = release.Prerelease ? BadgeTone.Neutral : BadgeTone.Info;
        InstallVersion.Text = release.Version;
        InstallPublished.Text = release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
        InstallAsset.Text = release.Asset ?? "未提供下载资产";
        InstallAssetSize.Text = FormatBytes(release.Bytes);
        InstallDialog.Description = FrameworkRules.Find(id)?.Repository;
        InstallExisting.IsVisible = downloadedVersions.Contains(release.Version);
        InstallExisting.Text = "库中已有该版本，安装会覆盖库内文件。";
        ShowAvailability(release);
        ShowPersistentPaths(id);
        ShowPresetTarget();
        await ShowDependenciesAsync(id);
        InstallDialog.IsOpen = true;
        InstallConfirm.Focus();
    }

    /// <summary>
    /// A release without a usable asset cannot be installed. Say why and stop offering the accent
    /// primary action, instead of leaving a normally-coloured button that only refuses clicks.
    /// </summary>
    private void ShowAvailability(FrameworkReleaseEntry release)
    {
        var reason = release.Downloadable ? null : release.UnavailableReason ?? "该版本没有可下载的资产";
        InstallReason.IsVisible = reason is not null;
        InstallReason.Text = reason ?? "";
        InstallConfirm.Content = release.Downloadable ? "安装" : "不可安装";
        InstallConfirm.Variant = release.Downloadable ? ButtonVariant.Primary : ButtonVariant.Secondary;
        InstallConfirm.IsEnabled = release.Downloadable;
    }

    private void ShowPersistentPaths(string id)
    {
        var paths = FrameworkRules.Find(id)?.PersistentPaths;
        InstallPersistentRow.IsVisible = paths is { Length: > 0 };
        InstallPersistentPaths.Text = paths is null ? "" : string.Join(Environment.NewLine, paths.Select(p => p.RuntimePath));
    }

    private void ShowPresetTarget()
    {
        var selected = Servers?.Selected?.Entry;
        var preset = selected?.Presets.FirstOrDefault(p => p.Id == selected.Definition?.SelectedPresetId);
        InstallPreset.IsVisible = false;
        InstallPreset.Content = selected is null || preset is null ? "" : selected.Definition?.Name + " · " + preset.Name;
        // Frameworks live in the shared library, so adding one to a preset only needs a writable preset.
        InstallAddToPreset.IsChecked = false;
        InstallAddToPreset.IsEnabled = Servers is { CanEditPlugins: true } && preset is not null;
        InstallAddToPreset.Description = InstallAddToPreset.IsEnabled
            ? null
            : "服务器运行中或未连接，安装后可到「插件」页启用";
    }

    private async Task ShowDependenciesAsync(string id)
    {
        foreach (var (_, _, existing) in installDependencies)
            InstallDependencies.Children.Remove(existing);
        installDependencies.Clear();

        var requires = FrameworkRules.Find(id)?.Requires;
        var definition = requires is null ? null : FrameworkRules.Find(requires);
        InstallDependencies.IsVisible = definition is not null;
        if (definition is null || Servers is not { } servers)
            return;

        string? latest = null;
        string? installed = null;
        try
        {
            var page = await servers.FrameworkCatalogAsync(new(definition.Id, CacheOnly: true), CatalogWorkspace);
            latest = FrameworkRules.SelectableVersion(definition.Id, page.Versions, OperatingSystem.IsWindows());
            installed = FrameworkRules.OrderVersions(definition.Id, page.InstalledVersions).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            latest = null;
        }

        var satisfied = installed is not null && latest is not null &&
            FrameworkRules.CompareVersions(definition.Id, latest, installed) <= 0;
        var check = new ArcCheckbox
        {
            Content = definition.DisplayName + "（" + (installed ?? latest ?? "无可用版本") + "）",
            IsChecked = true,
            IsEnabled = !satisfied && latest is not null,
            VerticalAlignment = VerticalAlignment.Center
        };
        var state = new Badge
        {
            Content = satisfied ? "已安装" : latest is null ? "不可用" : "将安装",
            Tone = satisfied ? BadgeTone.Success : latest is null ? BadgeTone.Danger : BadgeTone.Info,
            Size = BadgeSize.Small,
            VerticalAlignment = VerticalAlignment.Center
        };
        // One fixed-height row per dependency: checkbox left, state right, matching the informational
        // rows' rhythm instead of the 44px default control height.
        var row = new Grid { Height = 24, ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(check);
        Grid.SetColumn(state, 1);
        row.Children.Add(state);
        if (latest is null)
            InstallConfirm.IsEnabled = false;
        installDependencies.Add((definition, latest ?? installed ?? "", check));
        InstallDependencies.Children.Add(row);
    }

    /// <summary>Shows the preset target only while the option is on, so the badge never reads as "already added".</summary>
    private void PresetToggle(object? sender, TKCode.Arc.CheckboxStateChangedEventArgs e) =>
        InstallPreset.IsVisible = InstallAddToPreset.IsChecked == true && InstallPreset.Content is not null;

    private async void ConfirmInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        InstallConfirm.IsLoading = true;
        InstallConfirm.IsEnabled = false;
        InstallDismiss.IsEnabled = false;
        try { await InstallAsync(); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            Owner?.ShowInstanceNotification("安装失败", error.Message, ToastKind.Error);
        }
        finally
        {
            InstallConfirm.IsLoading = false;
            InstallConfirm.IsEnabled = true;
            InstallDismiss.IsEnabled = true;
        }
    }

    private async Task InstallAsync()
    {
        if (installRelease is not { } release || catalogId is not { } id || Servers is not { } servers)
            throw new IOException("HCSL-FRAMEWORK-BUSY: 无法读取安装目标。");

        foreach (var (dependency, version, toggle) in installDependencies)
            if (toggle.IsChecked == true && toggle.IsEnabled)
                await servers.InstallFrameworkAsync(dependency.Id, version);

        var result = await servers.InstallFrameworkAsync(id, release.Version);
        if (InstallAddToPreset.IsChecked == true)
            await AddFrameworkToPresetAsync(id);

        Owner?.ShowInstanceNotification("已安装 " + (FrameworkRules.Find(id)?.DisplayName ?? id),
            result.Version + " · " + result.Files + " 个文件", ToastKind.Success);
        InstallDialog.IsOpen = false;
        await LoadCatalogAsync(new(id, CacheOnly: true), catalogGeneration);
    }

    /// <summary>
    /// Enables the freshly installed framework in the selected preset through the shared plugin
    /// manifest path, so the preset keeps its old framework selectable instead of losing plugins.
    /// </summary>
    private async Task AddFrameworkToPresetAsync(string id)
    {
        if (Servers is not { } servers || servers.Selected?.Entry is not { Definition: { } definition } entry)
            return;
        var preset = entry.Presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId);
        if (preset is null) return;

        var current = await servers.PluginsAsync(new(PluginAction.List, entry.Id, preset.Id));
        var selections = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
        var choice = FrameworkSelection.Apply(selections, id, current);
        if (choice.MissingFramework is not null)
        {
            Owner?.ShowInstanceNotification("未能加入当前预设", choice.MissingFramework, ToastKind.Warning);
            return;
        }
        await servers.PluginsAsync(new(PluginAction.Save, entry.Id, preset.Id, definition.Revision, current.Hash, choice.Selections));
        Owner?.ShowInstanceNotification("已加入当前预设",
            definition.Name + " · " + preset.Name + "，重新部署后生效", ToastKind.Info);
    }

    private void CloseInstallDialog(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => InstallDialog.IsOpen = false;

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "大小未知",
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#") + " KB",
        _ => (bytes / (1024d * 1024d)).ToString("0.#") + " MB"
    };
}
