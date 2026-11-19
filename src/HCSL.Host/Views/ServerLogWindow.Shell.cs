using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using HCSL.UI.Shared.Controls;
using Avalonia.Layout;
using HCSL.Core;
using HCSL.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views;

public sealed partial class ServerLogWindow
{
    private readonly Sidebar navigation = new()
    {
        Label = "服务器控制台导航", ExpandedWidth = 216,
        IsCollapseButtonVisible = false, IsFilterVisible = false,
        SelectedKey = "console", BorderThickness = new Thickness(0)
    };
    private readonly Badge statusBadge = new() { Size = BadgeSize.Medium };
    private readonly Badge mapBadge = new() { Tone = BadgeTone.Neutral, Size = BadgeSize.Medium };
    private readonly Badge playersBadge = new() { Tone = BadgeTone.Neutral, Size = BadgeSize.Medium };
    private readonly Badge certificateBadge = new() { Size = BadgeSize.Medium, IsVisible = false };
    private readonly ToastStack notifications = new() { Duration = TimeSpan.FromSeconds(3) };
    private (string Text, BadgeTone Tone, LucideIconKind Icon)? lastCertificateState;
    private string? lastStatusTip;
    private (string, BadgeTone, LucideIconKind)? lastBadgeState;
    private Vector consoleOffset;
    private readonly List<IDisposable> shellBindings = new();

    private Control BuildShell(Control console)
    {
        Classes.Add("wallpaper-shell");
        shellBindings.Add(this.Bind(BackgroundProperty, owner.GetObservable(BackgroundProperty)));
        // The console keeps its own gradient: a wallpaper image chosen for the main window must not follow here.
        shellBindings.Add(this.Bind(WindowBackdrop.StrengthProperty, owner.GetObservable(WindowBackdrop.StrengthProperty)));
        Closed += (_, _) => { foreach (var binding in shellBindings) binding.Dispose(); shellBindings.Clear(); };
        navigation.Background = null;
        navigation.Classes.Add("main-sidebar");
        var section = new SidebarSection();
        section.Items.Add(new SidebarItem { Key = "console", Label = "控制台", Icon = ArcIcons.Terminal });
        section.Items.Add(new SidebarItem { Key = "players", Label = "在线玩家", Icon = ArcIcons.Users });
        section.Items.Add(new SidebarItem { Key = "maps", Label = "地图切换", Icon = Lucide.Geometry(LucideIconKind.Map) });
        navigation.Sections.Add(section);
        var sidebar = new Border { Child = navigation };
        sidebar.Classes.Add("sidebar");

        var players = BuildPlayersPage();
        var maps = Placeholder("地图切换", "地图列表与切换操作将在后续步骤接入。");
        players.IsVisible = maps.IsVisible = false;
        var pages = new Grid { Children = { console, players, maps } };
        navigation.ItemInvoked += (_, e) =>
        {
            CloseConsoleMenus();
            if (console.IsVisible && scroll is not null) consoleOffset = scroll.Offset;
            console.IsVisible = e.Item.Key == "console";
            players.IsVisible = e.Item.Key == "players";
            maps.IsVisible = e.Item.Key == "maps";
            if (console.IsVisible)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (closed) return;
                    if (scroll is not null && !following) scroll.Offset = consoleOffset;
                    Update();
                    if (following) ApplyBottom();
                }, Avalonia.Threading.DispatcherPriority.Loaded);
        };
        var shell = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*") };
        shellHost = shell;
        shell.Children.Add(sidebar);
        Grid.SetColumnSpan(notifications, 2);
        notifications.ZIndex = 10; // Above the content: toasts must not be hidden under the log viewport or the command input.
        shell.Children.Add(notifications);
        shell.Children.Add(grantDialog);
        Grid.SetColumn(pages, 1);
        shell.Children.Add(pages);
        shell.Children.Add(BuildShareDialog());

        var badges = new StackPanel { Orientation = Orientation.Horizontal,
            Children = { statusBadge, mapBadge, playersBadge } };
        ArcTokens.Bind(badges, StackPanel.SpacingProperty, ArcDesignTokenKind.Space2);
        TitleBar = new TitleBar { Height = 44, LeftContent = badges, CaptionButtons = TitleBarCaptionButtons.Auto,
            Icon = new ArcGlyph { Data = ArcIcons.Server, Size = 16 } };
        if (owner.TitleBar is { } ownerTitleBar)
            shellBindings.Add(TitleBar.Bind(BackgroundProperty, ownerTitleBar.GetObservable(BackgroundProperty)));
        SizeChanged += (_, _) => UpdateShellWidth();
        UpdateShellWidth();
        return shell;
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

    private static Control Placeholder(string title, string description)
    {
        var heading = new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        ArcTokens.Bind(heading, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeLG);
        var detail = new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        ArcTokens.Bind(detail, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        var panel = new StackPanel { Children = { heading, detail } };
        ArcTokens.Bind(panel, StackPanel.SpacingProperty, ArcDesignTokenKind.Space2);
        var surface = new Border { Child = panel };
        surface.Classes.Add("console-placeholder");
        return surface;
    }

    private void UpdateShellWidth()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        navigation.IsCollapsed = width < 900;
        mapBadge.IsVisible = width >= 900;
        playersBadge.IsVisible = width >= 1080;
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
