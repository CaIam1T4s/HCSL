using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using HCSL.Frontend;
using ArcWindow = TKCode.Arc.ArcWindow;
using ArcTitleBar = TKCode.Arc.TitleBar;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace HCSL.Host.Views;

/// <summary>View of the existing bounded, sanitized IPC log ring; no runtime ownership.</summary>
public sealed class ServerLogWindow : ArcWindow
{
    private readonly HostServerController model;
    private readonly MainWindow owner;
    private bool hiddenForPrompt;
    private readonly TextBlock summary = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock detail = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock history = new();
    private readonly TKCode.Arc.Textarea text = new()
    {
        IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        IsResizable = false, FrameMinHeight = 0, MinHeight = 0, ClipToBounds = true
    };
    private readonly TKCode.Arc.Button follow = new() { Content = "暂停跟随", Variant = TKCode.Arc.ButtonVariant.Secondary };
    private bool following = true, closed, projecting, updateQueued;
    private ScrollViewer? scroll;
    private IReadOnlyList<ConsoleLogRow> rows = Array.Empty<ConsoleLogRow>();
    private readonly ConsoleLogViewport reading = new();
    private Guid? displayedRun;
    private readonly TKCode.Arc.Input query = new() { PlaceholderText = "通用 CS2 单行命令；exec / alias / 命令链；↑↓历史" };
    private readonly TKCode.Arc.Button sendQuery = new() { Content = "发送命令" };
    private readonly TextBlock consoleStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private int historyIndex = -1;
    private async Task SendQueryAsync()
    {
        if (displayedRun is not { } run || !model.Server.CanSendConsole) return;
        var text = query.Text ?? "";
        HCSL.Core.ParsedConsoleCommand parsed;
        try { parsed = HCSL.Core.ServerConsoleRules.Parse(text); }
        catch (HCSL.Core.ServerException) { await model.Server.SendConsoleAsync(run, text); return; }
        JumpLatest(); // Explicit submission resumes following; ordinary output respects the reader.
        await model.Server.SendConsoleAsync(run, parsed.Text);
        if (displayedRun == run && query.Text == text) query.Text = "";
        historyIndex = -1;
    }
    public ServerLogWindow(HostServerController model, MainWindow owner)
    {
        this.model = model;
        this.owner = owner;
        owner.PromptVisibilityChanged += PromptChanged;
        owner.Closed += OwnerClosed;
        Title = "HCSL · 启动进度 / 服务器日志"; Width = 960; Height = 620; MinWidth = 600; MinHeight = 400;
        TitleBar = new ArcTitleBar { Height = 44, Title = Title, CaptionButtons = TKCode.Arc.TitleBarCaptionButtons.Auto };
        text.FontFamily = HCSL.UI.Shared.Services.MonospaceFont.Resolve();
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,*,Auto,Auto") };
        grid.Children.Add(summary);
        Grid.SetRow(detail, 1); grid.Children.Add(detail);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8) };
        follow.Content = "跳到最新";
        follow.Click += (_, _) => JumpLatest();
        var remember = new TKCode.Arc.Checkbox { Content = "↑↓命令历史（仅内存）", IsChecked = model.Server.ConsoleHistoryEnabled };
        remember.IsCheckedChanged += (_, _) => { model.Server.ConsoleHistoryEnabled = remember.IsChecked == true; historyIndex = -1; };
        var clear = new TKCode.Arc.Button { Content = "清空历史" };
        clear.Click += (_, _) => { model.Server.ClearConsoleHistory(); historyIndex = -1; };
        tools.Children.Add(follow); tools.Children.Add(remember); tools.Children.Add(clear); tools.Children.Add(history);
        history.MaxWidth = 280; history.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        query.MaxLength = 4096;
        text.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Avalonia.Input.Key.End) { e.Handled = true; JumpLatest(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            scroll = text.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null)
            {
                scroll.ScrollChanged += (_, e) =>
                {
                    if (closed || projecting) return;
                    // Pure content growth changes Extent only and is not reader intent; a user scroll
                    // (or a layout clamp) always moves the offset or the viewport.
                    if (e.OffsetDelta.Y == 0 && e.ViewportDelta.Y == 0) return;
                    following = IsAtBottom(scroll);
                    if (following) reading.Reset();
                };
                // Layout can finish after the dispatcher posts below; pin again without fighting
                // inertial scrolling (that already cleared `following` through ScrollChanged).
                scroll.LayoutUpdated += (_, _) =>
                {
                    if (!closed && following && !projecting && !IsAtBottom(scroll)) ApplyBottom();
                };
            }
            Update();
        };
        Grid.SetRow(tools, 2); grid.Children.Add(tools);
        // Arc's frame has its own height; stretching the outer control alone does
        // not constrain its internal editor. Size it from a finite, clipped viewport.
        var viewport = new Border { ClipToBounds = true, Child = text };
        viewport.SizeChanged += (_, e) => text.FrameHeight = Math.Max(0, e.NewSize.Height);
        text.Classes.Add("server-log");
        Grid.SetRow(viewport, 3); grid.Children.Add(viewport);
        var commandRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(sendQuery, 1); commandRow.Children.Add(query); commandRow.Children.Add(sendQuery);
        sendQuery.Click += async (_, _) => await SendQueryAsync();
        query.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; await SendQueryAsync(); }
            else if (e.Key is Avalonia.Input.Key.Up or Avalonia.Input.Key.Down && model.Server.ConsoleHistory.Count > 0)
            {
                var items = model.Server.ConsoleHistory;
                if (historyIndex < 0) historyIndex = items.Count;
                historyIndex = Math.Clamp(historyIndex + (e.Key == Avalonia.Input.Key.Up ? -1 : 1), 0, items.Count);
                query.Text = historyIndex == items.Count ? "" : items[historyIndex];
                // Arc's Text update may reset the caret after this input event. Set it now for
                // the immediate path and again after the pending text/layout work so a real
                // first recall lands at the end instead of the line start.
                var end = query.Text?.Length ?? 0;
                query.CaretIndex = end;
                query.SelectionStart = query.SelectionEnd = end;
                Dispatcher.UIThread.Post(() =>
                {
                    if (closed) return;
                    query.CaretIndex = end;
                    query.SelectionStart = query.SelectionEnd = end;
                }, DispatcherPriority.Background);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Grid.SetRow(commandRow, 4); grid.Children.Add(commandRow);
        Grid.SetRow(consoleStatus, 5); grid.Children.Add(consoleStatus);
        Content = grid;
        model.PropertyChanged += Changed;
        model.OpenLogs();
        Closed += (_, _) =>
        {
            closed = true; owner.PromptVisibilityChanged -= PromptChanged;
            owner.Closed -= OwnerClosed;
            model.PropertyChanged -= Changed;
            if (!hiddenForPrompt) model.CloseLogs();
            text.Text = "";
        };
        Update();
    }
    private void JumpLatest()
    {
        following = true; reading.Reset();
        ApplyBottom();
        Dispatcher.UIThread.Post(() => { if (!closed && following) ApplyBottom(); }, DispatcherPriority.Loaded);
        Update();
    }
    private static bool IsAtBottom(ScrollViewer viewer) =>
        viewer.Offset.Y >= Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height) - 1;
    /// <summary>Programmatic scroll to the end; ScrollChanged must not read this as user intent.</summary>
    private void ApplyBottom()
    {
        if (scroll is null || IsAtBottom(scroll)) return;
        var wasProjecting = projecting;
        projecting = true;
        try { scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)); }
        finally { projecting = wasProjecting; }
    }
    private void ScheduleUpdate()
    {
        if (updateQueued) return;
        updateQueued = true;
        // Coalesce bursts: a fast log stream must not queue one full rebuild per line.
        Dispatcher.UIThread.Post(() => { updateQueued = false; if (!closed) Update(); }, DispatcherPriority.Background);
    }
    private void OwnerClosed(object? sender, EventArgs e) => Close();
    private void PromptChanged(bool visible)
    {
        if (closed) return;
        if (visible && IsVisible)
        {
            hiddenForPrompt = true;
            model.CloseLogs();
            Hide();
        }
        else if (!visible && hiddenForPrompt)
        {
            hiddenForPrompt = false;
            model.OpenLogs();
            Show(); // Restore the existing owner relationship after Hide().
        }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (!closed) ScheduleUpdate(); }
    private void Update()
    {
        summary.Text = model.Header;
        detail.Text = model.Server.DetailText;
        history.Text = reading.ReadingPositionLost ? "较早日志已移出缓冲区；已定位最早保留记录。可跳到最新。"
            : model.Server.LogsTruncated ? "日志已截断：仅显示最近记录" : "最近记录；非完整磁盘日志";
        if (displayedRun != model.Server.RunId) { displayedRun = model.Server.RunId; text.Text = ""; query.Text = ""; historyIndex = -1; rows = Array.Empty<ConsoleLogRow>(); reading.Reset(); following = true; }
        // Keep input focused/editable while a command is in flight; only sending is gated.
        query.IsEnabled = model.Server.CanInputConsole;
        sendQuery.IsEnabled = model.Server.CanSendConsole;
        consoleStatus.Text = model.Server.ConsoleStatus + "\n↑↓历史仅存本前端内存，可能含秘密；插件回显仍可能进入服务器日志。";
        var nextRows = model.Server.ConsoleRows;
        var next = string.Join(Environment.NewLine, nextRows.Select(r => r.Text));
        if (text.Text == next && rows.Select(r => r.Id).SequenceEqual(nextRows.Select(r => r.Id))) return;
        var wasFollowing = following;
        double position = 0;
        if (!wasFollowing && scroll is not null && rows.Count > 0)
        {
            var rowHeight = Math.Max(1, scroll.Extent.Height / rows.Count);
            position = scroll.Offset.Y / rowHeight;
            reading.Capture(rows, (int)Math.Round(position));
        }
        projecting = true;
        try { text.Text = next; }
        finally { projecting = false; }
        rows = nextRows;
        if (wasFollowing)
        {
            // Extent is stale until the new text is measured: apply now, then again after layout.
            ApplyBottom();
            Dispatcher.UIThread.Post(() => { if (!closed && following) ApplyBottom(); }, DispatcherPriority.Loaded);
            return;
        }
        var restored = reading.Restore(rows);
        if (reading.ReadingPositionLost) history.Text = "较早日志已移出缓冲区；已定位最早保留记录。可跳到最新。";
        Dispatcher.UIThread.Post(() =>
        {
            if (closed || scroll is null || following) return;
            var rowHeight = rows.Count == 0 ? 1 : scroll.Extent.Height / rows.Count;
            var target = (restored + (reading.ReadingPositionLost ? 0 : position % 1)) * rowHeight;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, Math.Min(target, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height))));
        }, DispatcherPriority.Loaded);
    }
}
