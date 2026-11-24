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
    private bool choosingRelease;
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
    private async Task ChooseReleaseAsync(FrameworkReleaseEntry release)
    {
        if (choosingRelease || InstallDialog.IsOpen || InstallProgressDialog.IsOpen || catalogId is not { } id) return;
        choosingRelease = true;
        var generation = catalogGeneration;
        try
        {
            if (catalogPresetTarget is null || catalogPresetVersion is not { } current)
            {
                await PrepareInstallAsync(id, release);
                if (generation != catalogGeneration || !IsVisible) return;
                InstallDialog.IsOpen = true;
                InstallConfirm.Focus();
                return;
            }
            if (current == release.Version) return;
            if (Servers?.CanEditPlugins != true || (!release.Downloadable && !downloadedVersions.Contains(release.Version)))
            {
                Owner?.ShowInstanceNotification("暂不能更换框架版本", release.UnavailableReason ?? "请确认目标预设可修改，且所选版本可用。", ToastKind.Warning);
                return;
            }
            var dialog = new Dialog
            {
                Title = "更换版本",
                Description = $"将当前预设的 {FrameworkRules.Find(id)?.DisplayName ?? id} 从 {current} 更换为 {release.Version}？",
                Actions = VersionConfirmationActions()
            };
            if (await dialog.ShowAsync(this) is not true || generation != catalogGeneration) return;
            await PrepareInstallAsync(id, release);
            if (generation != catalogGeneration || !IsVisible || !InstallAddToPreset.IsEnabled || installPresetTarget is null) return;
            InstallAddToPreset.IsChecked = true;
            if (downloadedVersions.Contains(release.Version))
            {
                await AddFrameworkToPresetAsync(id, release.Version, installPresetTarget.Value, current);
                return;
            }
            if (InstallConfirm.IsEnabled) await InstallAsync(current);
            else Owner?.ShowInstanceNotification("暂不能更换框架版本", InstallReason.Text, ToastKind.Warning);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InstanceException)
        { Owner?.ShowInstanceNotification("无法选择框架版本", error.Message, ToastKind.Error); }
        finally { choosingRelease = false; }
    }

    private static StackPanel VersionConfirmationActions()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Classes.Add("dialog-actions");
        var cancel = new TKCode.Arc.Button { Content = "取消", Variant = ButtonVariant.Secondary };
        var confirm = new TKCode.Arc.Button { Content = "更换", Variant = ButtonVariant.Primary };
        confirm.Classes.Add("dialog-primary");
        Dialog.SetResult(cancel, false);
        Dialog.SetResult(confirm, true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        return actions;
    }

    private async Task PrepareInstallAsync(string id, FrameworkReleaseEntry release)
    {
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

    private (Guid Instance, Guid Preset)? installPresetTarget;

    private void ShowPresetTarget()
    {
        var selected = catalogPresetTarget is { } source
            ? Servers?.Instances.FirstOrDefault(item => item.Entry.Id == source.Instance)?.Entry
            : Servers?.Selected?.Entry;
        var presetId = catalogPresetTarget?.Preset ?? selected?.Definition?.SelectedPresetId;
        var preset = selected?.Presets.FirstOrDefault(p => p.Id == presetId);
        installPresetTarget = selected is not null && preset is not null ? (selected.Id, preset.Id) : null;
        InstallPreset.IsVisible = false;
        InstallPreset.Content = selected is null || preset is null ? "" : selected.Definition?.Name + " · " + preset.Name;
        // Frameworks live in the shared library, so adding one to a preset only needs a writable preset.
        InstallAddToPreset.IsEnabled = Servers is { CanEditPlugins: true } && preset is not null;
        InstallAddToPreset.IsChecked = catalogPresetTarget is not null && InstallAddToPreset.IsEnabled;
        InstallAddToPreset.Description = InstallAddToPreset.IsEnabled
            ? null
            : "服务器运行中或未连接，当前目标预设不可修改";
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
    /// Mirrors the server download dialog's readouts: a clock on the right and the traffic line under
    /// the bar. The host measures the transfer and answers lightweight polls, so both figures are real;
    /// until the first measurement arrives they stay a half-width dash.
    /// </summary>
    private void ShowInstallProgress(string name, FrameworkReleaseEntry release)
    {
        installTimer.Tick -= TickInstallElapsed;
        installTimer.Tick += TickInstallElapsed;
        InstallTaskTitle.Text = "正在安装 " + name;
        InstallSpeed.Text = "-";
        InstallBytes.Text = "- / " + FormatBytes(release.Bytes);
        InstallElapsed.Text = "00:00";
        // Unknown until the host reports bytes: the skeleton stays until a real percentage exists,
        // exactly like the server download dialog.
        InstallProgressRegion.IsLoading = true;
        InstallProgress.Value = 0;
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

    private async Task InstallAsync(string? expectedPresetVersion = null)
    {
        if (installRelease is not { } release || catalogId is not { } id || Servers is not { } servers)
            throw new IOException("HCSL-FRAMEWORK-BUSY: 无法读取安装目标。");

        var name = FrameworkRules.Find(id)?.DisplayName ?? id;
        var presetTarget = InstallAddToPreset.IsChecked == true ? installPresetTarget : null;
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

            var request = NewInstallRequest();
            using var tracking = new CancellationTokenSource();
            var poll = TrackInstallTransferAsync(request, release, tracking.Token);
            FrameworkInstallResult result;
            try { result = await servers.InstallFrameworkAsync(id, release.Version, request); }
            finally { tracking.Cancel(); }
            await poll;
            InstallSpeed.Text = "-";
            InstallStepsList.CurrentIndex++;
            if (presetTarget is { } target)
                await AddFrameworkToPresetAsync(id, result.Version, target, expectedPresetVersion);

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

    /// <summary>Polls the host while the install runs. The host owns the measurement; this only reads it.</summary>
    private async Task TrackInstallTransferAsync(Guid request, FrameworkReleaseEntry release, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if (Servers is not { } servers || installRequest != request) return;
                var transfer = await servers.FrameworkProgressAsync(request, ct);
                if (transfer is null) continue;
                ApplyTransfer(transfer, release);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            // A missed poll must never fail the install; the next tick simply retries.
        }
    }

    private void ApplyTransfer(HCSL.Contracts.TransferSnapshot transfer, FrameworkReleaseEntry release)
    {
        InstallBytes.Text = (transfer.DoneBytes is { } done ? FormatBytes(done) : "-") + " / "
            + (transfer.TotalBytes is { } total ? FormatBytes(total) : FormatBytes(release.Bytes));
        InstallSpeed.Text = transfer is { Phase: HCSL.Contracts.TransferPhase.Downloading, BytesPerSecond: { } rate } && rate > 0
            ? FormatBytes((long)rate) + "/s" : "-";
        if (transfer.Percent is { } percent)
        {
            // Only a measured percentage replaces the skeleton; the installing phase has none.
            InstallProgressRegion.IsLoading = false;
            InstallProgress.Value = percent;
        }
        else InstallProgressRegion.IsLoading = true;
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
    private async Task AddFrameworkToPresetAsync(string id, string version, (Guid Instance, Guid Preset) target, string? expectedPresetVersion = null)
    {
        if (Servers is not { } servers || servers.Instances.FirstOrDefault(item => item.Entry.Id == target.Instance)?.Entry
            is not { Definition: { } definition } entry) return;
        var preset = entry.Presets.FirstOrDefault(p => p.Id == target.Preset);
        if (preset is null) return;

        var current = await servers.PluginsAsync(new(PluginAction.List, entry.Id, preset.Id));
        if (expectedPresetVersion is not null && current.Selected.FirstOrDefault(s => s.Id == id && s.Enabled)?.Version != expectedPresetVersion)
            throw new IOException("预设框架已变更，请重新打开框架页确认。");
        var selections = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
        var choice = FrameworkRules.Find(id)?.Kind == FrameworkKind.Base
            ? new FrameworkChoice([new(id, version, true), .. selections.Where(s => s.Id != id)], null, [])
            : FrameworkSelection.Apply(selections, id, current, version);
        if (choice.MissingFramework is not null)
        {
            Owner?.ShowInstanceNotification("未能加入当前预设", choice.MissingFramework, ToastKind.Warning);
            return;
        }
        await servers.PluginsAsync(new(PluginAction.Save, entry.Id, preset.Id, definition.Revision, current.Hash, choice.Selections));
        if (catalogPresetTarget == target && catalogId == id) catalogPresetVersion = version;
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
