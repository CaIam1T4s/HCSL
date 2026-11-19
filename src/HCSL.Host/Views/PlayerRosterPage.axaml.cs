using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcTable = TKCode.Arc.Table;

namespace HCSL.Host.Views;

/// <summary>What the roster page asks the console window to do; the page never touches the daemon itself.</summary>
public interface IPlayerRosterActions
{
    Task KickAsync(PlayerRow row);
    Task GrantAsync(PlayerRow row, string group);
    Task RevokeAsync(PlayerRow row);
}

/// <summary>
/// Online roster for the console window. Rows come from the daemon's bounded roster query and the admin badge
/// from that preset's admin list; the layout and every confirmation live in PlayerRosterPage.axaml.
/// </summary>
public sealed partial class PlayerRosterPage : UserControl
{
    private readonly Dictionary<string, string> adminGroupBySteamId = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private GlassMenu? rowMenu;
    private PlayerRow? revokeTarget, grantTarget;

    public PlayerRosterPage()
    {
        InitializeComponent();
        Table.ItemsSource = Rows;
        Table.Width = TableWidth();
        AddColumns();
        // Menu at the pointer, like the instance list: ContextRequested is raised after release, so the
        // pointer position is available for PlacementMode.Pointer.
        Table.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        clock.Tick += (_, _) =>
        {
            if (!IsLive) return;
            foreach (var row in Rows) row.AdvanceSecond();
        };
        clock.Start();
        HoldAction.Confirmed += async (_, _) => await ConfirmHoldAsync();
        HoldPopover.Closed += (_, _) => holdConfirm = null;
    }

    /// <summary>Bound by the console window; the page only drives the UI.</summary>
    public IPlayerRosterActions? Actions { get; set; }

    /// <summary>Rolling server state, set by the window: the one-second clock only runs while a server is up.</summary>
    public bool IsLive { get; set; }

    /// <summary>The window closing ends the local clock with it.</summary>
    public void StopClock() => clock.Stop();

    public ObservableCollection<PlayerRow> Rows { get; } = [];

    private Func<Task>? holdConfirm;

    public void ShowReady()
    {
        Table.IsLoading = false;
        Table.EmptyMessage = "暂无玩家";
    }

    public void ShowLoading()
    {
        Table.IsLoading = Rows.Count == 0;
    }

    /// <summary>No server to read: the table says why instead of showing an empty roster as a result.</summary>
    public void ShowUnavailable(string message)
    {
        Table.IsLoading = false;
        Table.EmptyMessage = message;
        Rows.Clear();
    }

    /// <summary>Admin list driving the identity badge and the group picker; rebuilt by the window on refresh.</summary>
    public void SetAdminGroups(IReadOnlyCollection<string> groups, IReadOnlyDictionary<string, string> bySteamId)
    {
        GrantGroup.ItemsSource = groups;
        adminGroupBySteamId.Clear();
        foreach (var pair in bySteamId) adminGroupBySteamId[pair.Key] = pair.Value;
        foreach (var row in Rows) ApplyAdminGroup(row);
        GrantDialog.IsOpen = false;
    }

    public void ApplyAdminGroup(PlayerRow row) =>
        row.SetAdminGroup(row.SteamId is { } id && adminGroupBySteamId.TryGetValue(id, out var group) ? group : null);

    /// <summary>Every column is a row-bound template so the player is reachable from any cell's DataContext.</summary>
    private void AddColumns()
    {
        Table.Columns.Add(new DataTableColumn("玩家", row => ((PlayerRow)row).Name)
        {
            Width = ColumnWidth("ConsoleTablePlayerWidth"),
            CellTemplate = CellTemplate("PlayerNameCell")
        });
        Table.Columns.Add(new DataTableColumn("身份", row => ((PlayerRow)row).Identity)
        {
            Width = ColumnWidth("ConsoleTableIdentityWidth"),
            CellTemplate = CellTemplate("PlayerIdentityCell")
        });
        Table.Columns.Add(new DataTableColumn("SteamID", row => ((PlayerRow)row).SteamIdText)
        {
            Width = ColumnWidth("ConsoleTableSteamIdWidth"),
            CellTemplate = CellTemplate("PlayerSteamIdCell")
        });
        Table.Columns.Add(new DataTableColumn("地址", row => ((PlayerRow)row).Address)
        {
            Width = ColumnWidth("ConsoleTableAddressWidth"),
            CellTemplate = CellTemplate("PlayerAddressCell")
        });
        Table.Columns.Add(new DataTableColumn("状态", row => ((PlayerRow)row).State)
        {
            Width = ColumnWidth("ConsoleTableStateWidth"),
            CellTemplate = CellTemplate("PlayerStateCell")
        });
        // Numeric value for the sort, text for display: IsNumeric stays off so the header and cells both left-align.
        Table.Columns.Add(new DataTableColumn("延迟", row => ((PlayerRow)row).Ping)
        {
            Width = ColumnWidth("ConsoleTablePingWidth"),
            IsNumeric = false,
            CellTemplate = CellTemplate("PlayerPingCell")
        });
        Table.Columns.Add(new DataTableColumn("在线时长", row => ((PlayerRow)row).Connected)
        {
            Width = ColumnWidth("ConsoleTableConnectedWidth"),
            CellTemplate = CellTemplate("PlayerConnectedCell")
        });
    }

    private IDataTemplate? CellTemplate(string key) => this.FindResource(key) as IDataTemplate;

    /// <summary>Row actions live in a context menu so the table needs no action column. BOT rows get none.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Cell text is not hit-testable everywhere, so the row is found by position rather than by the event source.
        var point = e.TryGetPosition(Table, out var position) ? position : default;
        // Row containers have no data context, so the pointer is mapped to the container band first and the
        // player is then read from any cell that lives inside it.
        double? bandTop = null, bandBottom = null;
        foreach (var candidate in Table.GetVisualDescendants().OfType<Control>())
        {
            if (candidate.GetType().Name != "TableRow") continue;
            if (candidate.TranslatePoint(default, Table) is not { } origin) continue;
            var bottom = origin.Y + candidate.Bounds.Height;
            if (point.Y >= origin.Y && point.Y < bottom) { bandTop = origin.Y; bandBottom = bottom; break; }
        }
        PlayerRow? row = null;
        Control? rowControl = null;
        foreach (var candidate in Table.GetVisualDescendants().OfType<Control>())
        {
            if (candidate.DataContext is not PlayerRow found) continue;
            if (candidate.TranslatePoint(default, Table) is not { } origin) continue;
            var center = origin.Y + candidate.Bounds.Height / 2;
            var insideBand = bandTop is { } top && bandBottom is { } bottom
                ? center >= top && center < bottom
                : new Rect(origin, candidate.Bounds.Size).Contains(point);
            if (!insideBand) continue;
            row = found;
            rowControl = candidate;
            break;
        }
        if (rowControl is null || row is null || row.IsBot) return;
        e.Handled = true;
        rowMenu?.Close();
        var menu = rowMenu = new GlassMenu
        {
            Name = "PlayerRowMenu",
            // Same rule as the instance list: at the pointer when we know it, otherwise aligned to the row.
            Placement = e.TryGetPosition(rowControl, out _) ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight
        };
        AutomationProperties.SetName(menu, "玩家操作");
        // The confirmations anchor where the menu opened, so they appear at the menu, not at the row. The origin
        // is read when a command is clicked: by then the popup is placed, whereas LayoutUpdated can fire first.
        Point? menuOrigin = null;
        void CaptureOrigin()
        {
            if (menu.IsAttachedToVisualTree())
                menuOrigin = AnchorLayer.PointToClient(menu.PointToScreen(default));
        }
        menu.LayoutUpdated += (_, _) => { if (menu.IsOpen) CaptureOrigin(); };
        void Entry(string title, LucideIconKind icon, Action action, bool danger = false)
        {
            var item = new GlassMenuItem { Content = title, Icon = Lucide.Geometry(icon), IsDestructive = danger };
            item.Click += (_, _) => { CaptureOrigin(); menu.Close(); action(); };
            menu.Items.Add(item);
        }
        Entry("踢出玩家", LucideIconKind.UserRoundMinus,
            () => ShowHold(menuOrigin, rowControl, "长按踢出", "已请求踢出", LucideIconKind.UserRoundMinus,
                () => Actions?.KickAsync(row) ?? Task.CompletedTask), danger: true);
        Entry(row.IsAdmin ? "移除管理员" : "授予管理员",
            row.IsAdmin ? LucideIconKind.ShieldMinus : LucideIconKind.ShieldPlus,
            () =>
            {
                if (row.IsAdmin)
                    ShowHold(menuOrigin, rowControl, "长按移除管理员", "已移除", LucideIconKind.ShieldMinus,
                        () => RevokeAdminAsync(row));
                else OpenGrant(row);
            });
        menu.Open(rowControl);
    }

    /// <summary>
    /// The one destructive confirmation. It reuses the declared popover and only moves the anchor, the way the
    /// instance list does; the hold button is reset for every open so it never shows an old done state.
    /// </summary>
    private void ShowHold(Point? menuOrigin, Control container, string label, string confirmedLabel,
        LucideIconKind icon, Func<Task> confirmed)
    {
        holdConfirm = confirmed;
        HoldAction.Label = label;
        HoldAction.ConfirmedLabel = confirmedLabel;
        HoldAction.Icon = Lucide.Geometry(icon);
        var origin = menuOrigin ?? container.TranslatePoint(default, AnchorLayer) ?? default;
        Canvas.SetLeft(Anchor, origin.X);
        Canvas.SetTop(Anchor, origin.Y - Anchor.Bounds.Height);
        AnchorLayer.UpdateLayout();
        HoldPopover.ShowAt(Anchor);
    }

    private async Task ConfirmHoldAsync()
    {
        var confirmed = holdConfirm;
        holdConfirm = null;
        HoldPopover.IsOpen = false;
        if (confirmed is not null) await confirmed();
    }

    private void OpenGrant(PlayerRow row)
    {
        grantTarget = row;
        GrantPrompt.Text = "为 " + row.Name + " 选择管理组";
        GrantGroup.SelectedItem = null;
        GrantDialog.IsOpen = true;
    }

    private void CloseGrant(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => GrantDialog.IsOpen = false;

    private async void ConfirmGrant(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (grantTarget is not { } row || GrantGroup.SelectedItem is not string group) return;
        GrantDialog.IsOpen = false;
        if (Actions is { } actions) await actions.GrantAsync(row, group);
    }

    /// <summary>Icon and label of the revoke confirmation are part of the row's admin state.</summary>
    private Task RevokeAdminAsync(PlayerRow row)
    {
        revokeTarget = row;
        return Actions?.RevokeAsync(row) ?? Task.CompletedTask;
    }

    /// <summary>Column widths come from the shared resource dictionary; a missing entry falls back to Auto.</summary>
    private static GridLength ColumnWidth(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is double width
            ? new GridLength(width) : GridLength.Auto;

    /// <summary>Sum of every column, so the table keeps its widths and the page scrolls instead of clipping.</summary>
    private static double TableWidth()
    {
        string[] keys =
        [
            "ConsoleTablePlayerWidth", "ConsoleTableIdentityWidth", "ConsoleTableSteamIdWidth",
            "ConsoleTableAddressWidth", "ConsoleTableStateWidth", "ConsoleTablePingWidth",
            "ConsoleTableConnectedWidth"
        ];
        var total = 0d;
        foreach (var key in keys)
        {
            if (Avalonia.Application.Current?.TryFindResource(key, out var value) != true || value is not double width) return double.NaN;
            total += width;
        }
        return total;
    }
}
