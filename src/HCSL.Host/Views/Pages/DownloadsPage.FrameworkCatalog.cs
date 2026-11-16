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
        foreach (var framework in FrameworkRules.Known)
        {
            var version = new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center };
            version.Classes.Add("muted");
            frameworkLabels.Add(framework.Id, version);
            var update = new Badge { Content = "更新可用", IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            frameworkUpdates.Add(framework.Id, update);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(16, 0) };
            row.Children.Add(new TextBlock { Text = framework.DisplayName, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(version, 1); row.Children.Add(version);
            Grid.SetColumn(update, 2); row.Children.Add(update);
            var arrow = new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 3); row.Children.Add(arrow);
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
                var stable = page.Versions.Where(v => !v.Prerelease).Select(v => v.Version);
                var latest = FrameworkRules.OrderVersions(framework.Id, stable).FirstOrDefault();
                frameworkUpdates[framework.Id].IsVisible = latest is not null && page.InstalledVersions.FirstOrDefault() is { } installed &&
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
            shownVersions.Clear(); shownVersions.AddRange(merged);
            RenderCatalog();
            var firstLoad = shownVersions.Count == 0 && result.CheckedAt is null && result.Error is null;
            VersionsLoading.IsLoading = firstLoad;
            CatalogStatus.Text = result.Error is not null ? "暂时无法刷新，保留已有缓存。" :
                firstLoad ? "正在获取版本目录…" : shownVersions.Count == 0 ? "暂无可用版本。" : result.FromCache ? "已显示本地缓存。" : "版本目录已更新。";
            if (anchor is not null)
            {
                var index = shownVersions.FindIndex(v => v.Tag == anchor);
                if (index >= 0) Dispatcher.UIThread.Post(() =>
                {
                    if (generation == catalogGeneration) VersionScroll.Offset = new Vector(offset.X, index * 56 + within);
                }, DispatcherPriority.Loaded);
            }
            LoadMoreButton.IsVisible = shownVersions.Count < result.TotalCached || result.NextPage is not null;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            if (generation != catalogGeneration) return;
            VersionsLoading.IsLoading = false;
            CatalogStatus.Text = "目录暂不可用，已有缓存不会清除。" + Environment.NewLine + e.Message;
        }
        finally
        {
            if (generation == catalogGeneration) { catalogBusy = false; LoadMoreButton.IsEnabled = true; }
        }
    }
    private void RenderCatalog()
    {
        VersionRows.Children.Clear();
        foreach (var release in shownVersions)
        {
            var row = new Grid { Height = 56, ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock { Text = release.Version });
            var detail = new TextBlock { Text = release.UnavailableReason ?? release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-" };
            detail.Classes.Add("muted"); text.Children.Add(detail); row.Children.Add(text);
            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            badges.Children.Add(new Badge { Content = release.Prerelease ? "预发布版" : "正式版" });
            if (downloadedVersions.Contains(release.Version)) badges.Children.Add(new Badge { Content = "已下载" });
            Grid.SetColumn(badges, 1); row.Children.Add(badges);
            VersionRows.Children.Add(row);
        }
    }
    private async void LoadMoreVersions(object? sender, RoutedEventArgs e)
    {
        if (catalogBusy || catalogId is not { } id || catalogResult is not { } result) return;
        await LoadCatalogAsync(shownVersions.Count < result.TotalCached
            ? new(id, CacheOnly: true, Offset: shownVersions.Count)
            : new(id, Page: result.NextPage, Offset: shownVersions.Count), catalogGeneration);
    }
}
