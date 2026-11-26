using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.UI.Shared.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace HCSL.Host.Views.Pages;

public partial class DownloadsPage
{
    private string? catalogId;
    private (Guid Instance, Guid Preset)? catalogPresetTarget;
    private string? catalogPresetVersion;
    private int catalogGeneration;
    private bool catalogBusy;
    private bool loadMoreAnimating;
    private FrameworkCatalogResult? catalogResult;
    private readonly List<FrameworkReleaseEntry> shownVersions = [];
    private readonly HashSet<string> downloadedVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> frameworkLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Badge> frameworkUpdates = new(StringComparer.Ordinal);

    private WorkspacePaths? CatalogWorkspace => Model is { SettingsRoot.Length: > 0, DataRoot.Length: > 0, LibraryRoot.Length: > 0 } model
        ? new(model.SettingsRoot, model.DataRoot, model.LibraryRoot) : null;

    private void InitializeFrameworkCatalog()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible && catalogPresetTarget is not null)
                ResetFrameworkCatalog();
        };
        VersionScroll.ScrollChanged += (_, e) =>
        {
            // Extent-only changes matter too: appended rows move the bottom without moving the offset,
            // and the button has to follow that new bottom.
            if (e.OffsetDelta.Y == 0 && e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0) return;
            UpdateLoadMoreVisibility();
        };
        VersionScroll.SizeChanged += (_, e) => UpdateLoadMoreVisibility();
        foreach (var framework in FrameworkRules.Known)
        {
            var version = new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            version.Classes.Add("muted");
            frameworkLabels.Add(framework.Id, version);
            var update = new Badge { Content = "更新可用", IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Tone = BadgeTone.Warning, Size = BadgeSize.Small };
            update.Classes.Add("update-badge");
            frameworkUpdates.Add(framework.Id, update);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8, Margin = new Thickness(16, 0) };
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock { Text = framework.DisplayName, VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(nameText);
            update.VerticalAlignment = VerticalAlignment.Center;
            namePanel.Children.Add(update);
            Grid.SetColumn(namePanel, 0); row.Children.Add(namePanel);
            Grid.SetColumn(version, 2); row.Children.Add(version);
            var button = new TKCode.Arc.Button { Content = row, Variant = ButtonVariant.Ghost, Height = 48, Padding = default,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, IsPressScaleEnabled = false };
            button.Classes.Add("resource-row");
            button.SizeChanged += (_, e) => row.Width = Math.Max(0, e.NewSize.Width - 32);
            button.Click += async (_, _) => await OpenFrameworkCatalogAsync(framework.Id);
            FrameworkRows.Children.Add(new ElevatedSurface(button));
        }
    }
    private async Task RefreshFrameworkRowsAsync()
    {
        if (subscribed is not { } shell || !shell.Servers.Server.Connected) return;
        foreach (var framework in FrameworkRules.Known)
            try
            {
                var page = await shell.Servers.FrameworkCatalogAsync(new(framework.Id, CacheOnly: true), CatalogWorkspace);
                frameworkLabels[framework.Id].Text = page.InstalledVersions.FirstOrDefault() ?? "未下载";
                var latest = FrameworkRules.SelectableVersion(framework.Id, page.Versions, OperatingSystem.IsWindows());
                var installed = FrameworkRules.OrderVersions(framework.Id, page.InstalledVersions).FirstOrDefault();
                frameworkUpdates[framework.Id].IsVisible = latest is not null && installed is not null &&
                    FrameworkRules.CompareVersions(framework.Id, latest, installed) > 0;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { frameworkLabels[framework.Id].Text = "-"; frameworkUpdates[framework.Id].IsVisible = false; }
    }
    private async void RetryFrameworkCatalog(object? sender, RoutedEventArgs e)
    {
        if (catalogId is not { } id) return;
        var generation = catalogGeneration;
        await LoadCatalogAsync(new(id, Refresh: true), generation);
    }
    internal async Task OpenFrameworkCatalogAsync(string id, (Guid Instance, Guid Preset)? presetTarget = null, string? presetVersion = null)
    {
        if (held || subscribed is null) return;
        catalogId = id;
        catalogPresetTarget = presetTarget;
        catalogPresetVersion = presetVersion;
        catalogGeneration++;
        catalogBusy = false;
        loadMoreAnimating = false;
        shownVersions.Clear(); downloadedVersions.Clear(); VersionRows.Children.Clear();
        catalogResult = null;
        VersionScroll.Offset = new Vector(0, 0);
        LoadMoreSurface.IsVisible = false;
        LoadMoreButton.IsHitTestVisible = false;
        LoadMoreButton.Opacity = 0;
        CatalogEmpty.IsVisible = false;
        ResourceList.IsVisible = false;
        FrameworkCatalogPanel.IsVisible = true;
        CatalogBack.IsVisible = true;
        DownloadsHeading.Text = FrameworkRules.Find(id)!.DisplayName;
        VersionsLoading.IsLoading = true;
        var generation = catalogGeneration;
        await LoadCatalogAsync(new(id, CacheOnly: true), generation);
        if (generation == catalogGeneration && catalogResult?.NeedsRefresh == true)
            await LoadCatalogAsync(new(id, Refresh: true), generation);
        Dispatcher.UIThread.Post(() => UpdateLoadMoreVisibility(), DispatcherPriority.Loaded + 1);
    }
    private void CloseFrameworkCatalog(object? sender, RoutedEventArgs e)
    {
        var fromPreset = catalogPresetTarget is not null;
        ResetFrameworkCatalog();
        if (fromPreset) subscribed?.ReturnToPresetFrameworks();
        else _ = RefreshFrameworkRowsAsync();
    }

    private void ResetFrameworkCatalog()
    {
        catalogGeneration++;
        catalogId = null;
        catalogPresetTarget = null;
        catalogPresetVersion = null;
        catalogBusy = false;
        loadMoreAnimating = false;
        CatalogBack.IsVisible = false;
        FrameworkCatalogPanel.IsVisible = false;
        ResourceList.IsVisible = true;
        DownloadsHeading.Text = "下载中心";

    }
    private async Task LoadCatalogAsync(FrameworkCatalogCommand command, int generation)
    {
        if (catalogBusy || subscribed is not { } shell) return;
        catalogBusy = true;
        LoadMoreButton.IsEnabled = false;
        try
        {
            var result = await shell.Servers.FrameworkCatalogAsync(command, CatalogWorkspace);
            if (generation != catalogGeneration) return;
            catalogResult = result;
            // The installed list is authoritative and re-enumerated by the host: replace it instead of
            // accumulating, otherwise a package deleted outside the app keeps its "downloaded" badge.
            downloadedVersions.Clear();
            foreach (var version in result.InstalledVersions) downloadedVersions.Add(version);
            var offset = VersionScroll.Offset;
            var anchorIndex = Math.Min(shownVersions.Count - 1, (int)(offset.Y / 56));
            var anchor = anchorIndex >= 0 && offset.Y > 0 ? shownVersions[anchorIndex].Tag : null;
            var within = offset.Y - Math.Max(0, anchorIndex) * 56;
            var merged = result.Versions.Concat(shownVersions).DistinctBy(v => v.Tag)
                .OrderByDescending(v => v.PublishedAt).ThenByDescending(v => v.ReleaseId).ToArray();
            var wasEmpty = shownVersions.Count == 0;
            shownVersions.Clear(); shownVersions.AddRange(merged);
            RenderCatalog();
            var firstLoad = wasEmpty && result.CheckedAt is null && result.Error is null;
            VersionsLoading.IsLoading = firstLoad;
            // Progress and results are transient: the toast stack carries them for three seconds
            // instead of leaving a permanent line under the heading.
            if (result.Error is not null)
            {
                Owner?.ShowInstanceNotification("无法获取版本目录", shownVersions.Count > 0 ? "已保留本地缓存，可稍后重试" : "请稍后重试", ToastKind.Warning);
            }

            CatalogEmpty.IsVisible = !firstLoad && shownVersions.Count == 0;
            if (anchor is not null)
            {
                var index = shownVersions.FindIndex(v => v.Tag == anchor);
                if (index >= 0) Dispatcher.UIThread.Post(() =>
                {
                    if (generation == catalogGeneration)
                    {
                        var newOffset = index * 56 + within;
                        var maxOffset = Math.Max(0, VersionScroll.Extent.Height - VersionScroll.Viewport.Height);
                        VersionScroll.Offset = new Vector(offset.X, Math.Min(newOffset, maxOffset));
                    }
                }, DispatcherPriority.Loaded);
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (loadMoreAnimating) return;
                if (shownVersions.Count == 0 || catalogResult?.TotalCached == 0)
                {
                    LoadMoreSurface.IsVisible = false;
                    LoadMoreButton.IsHitTestVisible = false;
                    LoadMoreButton.Opacity = 0;
                }
                else
                {
                    UpdateLoadMoreVisibility();
                }
            }, DispatcherPriority.Loaded);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            if (generation != catalogGeneration) return;
            VersionsLoading.IsLoading = false;
            Owner?.ShowInstanceNotification("无法获取版本目录", "已有缓存不会被清除，可稍后重试", ToastKind.Error);
        }
        finally
        {
            if (generation == catalogGeneration)
            {
                catalogBusy = false;
                LoadMoreButton.IsEnabled = true;
                Dispatcher.UIThread.Post(() => UpdateLoadMoreVisibility(), DispatcherPriority.Loaded);
            }
        }
    }
    private void RenderCatalog()
    {
        VersionRows.Children.Clear();
        foreach (var release in shownVersions)
        {
            var row = new Grid { Height = 56, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 12, 0) };
            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var versionBlock = new MorphBlock { SkeletonWidth = 80, Lines = 1, LineHeight = 20 };
            versionBlock.Content = new TextBlock { Text = release.Version, VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(versionBlock);
            var detailBlock = new MorphBlock { SkeletonWidth = 120, Lines = 1, LineHeight = 16 };
            var detailText = new TextBlock { Text = release.UnavailableReason ?? release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-" };
            detailText.Classes.Add("muted");
            detailBlock.Content = detailText;
            textPanel.Children.Add(detailBlock);
            row.Children.Add(textPanel);
            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (downloadedVersions.Contains(release.Version))
                badges.Children.Add(new Badge
                {
                    Content = "已下载", Tone = BadgeTone.Success, Size = BadgeSize.Small,
                    Icon = new LucideIcon { Kind = LucideIconKind.CircleCheckBig, Size = 12 }
                });
            badges.Children.Add(new Badge { Content = release.Prerelease ? "预发布版" : "正式版", Tone = release.Prerelease ? BadgeTone.Neutral : BadgeTone.Info, Size = BadgeSize.Small });
            Grid.SetColumn(badges, 1); row.Children.Add(badges);
            var current = catalogPresetTarget is not null && release.Version == catalogPresetVersion;
            var canReuse = catalogPresetTarget is not null && downloadedVersions.Contains(release.Version);
            if (current) badges.Children.Add(new Badge { Content = "当前使用", Tone = BadgeTone.Info, Size = BadgeSize.Small });
            var install = new ArcButton
            {
                Variant = ButtonVariant.Ghost, Size = ControlSize.Small, IsPressScaleEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !current,
                Content = new LucideIcon { Kind = current ? LucideIconKind.Check :
                    canReuse ? LucideIconKind.ArrowRight : LucideIconKind.ArrowDownToLine, Size = 16 }
            };
            var action = catalogPresetTarget is null ? "安装" : current ? "当前使用" :
                canReuse ? (catalogPresetVersion is null ? "启用" : "切换到") : catalogPresetVersion is null ? "安装" : "下载并切换到";
            install.SetValue(AutomationProperties.NameProperty, action + " " + release.Version);
            var target = release;
            install.Click += async (_, _) => await ChooseReleaseAsync(target);
            if (catalogPresetTarget is not null && !current)
            {
                textPanel.IsHitTestVisible = badges.IsHitTestVisible = false;
                var hit = new ArcButton { Variant = ButtonVariant.Ghost, CornerRadius = new CornerRadius(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                hit.SetValue(AutomationProperties.NameProperty, "选择 " + release.Version);
                hit.Click += async (_, _) => await ChooseReleaseAsync(target);
                Grid.SetColumnSpan(hit, 2);
                row.Children.Insert(0, hit);
            }
            Grid.SetColumn(install, 2); row.Children.Add(install);
            VersionRows.Children.Add(row);
        }
    }
    private void UpdateLoadMoreVisibility()
    {
        // While the load-more morph animation or its dwell delay is running, scrolling must not
        // re-show or hide the button: the animation owns the surface until it finishes.
        if (loadMoreAnimating) return;
        var hasMore = shownVersions.Count < (catalogResult?.TotalCached ?? 0) || catalogResult?.NextPage is not null;
        var scrollable = VersionScroll.Extent.Height > VersionScroll.Viewport.Height + 1;
        var atBottom = hasMore && scrollable && IsLastRowReached();
        if (atBottom)
        {
            if (!LoadMoreSurface.IsVisible)
            {
                LoadMoreSurface.IsVisible = true;
                LoadMoreButton.Opacity = 0;
                LoadMoreButton.IsHitTestVisible = true;
                Dispatcher.UIThread.Post(() => LoadMoreButton.Opacity = 1, DispatcherPriority.Loaded);
            }
            else
            {
                LoadMoreButton.IsHitTestVisible = true;
                LoadMoreButton.Opacity = 1;
            }
        }
        else
        {
            LoadMoreButton.IsHitTestVisible = false;
            LoadMoreButton.Opacity = 0;
            LoadMoreSurface.IsVisible = false;
        }
    }
    /// <summary>
    /// Measures the last version row against the viewport instead of doing extent arithmetic:
    /// ScrollViewer.Extent can be larger than the offset actually reachable (a row taller than the
    /// real content limit), which made the "reached the bottom" test impossible to satisfy.
    /// </summary>
    private bool IsLastRowReached()
    {
        if (VersionRows.Children.Count == 0 || VersionRows.Children[^1] is not Control last) return false;
        if (last.TranslatePoint(new Point(0, last.Bounds.Height), VersionScroll) is not { } bottom) return false;
        return bottom.Y <= VersionScroll.Viewport.Height + 24;
    }
    private void LoadMoreButton_Click(object? sender, RoutedEventArgs e)
    {
        loadMoreAnimating = true;
        LoadMoreButton.IsHitTestVisible = false;
        LoadMoreGlyph.IsVisible = false;
        LoadMoreLoader.IsVisible = true;
        LoadMoreLoader.Status = TKCode.Arc.MorphLoaderStatus.Loading;
        LoadMoreLabel.Text = "加载中…";
        LoadMoreVersions(sender, e);
    }
    private async void LoadMoreVersions(object? sender, RoutedEventArgs e)
    {
        if (catalogBusy || catalogId is not { } id || catalogResult is not { } result) return;
        var generation = catalogGeneration;
        try
        {
            await LoadCatalogAsync(shownVersions.Count < result.TotalCached
                ? new(id, CacheOnly: true, Offset: shownVersions.Count)
                : new(id, Page: result.NextPage, Offset: shownVersions.Count), generation);
            if (generation != catalogGeneration) return;
            LoadMoreLoader.Status = TKCode.Arc.MorphLoaderStatus.Success;
            LoadMoreLabel.Text = "已加载";
            await Task.Delay(1000);
            if (generation != catalogGeneration) return;
            LoadMoreButton.Opacity = 0;
            await Task.Delay(300);
            if (generation != catalogGeneration) return;
            LoadMoreLoader.IsVisible = false;
            LoadMoreGlyph.IsVisible = true;
            LoadMoreLabel.Text = "加载更多";
            LoadMoreButton.IsHitTestVisible = false;
            FinishLoadMoreAnimation();
        }
        catch
        {
            LoadMoreLoader.Status = TKCode.Arc.MorphLoaderStatus.Error;
            LoadMoreLabel.Text = "加载失败";
            await Task.Delay(1000);
            if (generation != catalogGeneration) return;
            LoadMoreButton.Opacity = 0;
            await Task.Delay(300);
            if (generation != catalogGeneration) return;
            LoadMoreLoader.IsVisible = false;
            LoadMoreGlyph.IsVisible = true;
            LoadMoreLabel.Text = "加载更多";
            LoadMoreButton.IsHitTestVisible = false;
            FinishLoadMoreAnimation();
        }
    }
    /// <summary>
    /// Hands the surface back to the scroll-driven visibility logic once the animation ends.
    /// The evaluation is deferred to a loaded-priority pass: the rows this page added have just been
    /// inserted, and measuring the last row before that pass still sees the pre-growth layout, which
    /// hid the button even when the user was sitting at the new bottom.
    /// </summary>
    private void FinishLoadMoreAnimation()
    {
        loadMoreAnimating = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (loadMoreAnimating) return;
            UpdateLoadMoreVisibility();
        }, DispatcherPriority.Loaded);
    }
}
