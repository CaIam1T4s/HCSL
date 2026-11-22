using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using HCSL.UI.Shared.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views;

public sealed partial class ServerLogWindow
{
    private (string Text, BadgeTone Tone, LucideIconKind Icon)? lastCertificateState;
    private string? lastStatusTip;
    private (string, BadgeTone, LucideIconKind)? lastBadgeState;
    private Vector consoleOffset;
    private MapsPage? mapsPage;
    private readonly List<IDisposable> shellBindings = new();

    /// <summary>
    /// The chrome (sidebar, pages, title badges) is declared in ServerLogWindow.axaml; this only wires the console
    /// page into its host and the lifecycle bindings that follow the owning window.
    /// </summary>
    private void InitializeShell(Control console)
    {
        shellBindings.Add(this.Bind(BackgroundProperty, owner.GetObservable(BackgroundProperty)));
        // The console keeps its own gradient: a wallpaper image chosen for the main window must not follow here.
        shellBindings.Add(this.Bind(WindowBackdrop.StrengthProperty, owner.GetObservable(WindowBackdrop.StrengthProperty)));
        Closed += (_, _) => { foreach (var binding in shellBindings) binding.Dispose(); shellBindings.Clear(); };
        navigation.Background = null;
        // Arc's sidebar icons are geometry, so this one Lucide glyph is resolved here rather than as a control.
        foreach (var item in navigation.Sections.SelectMany(s => s.Items))
            if (item.Key == "maps") item.Icon = Lucide.Geometry(LucideIconKind.Map);
        consoleHost.Content = console;
        playersHost.Content = BuildPlayersPage();
        mapsPage = new MapsPage();
        mapsHost.Content = mapsPage;
        pages.Children.Add(BuildShareDialog());
        if (owner.TitleBar is { } ownerTitleBar)
            shellBindings.Add(ConsoleTitleBar.Bind(BackgroundProperty, ownerTitleBar.GetObservable(BackgroundProperty)));
        SizeChanged += (_, _) => UpdateShellWidth();
        UpdateShellWidth();
        // The stop button must read as one row of the sidebar, so its height follows a realized navigation row rather
        // than a guessed token (Arc enforces its own minimum on HoldToConfirm).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            var row = navigation.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.DataContext is SidebarItem && c.Bounds.Height > 0);
            if (row is null) return;
            var height = row.Bounds.Height;
            StopHold.Height = height;
            StopHold.MinHeight = height;
            StopHold.MaxHeight = height;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The map page reads the selected instance/preset; without one it explains why it is empty.</summary>
    private async Task OpenMapsAsync()
    {
        if (mapsPage is null) return;
        if (model.Selected?.Entry is not { Definition: { } definition, Presets: { } presets } ||
            presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId) is not { } preset)
        {
            notifications.Show(new ToastOptions
            {
                Title = "无法读取地图", Description = "先在实例列表中选择实例与预设", Kind = ToastKind.Warning
            });
            return;
        }
        await mapsPage.AttachAsync(new MapsPageContext(model, definition.Id, preset.Id,
            options => notifications.Show(options)));
    }

    /// <summary>Sidebar route: exactly one page is visible and the console restores the reader's position.</summary>
    private void OnNavigationItemInvoked(object? sender, SidebarItemInvokedEventArgs e)
    {
        CloseConsoleMenus();
        if (consoleHost.IsVisible && scroll is not null) consoleOffset = scroll.Offset;
        consoleHost.IsVisible = e.Item.Key == "console";
        playersHost.IsVisible = e.Item.Key == "players";
        mapsHost.IsVisible = e.Item.Key == "maps";
        if (mapsHost.IsVisible) _ = OpenMapsAsync();
        if (!consoleHost.IsVisible) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            if (scroll is not null && !following) scroll.Offset = consoleOffset;
            Update();
            if (following) ApplyBottom();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<LShapedFrostedChrome>("PART_ShellChrome") is { } chrome)
        {
            chrome.Bind(LShapedFrostedChrome.SidebarWidthProperty, new Binding("Bounds.Width") { Source = navigation });
            chrome.Bind(LShapedFrostedChrome.BlurRadiusProperty, new Binding("DataContext.SidebarBlurRadius") { Source = owner });
            chrome.Bind(LShapedFrostedChrome.TintOpacityProperty, new Binding("DataContext.SidebarTintOpacity") { Source = owner });
        }
    }

    /// <summary>Steam certificate acquisition for the active run; unknown means no evidence yet, not "ready".</summary>
    private void UpdateCertificateBadge()
    {
        var server = model.Server;
        var readiness = server.Snapshot?.NetworkReadiness;
        (string Text, BadgeTone Tone, LucideIconKind Icon)? next = null;
        if (server.Active && readiness is not null)
            next = readiness switch
            {
                { Authentication: SteamAuthenticationState.Ready } => ("证书就绪", BadgeTone.Success, LucideIconKind.CircleCheckBig),
                { Authentication: SteamAuthenticationState.Failed } => ("重试中", BadgeTone.Warning, LucideIconKind.RotateCw),
                { Configuration: SdrConfigurationState.Retrying } or { Delayed: true } => ("重试中", BadgeTone.Warning, LucideIconKind.RotateCw),
                { ClientCertificateRejected: true } => ("重试中", BadgeTone.Warning, LucideIconKind.RotateCw),
                _ => ("获取中", BadgeTone.Info, LucideIconKind.LoaderCircle)
            };
        certificateBadge.IsVisible = next is not null;
        if (next is not { } state || lastCertificateState == (state.Text, state.Tone, state.Icon)) return;
        lastCertificateState = (state.Text, state.Tone, state.Icon);
        certificateBadge.Content = state.Text;
        certificateBadge.Tone = state.Tone;
        // A retry/acquire is ongoing work, so it moves with Arc's own loader instead of a frozen glyph.
        if (state.Tone == BadgeTone.Success)
            certificateBadge.Icon = new LucideIcon { Kind = LucideIconKind.CircleCheckBig };
        else
        {
            var loader = new MorphLoader
            {
                Size = 12, IsDecorative = true, Status = MorphLoaderStatus.Loading,
                Variant = MorphLoaderVariant.Ring
            };
            ArcTokens.Bind(loader, MorphLoader.ForegroundProperty,
                state.Tone == BadgeTone.Warning ? ArcDesignTokenKind.Warning : ArcDesignTokenKind.AccentStrong);
            certificateBadge.Icon = loader;
        }
    }

    /// <summary>Short, single-line reason on the status badge; the full text stays in the log and detail line.</summary>
    private void UpdateStatusBadgeTip()
    {
        var snapshot = model.Server.Snapshot;
        string? tip = null;
        if (snapshot is { State: ServerState.Failed })
        {
            var reason = model.Server.DetailText;
            if (string.IsNullOrWhiteSpace(reason)) reason = snapshot.Code;
            tip = reason.ReplaceLineEndings(" ").Trim();
            if (tip.Length > 60) tip = tip[..60].TrimEnd() + "…";
        }
        if (lastStatusTip == tip) return;
        lastStatusTip = tip;
        if (tip is null) statusBadge.ClearValue(Tooltip.TipProperty);
        else statusBadge.SetValue(Tooltip.TipProperty, tip);
    }

    private void UpdateShellWidth()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        navigation.IsCollapsed = width < 900;
        mapBadge.IsVisible = width >= 900;
        playersBadge.IsVisible = width >= 1080;
        // The rail has no room for a label: it shows a trash icon that opens the same hold in a popover.
        StopHold.IsVisible = !navigation.IsCollapsed;
        StopIcon.IsVisible = navigation.IsCollapsed;
        if (navigation.IsCollapsed) StopPopover.IsOpen = false;
    }

    /// <summary>Stop is offered only while a server runs; the hold says so in its own label instead of a tooltip.</summary>
    private void UpdateStopAction()
    {
        var active = model.Server.Active;
        StopHold.IsEnabled = active;
        StopIcon.IsEnabled = active;
        StopHold.Label = active ? "长按停止服务器" : "服务器未运行";
        StopHoldCompact.Label = StopHold.Label;
        AutomationProperties.SetName(StopIcon, active ? "停止服务器" : "服务器未运行");
    }

    private void OpenStopPopover(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!model.Server.Active) return;
        StopPopover.ShowAt(StopIcon);
    }

    /// <summary>Hold confirmed: report progress through the toast stack, then stop the run that was current.</summary>
    private async void StopConfirmed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopPopover.IsOpen = false;
        if (model.Server.RunId is not { } run || !model.Server.Active)
        {
            notifications.Show(new ToastOptions { Title = "没有可停止的服务器", Kind = ToastKind.Warning });
            return;
        }
        notifications.Show(new ToastOptions { Title = "正在停止服务器", Kind = ToastKind.Info });
        var stopped = await model.Server.StopAsync();
        if (closed || model.Server.RunId != run && model.Server.Active) return;
        notifications.Show(stopped
            ? new ToastOptions { Title = "已请求停止服务器", Kind = ToastKind.Success }
            : new ToastOptions { Title = "停服未提交", Description = model.Server.DetailText, Kind = ToastKind.Error });
    }

    private void UpdateShellHeader()
    {
        var server = model.Server;
        var instance = model.Instances.FirstOrDefault(i => i.Entry.Id == server.Snapshot?.InstanceId);
        var name = instance?.Entry.Definition?.Name;
        Title = string.IsNullOrWhiteSpace(name) ? "服务器控制台" : "服务器控制台 · " + name;
        var next = ServerBadgePresentation.For(server.State, server.Connected, server.Reconnecting);
        if (lastBadgeState != next)
        {
            lastBadgeState = next;
            statusBadge.Content = next.Item1;
            statusBadge.Tone = next.Item2;
            // Reconnecting is ongoing work: Arc's loader carries the motion instead of a frozen glyph.
            statusBadge.Icon = server.Reconnecting
                ? new MorphLoader { Size = 12, Variant = MorphLoaderVariant.Bars, Status = MorphLoaderStatus.Loading, IsDecorative = true }
                : new LucideIcon { Kind = next.Item3 };
        }
        mapBadge.Content = server.Active && server.Snapshot is { MapLoaded: true, MapHistorical: false, Map: { } map } ? map : "地图 -";
        // Frontend refreshes the actual run's roster while the log window is open; stale/failing queries stay unknown.
        var roster = server.LivePlayerRoster;
        playersBadge.Content = roster is not null ? roster.Humans + " 人在线" : "人数 -";
    }
}
