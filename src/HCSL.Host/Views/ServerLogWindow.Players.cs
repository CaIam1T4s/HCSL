using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using HCSL.Core;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcBadge = TKCode.Arc.Badge;
using ArcButton = TKCode.Arc.Button;
using ArcDialog = TKCode.Arc.Dialog;
using ArcSelect = TKCode.Arc.Select;
using ArcTable = TKCode.Arc.Table;
using ArcTooltip = TKCode.Arc.Tooltip;

namespace HCSL.Host.Views;

/// <summary>
/// Online roster for the console window. Rows come from the daemon's bounded roster query and the admin
/// badge from that preset's admin list; the page never reads files or RCON itself.
/// </summary>
public sealed partial class ServerLogWindow
{
    /// <summary>
    /// One table row. Values that change every poll raise PropertyChanged so the table updates in place:
    /// rebuilding the row would reset cells (and their badge icons) on every refresh.
    /// </summary>
    private sealed class PlayerRow : INotifyPropertyChanged
    {
        private string state, connected, identity;
        private string serverConnected;
        private int ping;
        private string? adminGroup;

        public PlayerRow(int userId, string name, bool isBot, string? steamId, string address, string state,
            int ping, string connected)
        {
            UserId = userId; Name = name; IsBot = isBot; SteamId = steamId; Address = address;
            this.state = state; this.ping = ping; this.connected = connected;
            serverConnected = connected;
            identity = isBot ? "BOT" : "游客";
        }

        public int UserId { get; }
        public string Name { get; }
        public bool IsBot { get; }
        public string? SteamId { get; }
        public string Address { get; }
        public string SteamIdText => SteamId ?? "-";
        public string PingText => Ping > 0 ? Ping + " ms" : "-";
        public bool IsAdmin => adminGroup is { Length: > 0 };
        public string? AdminGroup => adminGroup;
        /// <summary>Badge text stays short; the concrete group is shown in the tooltip.</summary>
        public string Identity => IsBot ? "BOT" : IsAdmin ? "管理员" : "游客";
        public string IdentityTooltip => IsAdmin ? "管理组：" + adminGroup : IsBot ? "服务器 BOT" : "非管理员玩家";

        public string State { get => state; set => Set(ref state, value, nameof(State)); }
        public string Connected { get => connected; set => Set(ref connected, value, nameof(Connected)); }
        public int Ping { get => ping; set => Set(ref ping, value, nameof(Ping), nameof(PingText)); }

        public void SetAdminGroup(string? group)
        {
            if (adminGroup == group) return;
            adminGroup = group;
            identity = Identity;
            Raise(nameof(AdminGroup));
            Raise(nameof(IsAdmin));
            Raise(nameof(Identity));
            Raise(nameof(IdentityTooltip));
        }

        public void Update(string state, int ping, string connected)
        {
            State = state;
            Ping = ping;
            // The local one-second tick must survive polls that carry the same server value, otherwise the
            // clock would jump forward and back every five seconds.
            if (connected != serverConnected)
            {
                serverConnected = connected;
                Connected = connected;
            }
        }

        /// <summary>Local tick between roster polls so the session time is not frozen for five seconds.</summary>
        public void AdvanceSecond()
        {
            var parts = Connected.Split(':');
            if (parts.Length is < 2 or > 3) return;
            var seconds = 0L;
            foreach (var part in parts)
            {
                if (!long.TryParse(part, out var value)) return;
                seconds = seconds * 60 + value;
            }
            seconds++;
            Connected = parts.Length == 3
                ? (seconds / 3600).ToString("0") + ":" + (seconds / 60 % 60).ToString("00") + ":" + (seconds % 60).ToString("00")
                : (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }

        private void Set<T>(ref T field, T value, params string[] names)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            foreach (var name in names) Raise(name);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ArcTable playersTable = new()
    {
        Caption = "在线玩家",
        EmptyMessage = "暂无玩家",
        LoadingMessage = "正在读取玩家列表",
        ItemNameOne = "名玩家",
        ItemNameOther = "名玩家"
    };
    private readonly ObservableCollection<PlayerRow> playerRows = [];
    /// <summary>Host for the per-open popovers; they are recreated so each one gets a fresh anchor.</summary>
    private Grid? shellHost;
    /// <summary>Canvas the popovers are anchored through, positioned at the context menu's own origin.</summary>
    private Canvas? popoverLayer;
    private Border? popoverAnchor;
    private readonly ArcDialog grantDialog = new() { Title = "授予管理员", Size = ControlSize.Small, ShowCloseButton = true };
    private readonly ArcSelect grantGroup = new() { Placeholder = "选择管理组" };
    private readonly TextBlock grantPrompt = new() { TextWrapping = TextWrapping.Wrap };
    private PlayerRow? kickTarget, revokeTarget, grantTarget;
    private readonly Dictionary<string, string> adminGroupBySteamId = new(StringComparer.OrdinalIgnoreCase);
    private string[] adminGroups = [];
    private string? adminHash;
    private bool adminRefreshInFlight;
    private long nextAdminRefreshAt;
    private bool rosterFailureNotified;
    private readonly Avalonia.Threading.DispatcherTimer playerClock = new() { Interval = TimeSpan.FromSeconds(1) };

    private Control BuildPlayersPage()
    {
        // Every column uses a template that binds to the row, so a cell anywhere in the row carries the player
        // in its DataContext. Arc's built-in text cells do not, which is why context actions only worked on
        // the templated badge before.
        playersTable.Columns.Add(new DataTableColumn("玩家", row => ((PlayerRow)row).Name)
        {
            Width = ColumnWidth("ConsoleTablePlayerWidth"),
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => Cell("Name"))
        });
        playersTable.Columns.Add(new DataTableColumn("身份", row => ((PlayerRow)row).Identity)
        {
            Width = ColumnWidth("ConsoleTableIdentityWidth"),
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => new IdentityCell())
        });
        playersTable.Columns.Add(new DataTableColumn("SteamID", row => ((PlayerRow)row).SteamIdText)
        {
            Width = ColumnWidth("ConsoleTableSteamIdWidth"),
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => Cell("SteamIdText"))
        });
        playersTable.Columns.Add(new DataTableColumn("地址", row => ((PlayerRow)row).Address)
        {
            Width = ColumnWidth("ConsoleTableAddressWidth"),
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => Cell("Address"))
        });
        playersTable.Columns.Add(new DataTableColumn("状态", row => ((PlayerRow)row).State)
        {
            Width = ColumnWidth("ConsoleTableStateWidth"),
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => new StateCell())
        });
        // Numeric value for the sort, text for display: IsNumeric stays off so the header and cells both left-align.
        playersTable.Columns.Add(new DataTableColumn("延迟", row => ((PlayerRow)row).Ping)
        {
            Width = ColumnWidth("ConsoleTablePingWidth"),
            IsNumeric = false,
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => Cell("PingText"))
        });
        playersTable.Columns.Add(new DataTableColumn("在线时长", row => ((PlayerRow)row).Connected)
        {
            Width = ColumnWidth("ConsoleTableConnectedWidth"),
            CellTemplate = new FuncDataTemplate<PlayerRow>((_, _) => Cell("Connected"))
        });

        grantDialog.Content = BuildGrantBody();
        Grid.SetColumnSpan(grantDialog, 2);
        grantDialog.ZIndex = 21;

        playersTable.ItemsSource = playerRows;
        playerClock.Tick += (_, _) =>
        {
            if (closed || !model.Server.Active) return;
            foreach (var row in playerRows) row.AdvanceSecond();
        };
        playerClock.Start();
        // Both themed borders of the table are hidden by Controls.axaml (`arc|Table.console-players`),
        // so the page frame below is the only outline.
        playersTable.Classes.Add("console-players");
        playersTable.HorizontalAlignment = HorizontalAlignment.Left;
        playersTable.VerticalAlignment = VerticalAlignment.Stretch;
        playersTable.Width = TableWidth();
        // Menu at the pointer, like the instance list: ContextRequested is raised after release, so the
        // pointer position is available for PlacementMode.Pointer.
        playersTable.AddHandler(InputElement.ContextRequestedEvent, OnPlayerContextRequested,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        var scroller = new ScrollViewer
        {
            Content = playersTable,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        // Content is clipped to the same rounded shape, so a sorted column's tint cannot square off the corners.
        var clip = new Border { Child = scroller, ClipToBounds = true };
        ArcTokens.BindCornerRadius(clip, ArcDesignTokenKind.RadiusSurface);
        var frame = new Border { Child = clip, ClipToBounds = true };
        // The outer frame owns the stroke and the corner radius; the table's own frame stays invisible.
        ArcTokens.Bind(frame, Border.BackgroundProperty, ArcDesignTokenKind.Surface);
        ArcTokens.Bind(frame, Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        // Arc's edge token plus 1px: the console frame reads better one pixel heavier than the default rule.
        frame.AttachedToVisualTree += (_, _) =>
        {
            try
            {
                var edge = ArcTokens.Get<Thickness>(frame, ArcDesignTokenKind.EdgeThickness);
                var stroke = new Thickness(edge.Left + 1, edge.Top + 1, edge.Right + 1, edge.Bottom + 1);
                frame.BorderThickness = stroke;
                frame.Padding = stroke;
            }
            catch (InvalidOperationException)
            {
                // Not themed yet: the defaults still draw a frame.
            }
        };
        ArcTokens.BindCornerRadius(frame, ArcDesignTokenKind.RadiusSurface);
        var surface = new Border
        {
            // Same material as the console log: the page background stays the window's own gradient.
            Child = new Grid { Children = { frame } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        surface.Bind(Border.PaddingProperty, new DynamicResourceExtension("ConsolePageInset"));
        return surface;
    }

    private Control BuildGrantBody()
    {
        var cancel = new ArcButton { Content = "取消", Variant = ButtonVariant.Secondary, Size = ControlSize.Small, MinHeight = 32 };
        var confirm = new ArcButton { Content = "确定", Variant = ButtonVariant.Primary, Size = ControlSize.Small, MinHeight = 32 };
        // Arc's Primary variant renders neutral in this theme; the accent comes from a style, applied after
        // the control is themed (a code-side token bind runs too early and is silently ignored).
        confirm.Classes.Add("accent-confirm");
        cancel.Click += (_, _) => grantDialog.IsOpen = false;
        confirm.Click += async (_, _) => { grantDialog.IsOpen = false; await GrantAdminAsync(); };
        // Footer matches the other dialogs: right-aligned cancel plus an accent-coloured confirm.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm }
        };
        ArcTokens.Bind(actions, StackPanel.SpacingProperty, ArcDesignTokenKind.Space2);
        var body = new StackPanel { Children = { grantPrompt, grantGroup, actions } };
        ArcTokens.Bind(body, StackPanel.SpacingProperty, ArcDesignTokenKind.Space3);
        return body;
    }

    /// <summary>Cells are recycled between rows, so they read their value through the row's data context.</summary>
    private static TextBlock Cell(string property) => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        [!TextBlock.TextProperty] = new Binding(property)
    };

    /// <summary>Identity badge: short label plus the real group in an Arc tooltip. The icon is built once.</summary>
    private sealed class IdentityCell : Border
    {
        private readonly ArcBadge badge = new() { Size = BadgeSize.Small };
        private readonly LucideIcon icon = new();
        private PlayerRow? row;

        public IdentityCell()
        {
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Left;
            badge.Icon = icon;
            Child = badge;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (row is not null) row.PropertyChanged -= Changed;
            row = DataContext as PlayerRow;
            if (row is not null) row.PropertyChanged += Changed;
            Apply();
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PlayerRow.Identity) or nameof(PlayerRow.IdentityTooltip) or nameof(PlayerRow.IsAdmin))
                Apply();
        }

        private void Apply()
        {
            if (row is null) return;
            badge.Content = row.Identity;
            badge.Tone = row.IsAdmin ? BadgeTone.Info : BadgeTone.Neutral;
            icon.Kind = row.IsBot ? LucideIconKind.Bot : row.IsAdmin ? LucideIconKind.UserRoundStar : LucideIconKind.User;
            badge.SetValue(ArcTooltip.TipProperty, row.IdentityTooltip);
        }
    }

    /// <summary>Status badge: the engine state with a tone, never colour alone.</summary>
    private sealed class StateCell : Border
    {
        private readonly ArcBadge badge = new() { Size = BadgeSize.Small };
        private PlayerRow? row;

        public StateCell()
        {
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Left;
            Child = badge;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (row is not null) row.PropertyChanged -= Changed;
            row = DataContext as PlayerRow;
            if (row is not null) row.PropertyChanged += Changed;
            Apply();
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PlayerRow.State)) Apply();
        }

        private void Apply()
        {
            if (row is null) return;
            badge.Content = row.State;
            badge.Tone = string.Equals(row.State, "active", StringComparison.OrdinalIgnoreCase) ? BadgeTone.Success : BadgeTone.Info;
        }
    }

    /// <summary>Row actions: kick, and grant or revoke admin. BOT rows get neither.</summary>
    private GlassMenu? playerMenu;

    /// <summary>Row actions live in a context menu so the table needs no action column. BOT rows get none.</summary>
    private void OnPlayerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Cell text is not hit-testable everywhere, so the row is found by position rather than by the event source.
        var point = e.TryGetPosition(playersTable, out var position) ? position : default;
        // Row containers have no data context, so the pointer is mapped to the container band first and the
        // player is then read from any cell that lives inside it.
        double? bandTop = null, bandBottom = null;
        foreach (var candidate in playersTable.GetVisualDescendants().OfType<Control>())
        {
            if (candidate.GetType().Name != "TableRow") continue;
            if (candidate.TranslatePoint(default, playersTable) is not { } origin) continue;
            var bottom = origin.Y + candidate.Bounds.Height;
            if (point.Y >= origin.Y && point.Y < bottom) { bandTop = origin.Y; bandBottom = bottom; break; }
        }
        PlayerRow? row = null;
        Control? rowControl = null;
        foreach (var candidate in playersTable.GetVisualDescendants().OfType<Control>())
        {
            if (candidate.DataContext is not PlayerRow found) continue;
            if (candidate.TranslatePoint(default, playersTable) is not { } origin) continue;
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
        playerMenu?.Close();
        var menu = playerMenu = new GlassMenu
        {
            Name = "PlayerRowMenu",
            // Same rule as the instance list: at the pointer when we know it, otherwise aligned to the row.
            Placement = e.TryGetPosition(rowControl, out _) ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight
        };
        AutomationProperties.SetName(menu, "玩家操作");
        // The popovers anchor where the menu opened, so the confirm appears at the menu, not at the row.
        // The origin is read when the command is clicked: by then the popup is placed (LayoutUpdated alone
        // can fire before placement and would yield the panel's unpositioned origin).
        Point? menuOrigin = null;
        void CaptureOrigin()
        {
            if (menu.IsAttachedToVisualTree() && popoverLayer is not null)
                menuOrigin = popoverLayer.PointToClient(menu.PointToScreen(default));
        }
        menu.LayoutUpdated += (_, _) => { if (menu.IsOpen) CaptureOrigin(); };
        void Entry(string title, LucideIconKind icon, Action action, bool danger = false)
        {
            var item = new GlassMenuItem { Content = title, Icon = Lucide.Geometry(icon), IsDestructive = danger };
            item.Click += (_, _) => { CaptureOrigin(); menu.Close(); action(); };
            menu.Items.Add(item);
        }
        Entry("踢出玩家", LucideIconKind.UserRoundMinus, () => OpenKick(row, rowControl, menuOrigin), danger: true);
        Entry(row.IsAdmin ? "移除管理员" : "授予管理员",
            row.IsAdmin ? LucideIconKind.ShieldMinus : LucideIconKind.ShieldPlus,
            () => OpenAdminAction(row, rowControl, menuOrigin));
        menu.Open(rowControl);
    }

    /// <summary>
    /// A Popover keeps the anchor of its first show, so a fresh instance is created for every open; it is
    /// removed again when it closes. The hold button is new too, which also clears its done state.
    /// </summary>
    private void ShowHoldPopover(Point? menuOrigin, Control container, string label, string confirmedLabel,
        LucideIconKind icon, Func<Task> confirmed)
    {
        if (shellHost is null || popoverLayer is null || popoverAnchor is null) return;
        var hold = new HoldToConfirm
        {
            Label = label,
            ConfirmedLabel = confirmedLabel,
            HoldDuration = TimeSpan.FromSeconds(1),
            Tone = HoldToConfirmTone.Danger,
            Icon = Lucide.Geometry(icon)
        };
        var popover = new Popover
        {
            TriggerMode = PopoverTriggerMode.Manual,
            Placement = PopoverPlacement.Bottom,
            Alignment = PopoverAlignment.Start,
            // Offset 0 like the instance list, so the panel starts exactly at the anchor without a gap.
            Offset = 0,
            // The hold button is the surface: no panel shadow behind it.
            HasShadow = false,
            Content = hold
        };
        popover.Styles.Add(new Style(x => x.OfType<PopoverSurface>())
        {
            // Arc documents this combination for showing content on its own: no material, no border, no
            // minimum width that would pad the pill.
            Setters =
            {
                new Setter(Border.BackgroundProperty, Brushes.Transparent),
                new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                new Setter(Border.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MinWidthProperty, 0d)
            }
        });
        Grid.SetColumnSpan(popover, 2);
        popover.ZIndex = 20;
        popover.Closed += (_, _) => shellHost.Children.Remove(popover);
        hold.Confirmed += async (_, _) => { popover.IsOpen = false; await confirmed(); };
        // Anchor the panel at the menu origin, exactly like the instance list does.
        var origin = menuOrigin ?? container.TranslatePoint(default, popoverLayer) ?? default;
        Canvas.SetLeft(popoverAnchor, origin.X);
        Canvas.SetTop(popoverAnchor, origin.Y - popoverAnchor.Height);
        popoverLayer.UpdateLayout();
        shellHost.Children.Add(popover);
        popover.ShowAt(popoverAnchor);
    }

    private void OpenKick(PlayerRow? row, Control anchor, Point? menuOrigin)
    {
        if (row is null) return;
        kickTarget = row;
        ShowHoldPopover(menuOrigin, anchor, "长按踢出", "已请求踢出",
            LucideIconKind.UserRoundMinus, KickAsync);
    }

    private void OpenAdminAction(PlayerRow? row, Control anchor, Point? menuOrigin)
    {
        if (row is null || row.IsBot) return;
        if (row.IsAdmin)
        {
            revokeTarget = row;
            ShowHoldPopover(menuOrigin, anchor, "长按移除管理员", "已移除",
                LucideIconKind.ShieldMinus, RevokeAdminAsync);
            return;
        }
        grantTarget = row;
        grantPrompt.Text = "为 " + row.Name + " 选择管理组";
        grantGroup.ItemsSource = adminGroups;
        grantGroup.SelectedItem = null;
        _ = RefreshAdminListAsync();
        grantDialog.IsOpen = true;
    }

    private async Task KickAsync()
    {
        if (kickTarget is not { } row) return;
        kickTarget = null;
        if (model.Server.Snapshot is not { InstanceId: { } instance, RunId: { } run })
        {
            notifications.Show(new ToastOptions { Title = "踢出失败", Description = "当前没有可用的运行实例", Kind = ToastKind.Error });
            return;
        }
        try
        {
            await model.KickAsync(instance, run, row.UserId, row.SteamId, "HCSL console");
            // Refresh immediately instead of waiting for the next poll.
            await model.Server.RefreshRosterAsync();
            notifications.Show(new ToastOptions
            {
                Title = "已发送踢出请求",
                Description = row.Name + " · 名单将在下次刷新时更新",
                Kind = ToastKind.Info
            });
        }
        catch (Exception e) when (e is IOException or ServerException or InvalidOperationException)
        {
            notifications.Show(new ToastOptions { Title = "踢出失败", Description = e.Message, Kind = ToastKind.Error });
        }
    }

    private async Task GrantAdminAsync()
    {
        if (grantTarget is not { } row || grantGroup.SelectedItem is not string group) return;
        await ApplyAdminAsync(row, group, remove: false);
    }

    private async Task RevokeAdminAsync()
    {
        if (revokeTarget is not { } row) return;
        await ApplyAdminAsync(row, null, remove: true);
    }

    private async Task ApplyAdminAsync(PlayerRow row, string? group, bool remove)
    {
        if (row.SteamId is not { Length: > 0 } steamId)
        {
            notifications.Show(new ToastOptions { Title = "无法修改身份", Description = "该玩家还没有关联 SteamID", Kind = ToastKind.Warning });
            return;
        }
        if (model.Selected is not { Entry: { Definition: { } definition, Presets: var presets } }
            || presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId) is not { } preset)
        {
            notifications.Show(new ToastOptions { Title = "无法修改身份", Description = "先在实例列表中选择该实例与预设", Kind = ToastKind.Warning });
            return;
        }
        try
        {
            var mutation = remove
                ? new AdminMutation(AdminMutationKind.Remove, steamId)
                : new AdminMutation(AdminMutationKind.Upsert, steamId, row.Name, [group!]);
            var command = new AdminCommand(AdminAction.Apply, definition.Id, preset.Id, adminHash, [mutation]);
            await model.ApplyAdminAsync(command);
            if (remove) adminGroupBySteamId.Remove(steamId);
            else adminGroupBySteamId[steamId] = group!;
            foreach (var existing in playerRows)
                if (string.Equals(existing.SteamId, steamId, StringComparison.OrdinalIgnoreCase))
                    existing.SetAdminGroup(remove ? null : group);
            // Ask the host to re-apply the list now: a plugin that caches admins keeps the old set otherwise.
            try { await model.ReloadAdminsAsync(); }
            catch (Exception e) when (e is IOException or ServerException or InvalidOperationException or UnauthorizedAccessException) { }
            // The file hash changed, so the next write must be planned against the new content.
            await RefreshAdminListAsync();
            notifications.Show(new ToastOptions
            {
                Title = remove ? "已移除管理员" : "已授予管理员",
                Description = row.Name + (remove ? "" : " · " + group),
                Kind = ToastKind.Success
            });
        }
        catch (Exception e) when (e is IOException or ServerException or InvalidOperationException or UnauthorizedAccessException)
        {
            notifications.Show(new ToastOptions { Title = "修改身份失败", Description = e.Message, Kind = ToastKind.Error });
        }
    }

    /// <summary>Rebuilds nothing: rows are matched by identity and only changed fields are pushed.</summary>
    private void UpdatePlayersPage()
    {
        var roster = model.Server.LivePlayerRoster;
        if (!model.Server.Active)
        {
            playersTable.IsLoading = false;
            playersTable.EmptyMessage = "服务器未运行，无法查看玩家";
            playerRows.Clear();
            return;
        }
        playersTable.EmptyMessage = "暂无玩家";
        if (roster is null)
        {
            // No roster yet: keep the last rows and stay quiet. A roster that was never fetched (for example
            // the first moments after reopening the host) is not a failure; only a failed query is.
            playersTable.IsLoading = playerRows.Count == 0;
            if (model.Server.RosterFailed && !rosterFailureNotified)
            {
                rosterFailureNotified = true;
                notifications.Show(new ToastOptions
                {
                    Title = "无法读取玩家名单",
                    Description = "保留上次结果；服务器仍在运行时可稍后重试",
                    Kind = ToastKind.Warning
                });
            }
            return;
        }
        rosterFailureNotified = false;
        playersTable.IsLoading = false;
        if (Environment.TickCount64 >= nextAdminRefreshAt)
        {
            nextAdminRefreshAt = Environment.TickCount64 + 30000;
            _ = RefreshAdminListAsync();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in roster.Players)
        {
            var key = RowKey(player.SteamId64, player.UserId);
            seen.Add(key);
            var existing = playerRows.FirstOrDefault(r => RowKey(r.SteamId, r.UserId) == key);
            if (existing is null)
            {
                existing = new PlayerRow(player.UserId, player.Name, player.IsBot, player.SteamId64, player.Address,
                    player.State, player.Ping, player.Connected);
                playerRows.Add(existing);
            }
            else existing.Update(player.State, player.Ping, player.Connected);
            existing.SetAdminGroup(player.SteamId64 is { } id && adminGroupBySteamId.TryGetValue(id, out var group) ? group : null);
        }
        for (var i = playerRows.Count - 1; i >= 0; i--)
            if (!seen.Contains(RowKey(playerRows[i].SteamId, playerRows[i].UserId))) playerRows.RemoveAt(i);
    }

    private static string RowKey(string? steamId, int userId) =>
        steamId is { Length: > 0 } ? "s:" + steamId : "u:" + userId;

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

    /// <summary>Admin groups and the preset hash the write must match; one page is enough to label the roster.</summary>
    private async Task RefreshAdminListAsync()
    {
        if (adminRefreshInFlight || !model.Server.Active) return;
        adminRefreshInFlight = true;
        try
        {
            var result = await model.AdminsAsync(0, 200);
            var snapshot = result.Snapshot;
            adminHash = snapshot.Hash;
            adminGroups = snapshot.Groups.Select(g => g.Name).ToArray();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Entries)
                if (entry.Groups.Length > 0) map[entry.SteamId64] = entry.Groups[0];
            adminGroupBySteamId.Clear();
            foreach (var pair in map) adminGroupBySteamId[pair.Key] = pair.Value;
            foreach (var row in playerRows)
                row.SetAdminGroup(row.SteamId is { } id && adminGroupBySteamId.TryGetValue(id, out var group) ? group : null);
        }
        catch (Exception e) when (e is IOException or ServerException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The roster is still useful without admin labels.
        }
        finally { adminRefreshInFlight = false; }
    }
}
