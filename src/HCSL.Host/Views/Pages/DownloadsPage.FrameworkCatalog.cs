using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.UI.Shared.Controls;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

public partial class DownloadsPage
{
    private string? catalogId;
    private int catalogGeneration;
    private bool catalogBusy;
    private FrameworkCatalogResult? catalogResult;
    private readonly List<FrameworkReleaseEntry> shownVersions = [];
    private readonly HashSet<string> downloadedVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> frameworkLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Badge> frameworkUpdates = new(StringComparer.Ordinal);

    private WorkspacePaths? CatalogWorkspace => Model is { SettingsRoot.Length: > 0, DataRoot.Length: > 0, LibraryRoot.Length: > 0 } model
        ? new(model.SettingsRoot, model.DataRoot, model.LibraryRoot) : null;

    private void InitializeFrameworkCatalog()
    {
        VersionScroll.ScrollChanged += (_, e) =>
        {
            if (e.OffsetDelta.Y == 0) return;
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
    private async Task OpenFrameworkCatalogAsync(string id)
    {
        if (held || subscribed is null) return;
        catalogId = id;
        catalogGeneration++;
        catalogBusy = false;
        shownVersions.Clear(); downloadedVersions.Clear(); VersionRows.Children.Clear();
        catalogResult = null;
        VersionScroll.Offset = new Vector(0, 0);
        LoadMoreSurface.IsVisible = false;
        LoadMoreButton.IsHitTestVisible = false;
        LoadMoreButton.Opacity = 0;
        ResourceList.IsVisible = false;
        FrameworkCatalogPanel.IsVisible = true;
        CatalogBack.IsVisible = true;
        DownloadsHeading.Text = FrameworkRules.Find(id)!.DisplayName;
        VersionsLoading.IsLoading = true;
        CatalogStatus.Text = "读取本地缓存…";
        var generation = catalogGeneration;
        await LoadCatalogAsync(new(id, CacheOnly: true), generation);
        if (generation == catalogGeneration && catalogResult?.NeedsRefresh == true)
            await LoadCatalogAsync(new(id, Refresh: true), generation);
        Dispatcher.UIThread.Post(() => UpdateLoadMoreVisibility(), DispatcherPriority.Loaded + 1);
    }
    private void CloseFrameworkCatalog(object? sender, RoutedEventArgs e)
    {
        catalogGeneration++;
        catalogId = null;
        catalogBusy = false;
        CatalogBack.IsVisible = false;
        FrameworkCatalogPanel.IsVisible = false;
        ResourceList.IsVisible = true;
        DownloadsHeading.Text = "下载中心";
        _ = RefreshFrameworkRowsAsync();
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
            CatalogStatus.Text = result.Error is not null ? "暂时无法刷新，保留已有缓存。" :
                firstLoad ? "正在获取版本目录…" : shownVersions.Count == 0 ? "暂无可用版本。" : result.FromCache ? "已显示本地缓存。" : "版本目录已更新。";
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
                if (shownVersions.Count == 0 || catalogResult?.TotalCached == 0)
                {
                    LoadMoreSurface.IsVisible = false;
                    LoadMoreButton.IsHitTestVisible = false;
                    LoadMoreButton.Opacity = 0;
                }
                else if (!catalogBusy && LoadMoreLoader.IsVisible)
                {
                    // Don't update visibility while loading more
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
            CatalogStatus.Text = "目录暂不可用，已有缓存不会清除。" + Environment.NewLine + e.Message;
            VersionsLoading.IsLoading = false;
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
            var row = new Grid { Height = 56, ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 12, 0) };
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
                badges.Children.Add(new Badge { Content = "已下载", Tone = BadgeTone.Success, Size = BadgeSize.Small });
            badges.Children.Add(new Badge { Content = release.Prerelease ? "预发布版" : "正式版", Tone = release.Prerelease ? BadgeTone.Neutral : BadgeTone.Info, Size = BadgeSize.Small });
            Grid.SetColumn(badges, 1); row.Children.Add(badges);
            VersionRows.Children.Add(row);
        }
    }
    private void UpdateLoadMoreVisibility()
    {
        var hasMore = shownVersions.Count < (catalogResult?.TotalCached ?? 0) || catalogResult?.NextPage is not null;
        var viewportHeight = VersionScroll.Viewport.Height;
        var extentHeight = VersionScroll.Extent.Height;
        var offsetY = VersionScroll.Offset.Y;
        var maxOffset = Math.Max(0, extentHeight - viewportHeight);
        var atBottom = hasMore && maxOffset > 0 && offsetY >= maxOffset - 20;
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
    private void LoadMoreButton_Click(object? sender, RoutedEventArgs e)
    {
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
        try
        {
            await LoadCatalogAsync(shownVersions.Count < result.TotalCached
                ? new(id, CacheOnly: true, Offset: shownVersions.Count)
                : new(id, Page: result.NextPage, Offset: shownVersions.Count), catalogGeneration);
            LoadMoreLoader.Status = TKCode.Arc.MorphLoaderStatus.Success;
            LoadMoreLabel.Text = "已加载";
            await Task.Delay(1000);
            LoadMoreButton.Opacity = 0;
            await Task.Delay(300);
            LoadMoreLoader.IsVisible = false;
            LoadMoreGlyph.IsVisible = true;
            LoadMoreLabel.Text = "加载更多";
            LoadMoreButton.IsHitTestVisible = false;
            LoadMoreSurface.IsVisible = false;
        }
        catch
        {
            LoadMoreLoader.Status = TKCode.Arc.MorphLoaderStatus.Error;
            LoadMoreLabel.Text = "加载失败";
            await Task.Delay(1000);
            LoadMoreButton.Opacity = 0;
            await Task.Delay(300);
            LoadMoreLoader.IsVisible = false;
            LoadMoreGlyph.IsVisible = true;
            LoadMoreLabel.Text = "加载更多";
            LoadMoreButton.IsHitTestVisible = false;
            LoadMoreSurface.IsVisible = false;
        }
    }
}
