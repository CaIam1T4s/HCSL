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
    private Guid? installRequest;
    private bool installCancelled;
    private readonly System.Diagnostics.Stopwatch installElapsed = new();
    private readonly DispatcherTimer installTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(FrameworkDefinition Definition, string Version, ArcCheckbox Check, Control Row)> installDependencies = [];

    private HostServerController? Servers => (DataContext as ShellViewModel)?.Servers;

    private void TickInstallElapsed(object? sender, EventArgs e)
    {
        var time = installElapsed.Elapsed;
        InstallElapsed.Text = time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : time.ToString(@"mm\:ss");
    }

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
        // Remove the row that was added, not the checkbox it contains; otherwise reopening the dialog
        // keeps stacking one more row per open.
        foreach (var (_, _, _, existing) in installDependencies)
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

        var shownVersion = installed ?? latest ?? "无可用版本";
        // "Installed" means the very version this row names is on disk; a newer release in the
        // catalog does not turn an already satisfied dependency back into work to do.
        var satisfied = installed is not null && string.Equals(shownVersion, installed, StringComparison.Ordinal);
        var check = new ArcCheckbox
        {
            Content = definition.DisplayName + "（" + shownVersion + "）",
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
        // One row per dependency: checkbox left, state right. The negative margin only shrinks the
        // row's footprint in the flow, so the 44px control is neither squeezed nor clipped.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, -10, 0, -10) };
        row.Children.Add(check);
        Grid.SetColumn(state, 1);
        row.Children.Add(state);
        if (latest is null)
            InstallConfirm.IsEnabled = false;
        installDependencies.Add((definition, latest ?? installed ?? "", check, row));
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

    /// <summary>
    /// Mirrors the server download dialog's readouts: a clock on the right, and the traffic line under
    /// the bar. The host reports no intermediate bytes, so the transfer figures stay "-" while the
    /// release's known size supplies the denominator.
    /// </summary>
    private void ShowInstallProgress(string name, FrameworkReleaseEntry release)
    {
        installTimer.Tick -= TickInstallElapsed;
        installTimer.Tick += TickInstallElapsed;
        InstallTaskTitle.Text = "正在安装 " + name;
        InstallTotalSize.Text = "共 " + FormatBytes(release.Bytes);
        InstallElapsed.Text = "00:00";
        installElapsed.Restart();
        installTimer.Start();
        InstallProgressDialog.IsOpen = true;
    }

    private void StopInstallProgress()
    {
        installTimer.Stop();
        installTimer.Tick -= TickInstallElapsed;
        installElapsed.Reset();
        InstallProgressDialog.IsOpen = false;
    }

    private async Task InstallAsync()
    {
        if (installRelease is not { } release || catalogId is not { } id || Servers is not { } servers)
            throw new IOException("HCSL-FRAMEWORK-BUSY: 无法读取安装目标。");

        var name = FrameworkRules.Find(id)?.DisplayName ?? id;
        var pending = installDependencies
            .Where(d => d.Check.IsChecked == true && d.Check.IsEnabled)
            .ToArray();
        InstallTaskTitle.Text = "正在安装 " + name;
        InstallStepsList.Steps.Clear();
        InstallStepsList.Steps.Add(new StepperStep { Label = "准备安装" });
        foreach (var dependency in pending)
            InstallStepsList.Steps.Add(new StepperStep { Label = "安装 " + dependency.Definition.DisplayName });
        InstallStepsList.Steps.Add(new StepperStep { Label = "安装 " + name });
        InstallStepsList.Steps.Add(new StepperStep { Label = "刷新版本目录" });
        InstallStepsList.CurrentIndex = 1;
        InstallDialog.IsOpen = false;
        ShowInstallProgress(name, release);

        try
        {
            foreach (var dependency in pending)
            {
                await servers.InstallFrameworkAsync(dependency.Definition.Id, dependency.Version, NewInstallRequest());
                InstallStepsList.CurrentIndex++;
            }

            var result = await servers.InstallFrameworkAsync(id, release.Version, NewInstallRequest());
            InstallStepsList.CurrentIndex++;
            if (InstallAddToPreset.IsChecked == true)
                await AddFrameworkToPresetAsync(id);

            await LoadCatalogAsync(new(id, CacheOnly: true), catalogGeneration);
            InstallStepsList.CurrentIndex = InstallStepsList.Steps.Count;
            Owner?.ShowInstanceNotification("已安装 " + name,
                result.Version + " · " + result.Files + " 个文件", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            MarkInstallCancelled();
            Owner?.ShowInstanceNotification("已取消安装", name + " 未安装，临时文件已清理", ToastKind.Warning);
        }
        catch (IOException) when (installCancelled)
        {
            MarkInstallCancelled();
            Owner?.ShowInstanceNotification("已取消安装", name + " 未安装，临时文件已清理", ToastKind.Warning);
        }
        finally
        {
            installCancelled = false;
            StopInstallProgress();
        }
    }

    private Guid NewInstallRequest()
    {
        var request = Guid.NewGuid();
        installRequest = request;
        return request;
    }

    /// <summary>Marks the step that was running as failed and appends the cancelled outcome to the stepper.</summary>
    private void MarkInstallCancelled()
    {
        if (InstallStepsList.Steps.Count > 0 && InstallStepsList.CurrentIndex < InstallStepsList.Steps.Count
            && InstallStepsList.Steps[InstallStepsList.CurrentIndex] is { } running)
            running.Error = "已取消";
        InstallStepsList.Steps.Add(new StepperStep { Label = "已取消" });
        InstallStepsList.CurrentIndex = InstallStepsList.Steps.Count;
    }

    private async void CancelInstall(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (installRequest is not { } request || Servers is not { } servers) return;
        installCancelled = true;
        InstallCancelButton.IsEnabled = false;
        try { await servers.CancelFrameworkInstallAsync(request); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            Owner?.ShowInstanceNotification("取消未生效", error.Message, ToastKind.Error);
        }
        finally { InstallCancelButton.IsEnabled = true; }
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
