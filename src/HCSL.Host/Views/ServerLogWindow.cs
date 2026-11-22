using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using HCSL.Frontend;
using ArcWindow = TKCode.Arc.ArcWindow;
using Avalonia.VisualTree;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace HCSL.Host.Views;

/// <summary>View of the existing bounded, sanitized IPC log ring; no runtime ownership.</summary>
public sealed partial class ServerLogWindow : ArcWindow
{
    private readonly HostServerController model;
    private readonly MainWindow owner;
    private bool hiddenForPrompt;
    private bool positionLostNotified;
    // Arc Textarea keeps a real, virtualized text view: a plain TextBlock reformatted the whole
    // buffer on every poll and made dragging the scrollbar stutter. The frame is flattened by styles.
    private readonly TKCode.Arc.Textarea text = new()
    {
        IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        IsResizable = false, FrameMinHeight = 0, MinHeight = 0, ClipToBounds = true
    };
    private bool following = true, closed, projecting, updateQueued, pinPending, bandsQueued;
    private ScrollViewer? scroll;
    /// <summary>Bands behind the log text marking lines the engine echoed from a written command.</summary>
    private readonly LogBandLayer bands = new() { IsHitTestVisible = false };
    private bool bandsQueuedInContent;
    /// <summary>Measured lines of the log view: bands are placed on the presenter, never on a guessed row height.</summary>
    private Avalonia.Controls.Presenters.TextPresenter? textPresenter;
    private IReadOnlyList<ConsoleLogRow> rows = Array.Empty<ConsoleLogRow>();
    private readonly ConsoleLogViewport reading = new();
    private Guid? displayedRun;
    private readonly TKCode.Arc.Input query = new() { Label = "服务器命令", IsLabelVisible = false,
        PlaceholderText = "输入服务器命令 · Enter 发送 · ↑↓历史" };    private int historyIndex = -1;
    private async Task SendQueryAsync()
    {
        if (displayedRun is not { } run || !model.Server.CanSendConsole) return;
        var text = query.Text ?? "";
        JumpLatest(); // Explicit submission resumes following; ordinary output respects the reader.
        var receipt = await model.Server.SendConsoleAsync(run, text);
        if (closed || displayedRun != run) return;
        if (receipt is null)
        {
            if (showCommandReceipts) notifications.Show(new ToastOptions { Title = "命令未发送",
                Description = model.Server.ConsoleStatus, Kind = ToastKind.Error });
            return;
        }
        if (showCommandReceipts) notifications.Show(receipt.Delivery switch
        {
            HCSL.Core.ConsoleDelivery.Rejected => new ToastOptions { Title = "命令被拒绝，未发送",
                Description = receipt.Code, Kind = ToastKind.Error },
            HCSL.Core.ConsoleDelivery.Written => new ToastOptions { Title = "已写入服务器输入通道",
                Description = "写入不代表执行成功，请以日志输出为准", Kind = ToastKind.Info },
            _ => new ToastOptions { Title = "投递结果未知",
                Description = "未自动重发；请检查日志确认是否生效", Kind = ToastKind.Warning }
        });
        if (receipt.Delivery == HCSL.Core.ConsoleDelivery.Written && query.Text == text) query.Text = "";
        historyIndex = -1;
    }
    public ServerLogWindow(HostServerController model, MainWindow owner)
    {
        this.model = model;
        this.owner = owner;
        InitializeComponent();
        owner.PromptVisibilityChanged += PromptChanged;
        owner.Closed += OwnerClosed;
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = RowDefinitions.Parse("Auto,*,Auto") };
        // Tight rhythm: the toolbar, the log and the command input read as one surface.
        TKCode.Arc.ArcTokens.Bind(grid, Grid.RowSpacingProperty, TKCode.Arc.DesignTokens.ArcDesignTokenKind.Space1);
        grid.Children.Add(BuildConsoleToolbar());
        query.MaxLength = 4096;
        text.Classes.Add("console-log");
        text.FontFamily = HCSL.UI.Shared.Services.MonospaceFont.Resolve();
        // The command input lines up with the log it feeds, character for character.
        query.FontFamily = HCSL.UI.Shared.Services.MonospaceFont.Resolve();
        text.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Avalonia.Input.Key.End) { e.Handled = true; JumpLatest(); } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            scroll = text.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null)
            {
                // Arc's Textarea template hard-codes Disabled here, so a style cannot enable it (a local
                // value does). NoWrap log lines are wider than the viewport and must stay reachable.
                scroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
                // The horizontal bar overlays the viewport's last 12px, so the scrollable text itself
                // needs bottom room; padding on the Textarea only shrinks the frame, not the content.
                if (text.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault() is { } presenter)
                {
                    presenter.Margin = new Thickness(0, 0, 0, 14);
                    textPresenter = presenter;
                }
                // Inside the scroll content, behind the text: the bands then share the text's own coordinate system, so
                // both scroll axes, the frame padding and any line leading are already applied by the ScrollViewer.
                if (scroll.Content is Panel content)
                {
                    if (bands.Parent is Panel previous) previous.Children.Remove(bands);
                    content.Children.Insert(0, bands);
                    bandsQueuedInContent = true;
                }
                // Layout settles after a text change; the bands follow the measured lines, not a stale extent.
                // The repaint is posted (never run inside layout) because the layer draws instead of hosting children.
                scroll.LayoutUpdated += (_, _) =>
                {
                    if (!bandsQueued) { bandsQueued = true; Dispatcher.UIThread.Post(RepaintBands, DispatcherPriority.Loaded); }
                };
                scroll.ScrollChanged += (_, e) =>
                {
                    PaintBands();
                    if (closed || projecting || pinPending || !text.IsEffectivelyVisible) return;
                    // Pure content growth changes Extent only and is not reader intent; a user scroll
                    // (or a layout clamp) always moves the offset or the viewport.
                    if (e.OffsetDelta.Y == 0 && e.ViewportDelta.Y == 0) return;
                    following = IsAtBottom(scroll);
                    if (following) reading.Reset();
                    else _ = TryLoadEarlierAsync();
                };
                // Already at the top, a further wheel turn or a paging key changes no offset, so
                // ScrollChanged never fires; those gestures are the only way to ask for more.
                text.AddHandler(PointerWheelChangedEvent, (_, e) =>
                {
                    if (e.Delta.Y > 0) _ = TryLoadEarlierAsync();
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                text.AddHandler(KeyDownEvent, (_, e) =>
                {
                    if (e.Key is Avalonia.Input.Key.PageUp or Avalonia.Input.Key.Home) _ = TryLoadEarlierAsync();
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                // Layout can finish after the dispatcher posts below; pin again without fighting
                // inertial scrolling (that already cleared `following` through ScrollChanged).
                scroll.LayoutUpdated += (_, _) =>
                {
                    if (!closed && text.IsEffectivelyVisible && following && !projecting && !IsAtBottom(scroll)) ApplyBottom();
                };
            }
            Update();
        };
        // Arc's frame has its own height; stretching the outer control alone does
        // not constrain its internal editor. Size it from a finite, clipped viewport.
        var viewport = new Border { ClipToBounds = true, Child = text };
        viewport.SizeChanged += (_, e) => { text.FrameHeight = Math.Max(0, e.NewSize.Height); PaintBands(); };
        // The echo tint is drawn behind the text, which keeps the Textarea's own virtualization and cross-line
        // selection; a selection can only hold one range, a drawn layer can hold many. The layer is inserted into the
        // scroll content once the template is applied (see Opened).
        TKCode.Arc.ArcTokens.Bind(bands, LogBandLayer.EchoBrushProperty, TKCode.Arc.DesignTokens.ArcDesignTokenKind.AccentSubtle);
        // Fixed yellow for search, independent of the accent: a match must read the same under every theme.
        bands.Bind(LogBandLayer.MatchBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ConsoleSearchMatch"));
        bands.Bind(LogBandLayer.CurrentMatchBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("ConsoleSearchMatchCurrent"));
        bands.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        bands.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        var logLayers = new Grid { ClipToBounds = true };
        logLayers.Children.Add(viewport);
        Grid.SetRow(logLayers, 1); grid.Children.Add(logLayers);
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
        Grid.SetRow(query, 2); grid.Children.Add(query);
        InitializeShell(grid);
        model.PropertyChanged += Changed;
        model.OpenLogs();
        Closed += (_, _) =>
        {
            closed = true; CloseConsoleMenus(); owner.PromptVisibilityChanged -= PromptChanged;
            playerPage.StopClock();
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
    /// <summary>Coalesced repaint after layout has measured the new text.</summary>
    private void ScheduleEchoRepaint()
    {
        if (bandsQueued) return;
        bandsQueued = true;
        Dispatcher.UIThread.Post(RepaintBands, DispatcherPriority.Loaded);
    }

    private void RepaintBands()
    {
        bandsQueued = false;
        if (closed) return;
        PaintBands();
    }

    /// <summary>
    /// Positions the bands behind the log: the engine echo of a written command tints a line, a search match tints only
    /// the matched characters, and the current match is emphasised. Positions come from the measured text layout, so
    /// wrapping, padding or a stale extent cannot shift them; the layer only invalidates visuals.
    /// </summary>
    private void PaintBands()
    {
        var echoRows = new List<int>();
        for (var i = 0; i < rows.Count; i++) if (rows[i].Echo) echoRows.Add(i);
        // Never hide the layer: its size is part of the scroll content, and a visibility change would move the extent.
        if (echoRows.Count == 0 && matches.Count == 0)
        {
            bands.SetBands([], [], [], 0, 0, 0, default);
            return;
        }
        if (textPresenter?.TextLayout is not { TextLines.Count: > 0 } layout || !bandsQueuedInContent)
        {
            bands.SetBands([], [], [], 0, 0, 0, default);
            return;
        }
        var lineHeight = layout.TextLines[0].Height;
        if (!double.IsFinite(lineHeight) || lineHeight <= 0)
        {
            bands.SetBands([], [], [], 0, 0, 0, default);
            return;
        }
        var matchSpans = new List<BandSpan>(matches.Count);
        foreach (var match in matches)
            if (SpanOf(layout, match) is { } span) matchSpans.Add(span);
        var currentSpans = new List<BandSpan>(1);
        if (matchIndex >= 0 && matchIndex < matches.Count && SpanOf(layout, matches[matchIndex]) is { } currentSpan)
            currentSpans.Add(currentSpan);
        // The layer lives inside the scroll content, so it is positioned in text coordinates: no scroll offset at all.
        bands.Width = layout.WidthIncludingTrailingWhitespace;
        bands.Height = layout.Height;
        bands.SetBands(echoRows, matchSpans, currentSpans, lineHeight, 0, 0, new Size(bands.Width, bands.Height));
    }

    /// <summary>The measured horizontal span of one match, so the tint covers the matched characters only.</summary>
    private static BandSpan? SpanOf(Avalonia.Media.TextFormatting.TextLayout layout, LogMatch match)
    {
        if (match.Row < 0 || match.Row >= layout.TextLines.Count) return null;
        var line = layout.TextLines[match.Row];
        var start = line.FirstTextSourceIndex + match.Start;
        var left = line.GetDistanceFromCharacterHit(new Avalonia.Media.CharacterHit(start, 0));
        var right = line.GetDistanceFromCharacterHit(new Avalonia.Media.CharacterHit(start + match.Length, 0));
        var width = right - left;
        if (!double.IsFinite(left) || !double.IsFinite(width) || width <= 0) return null;
        return new BandSpan(match.Row, left, width);
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
            CloseConsoleMenus();
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
        UpdateShellHeader();
        UpdateStopAction();
        UpdateCertificateBadge();
        UpdateStatusBadgeTip();
        SyncShareDialog();
        UpdatePlayersPage();
        if (reading.ReadingPositionLost && !positionLostNotified)
        {
            positionLostNotified = true;
            notifications.Show(new ToastOptions { Title = "较早日志已移出缓冲区",
                Description = "已定位到最早保留的记录，可在查看中选择跳到最新。", Kind = ToastKind.Warning });
        }
        else if (!reading.ReadingPositionLost) positionLostNotified = false;
        if (displayedRun != model.Server.RunId) { displayedRun = model.Server.RunId; text.Text = ""; query.Text = ""; historyIndex = -1; rows = Array.Empty<ConsoleLogRow>(); reading.Reset(); following = true; historyNoticeShown = false; limitNoticeShown = false; }
        // Keep input focused/editable while a command is in flight; only sending is gated.
        query.IsEnabled = model.Server.CanInputConsole;
        if (!text.IsEffectivelyVisible) return;
        var nextRows = model.Server.ConsoleRows.TakeLast(displayLimit).ToArray();
        var next = string.Join(Environment.NewLine, nextRows.Select(r => showTimestamps ? r.DisplayText : r.Text));
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
        pinPending = wasFollowing;
        try { text.Text = next; }
        finally { projecting = false; }
        rows = nextRows;
        PaintBands();
        // New rows can add or remove matches; the reader stays where they are.
        if (!string.IsNullOrEmpty(findInput.Text)) RecomputeMatches(jump: false);
        ScheduleEchoRepaint();
        if (wasFollowing)
        {
            // Extent is stale until the new text is measured: apply now, then again after layout.
            ApplyBottom();
            Dispatcher.UIThread.Post(() =>
            {
                pinPending = false;
                if (!closed && following) ApplyBottom();
            }, DispatcherPriority.Loaded);
            return;
        }
        pinPending = false;
        var restored = reading.Restore(rows);
        if (reading.ReadingPositionLost) reading.Reset();
        Dispatcher.UIThread.Post(() =>
        {
            if (closed || scroll is null || following) return;
            var rowHeight = rows.Count == 0 ? 1 : scroll.Extent.Height / rows.Count;
            var target = (restored + (reading.ReadingPositionLost ? 0 : position % 1)) * rowHeight;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, Math.Min(target, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height))));
        }, DispatcherPriority.Loaded);
    }
}
