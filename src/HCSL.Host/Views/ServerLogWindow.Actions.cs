using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HCSL.Frontend;
using HCSL.UI.Shared.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using ArcSearchField = TKCode.Arc.SearchField;

namespace HCSL.Host.Views;

public sealed partial class ServerLogWindow
{
    internal void NotifyManualPool(string title) =>
        notifications.Show(new ToastOptions { Title = title, Kind = ToastKind.Warning });

    private GlassMenu? consoleMenu;
    private bool showTimestamps = true;
    private int displayLimit = 2000;
    private readonly ArcSearchField findInput = new() { Label = "查找日志", IsLabelVisible = false,
        Placeholder = "查找日志", Size = ControlSize.Small };
    private readonly TextBlock findResult = new() { VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Every match in the displayed rows, the current one, and whether the query is case sensitive.</summary>
    private IReadOnlyList<LogMatch> matches = [];
    private int matchIndex = -1;
    private bool matchCase;

    private Control BuildConsoleToolbar()
    {
        var share = new ExpandingButtonGroupItem { Label = "分享", Icon = Lucide.Geometry(LucideIconKind.Send) };
        var view = new ExpandingButtonGroupItem { Label = "查看", Icon = Lucide.Geometry(LucideIconKind.Eye) };
        var options = new ExpandingButtonGroupItem { Label = "选项", Icon = Lucide.Geometry(LucideIconKind.Settings) };
        // Arc's Medium group: 36 px actions with 16 px icons; share reads first, on the left.
        var group = new ExpandingButtonGroup { Label = "控制台操作", Size = ControlSize.Medium, DefaultExpandedItem = view };
        group.Items.Add(share); group.Items.Add(view); group.Items.Add(options);
        group.Classes.Add("console-actions");        view.Click += (_, _) => OpenViewMenu(view);
        share.Click += (_, _) => OpenShareDialog();
        options.Click += (_, _) => OpenOptionsMenu(options);
        var toolbar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        // Same face as the log: the query is text the console will echo back.
        findInput.FontFamily = HCSL.UI.Shared.Services.MonospaceFont.Resolve();
        // Find sits where the console actions start, the way the instance list keeps search at its top left.
        var search = new ElevatedSurface(findInput)
        {
            Classes = { "console-search" },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        // A find field is a short query, not a page column: keep it near 256 px so the counter, the step buttons and
        // the action group at the right never compete for the same space.
        ArcTokens.Bind(search, Layoutable.MaxWidthProperty, ArcDesignTokenKind.MediaSizeXL);
        ArcTokens.Bind(search, Layoutable.MinWidthProperty, ArcDesignTokenKind.MediaSizeXL);
        ArcTokens.Bind(findResult, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        // The counter answers "is there more?" without a second look at the log, like an editor's find bar.
        // A fixed width keeps the step buttons from jumping when the total grows from "1 / 8" to "1 / 150".
        ArcTokens.Bind(findResult, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeSM);
        findResult.TextAlignment = Avalonia.Media.TextAlignment.Right;
        ArcTokens.Bind(findResult, Layoutable.MinWidthProperty, ArcDesignTokenKind.Space16);
        var previous = FindButton(LucideIconKind.ChevronUp, "上一个匹配", () => StepMatch(-1));
        var next = FindButton(LucideIconKind.ChevronDown, "下一个匹配", () => StepMatch(1));
        // Arc's toggle group in multiple mode is the pressed/not-pressed control Arc offers for this; one option is
        // all a find bar needs. The label names the option because it shows an icon only.
        var caseGroup = new ToggleGroup
        {
            Label = "区分大小写", Mode = ToggleGroupMode.Multiple, Size = ControlSize.Small
        };
        var caseOption = new ToggleGroupOption
        {
            Value = "case", Label = "区分大小写",
            Icon = Lucide.Geometry(LucideIconKind.CaseSensitive)
        };
        caseGroup.Items.Add(caseOption);
        caseGroup.ValueChanged += (_, e) =>
        {
            matchCase = e.IsPressed;
            RecomputeMatches(reset: true);
        };
        var searchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Classes = { "console-searchbar" },
            // Steps next to the field, then the case toggle, and the count last: it grows without pushing the buttons.
            Children = { search, previous, next, caseGroup, findResult }
        };
        ArcTokens.Bind(searchRow, StackPanel.SpacingProperty, ArcDesignTokenKind.Space1);
        toolbar.Children.Add(searchRow);
        Grid.SetColumn(group, 1); toolbar.Children.Add(group);
        findInput.TextChanged += (_, _) => RecomputeMatches(reset: true, jump: false);
        // Tunnel first: the field would otherwise swallow Enter as SearchSubmitted and Escape as a clear.
        findInput.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; StepMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); }
            else if (e.Key == Key.Escape) { e.Handled = true; findInput.Text = ""; text.Focus(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!text.IsEffectivelyVisible) return;
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { e.Handled = true; OpenFind(); }
            else if (e.Key == Key.F3)
            { e.Handled = true; StepMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return toolbar;
    }

    /// <summary>Icon-only find control: Arc's ghost button plus the accessible name the icon cannot carry.</summary>
    private static ArcButton FindButton(LucideIconKind icon, string name, Action clicked)
    {
        var button = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Content = new LucideIcon { Kind = icon } };
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => clicked();
        return button;
    }

    private bool loadingEarlier, historyNoticeShown, limitNoticeShown;
    /// <summary>Delivery receipts for sent commands; off means the console stays quiet about them.</summary>
    private bool showCommandReceipts = true;
    private long nextEarlierLoadAt;

    /// <summary>
    /// Reaching the top asks the controller for older lines. The view keeps its reading row because the
    /// text update path restores the captured row id, so the newly inserted lines appear above it.
    /// </summary>
    private async Task TryLoadEarlierAsync()
    {
        if (closed || loadingEarlier || scroll is null) return;
        if (scroll.Extent.Height <= scroll.Viewport.Height) return;
        if (scroll.Offset.Y > 24) return;
        // One load per pause: without this a short content window fires again on the same gesture.
        if (Environment.TickCount64 < nextEarlierLoadAt) return;
        if (!model.Server.CanLoadEarlier) { NotifyHistoryEnd(); return; }
        // The buffer may already hold more than the chosen display window: the top of the view is then
        // the display limit, not the start of history, so say so instead of reading more lines the
        // window would hide again.
        if (model.Server.LogLineCount > rows.Count) { NotifyDisplayLimit(); return; }
        loadingEarlier = true;
        nextEarlierLoadAt = Environment.TickCount64 + 400;
        try
        {
            // The merge lands on the dispatcher after the await, so only the returned result can
            // tell whether the daemon actually had older lines.
            var result = await model.Server.LoadEarlierAsync();
            if (closed) return;
            if (result == LogBackfillResult.Exhausted) NotifyHistoryEnd();
            else if (result == LogBackfillResult.Failed) notifications.Show(new ToastOptions
            {
                Title = "加载更早的日志失败",
                Description = "当前内容保持不变，稍后可再试",
                Kind = ToastKind.Warning
            });
        }
        finally { loadingEarlier = false; }
    }

    /// <summary>Only states that stopped the view from going further; a normal load stays silent.</summary>
    private void NotifyDisplayLimit()
    {
        if (limitNoticeShown) return;
        limitNoticeShown = true;
        notifications.Show(new ToastOptions
        {
            Title = "已到达显示上限",
            Description = "在查看中调大显示行数即可继续向上查看",
            Kind = ToastKind.Info
        });
    }

    private void NotifyHistoryEnd()
    {
        if (historyNoticeShown) return;
        historyNoticeShown = true;
        notifications.Show(new ToastOptions
        {
            Title = "没有更早的日志",
            Description = "已到达缓冲区起点，完整记录请导出日志文件",
            Kind = ToastKind.Warning
        });
    }

    private void CloseConsoleMenus() { consoleMenu?.Close(); consoleMenu = null; }
    private GlassMenu NewConsoleMenu(string name)
    {
        CloseConsoleMenus();
        var menu = consoleMenu = new GlassMenu();
        AutomationProperties.SetName(menu, name);
        return menu;
    }
    private static void MenuEntry(GlassMenu menu, string label, Action action, bool selected = false, bool enabled = true)
    {
        var item = new GlassMenuItem { Content = label, IsEnabled = enabled,
            Icon = selected ? Lucide.Geometry(LucideIconKind.Check) : null };
        item.Click += (_, _) => { if (!item.IsEnabled) return; menu.Close(); action(); };
        menu.Items.Add(item);
    }
    private void OpenViewMenu(Control anchor)
    {
        var menu = NewConsoleMenu("日志查看");
        foreach (var limit in new[] { 500, 1000, 2000 })
            MenuEntry(menu, "显示最近 " + limit + " 行", () => { displayLimit = limit; limitNoticeShown = false; nextEarlierLoadAt = 0; Update(); }, displayLimit == limit);
        menu.Items.Add(new GlassMenuSeparator());
        MenuEntry(menu, "显示采集时间", () => { showTimestamps = !showTimestamps; Update(); }, showTimestamps);
        MenuEntry(menu, "显示命令回执", () => { showCommandReceipts = !showCommandReceipts; }, showCommandReceipts);
        MenuEntry(menu, "自动跟随", () => { if (following) { following = false; Update(); } else JumpLatest(); }, following);
        MenuEntry(menu, "跳到最新", JumpLatest);
        MenuEntry(menu, "加载更早的日志", async () => await TryLoadEarlierAsync(), enabled: model.Server.CanLoadEarlier);
        menu.Open(anchor);
    }
    private void OpenOptionsMenu(Control anchor)
    {
        var menu = NewConsoleMenu("日志选项");
        MenuEntry(menu, "导出当前显示日志…", async () => await ExportConsoleAsync(), enabled: rows.Count > 0);
        MenuEntry(menu, "清空显示（不删除文件）", () =>
        {
            model.Server.ClearConsoleDisplay(); reading.Reset(); historyNoticeShown = false; limitNoticeShown = false; JumpLatest();
        }, enabled: rows.Count > 0);
        menu.Items.Add(new GlassMenuSeparator());
        MenuEntry(menu, "命令历史（仅内存）", () =>
        {
            model.Server.ConsoleHistoryEnabled = !model.Server.ConsoleHistoryEnabled;
            historyIndex = -1;
        }, model.Server.ConsoleHistoryEnabled);
        MenuEntry(menu, "清空命令历史", () => { model.Server.ClearConsoleHistory(); historyIndex = -1; },
            enabled: model.Server.ConsoleHistory.Count > 0);
        menu.Open(anchor);
    }
    private async Task ExportConsoleAsync()
    {
        var content = text.Text ?? ""; // Snapshot before opening the picker; a run may change while it is open.
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出当前显示日志", SuggestedFileName = "hcsl-console-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log",
                DefaultExtension = "log"
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true))
                await writer.WriteAsync(content);
            if (!closed) notifications.Show(new ToastOptions { Title = "已导出当前显示日志",
                Description = "不包含缓冲区以外的磁盘记录", Kind = ToastKind.Success });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            if (!closed) notifications.Show(new ToastOptions { Title = "日志导出失败",
                Description = "请检查文件权限和保存位置后重试", Kind = ToastKind.Error });
        }
    }
    private void OpenFind()
    {
        Dispatcher.UIThread.Post(() => { if (!closed) { findInput.Focus(); findInput.SelectAll(); } }, DispatcherPriority.Loaded);
    }
    /// <summary>The text the log actually shows for each row, so a match maps to a visible line.</summary>
    private IReadOnlyList<string> LineTexts() =>
        rows.Select(r => showTimestamps ? r.DisplayText : r.Text).ToArray();

    /// <summary>
    /// Scans the rows the log shows. A fresh query starts at the match nearest the reader's viewport instead of the
    /// first match in the file; callers that only refreshed the rows pass <c>jump: false</c>, so a poll never moves them.
    /// </summary>
    private void RecomputeMatches(bool reset = false, bool jump = true)
    {
        var query = findInput.Text;
        matches = LogSearch.Find(LineTexts(), query, matchCase);
        matchIndex = matches.Count == 0 ? -1 : reset ? NearestMatch() : Math.Clamp(matchIndex, 0, matches.Count - 1);
        findResult.Text = query is { Length: > 0 } ? LogSearch.Counter(matches.Count, matchIndex) : "";
        PaintBands();
        if (jump && matchIndex >= 0) JumpToMatch();
    }

    /// <summary>The first match at or below the top visible line, so typing continues where the reader is looking.</summary>
    private int NearestMatch()
    {
        if (matches.Count == 0) return -1;
        if (textPresenter?.TextLayout is not { TextLines.Count: > 0 } layout || scroll is null) return 0;
        var lineHeight = layout.TextLines[0].Height;
        if (!double.IsFinite(lineHeight) || lineHeight <= 0) return 0;
        var topRow = (int)Math.Floor(Math.Max(0, scroll.Offset.Y) / lineHeight);
        for (var i = 0; i < matches.Count; i++)
            if (matches[i].Row >= topRow) return i;
        return 0;
    }

    /// <summary>Moves to the next or previous match, wrapping; the log stops following the tail while reading.</summary>
    private void StepMatch(int step)
    {
        if (matches.Count == 0) { RecomputeMatches(); return; }
        matchIndex = LogSearch.Step(matches.Count, matchIndex, step);
        findResult.Text = LogSearch.Counter(matches.Count, matchIndex);
        PaintBands();
        JumpToMatch();
    }

    /// <summary>Scrolls the current match into view on both axes; NoWrap lines are wider than the viewport.</summary>
    private void JumpToMatch()
    {
        if (matchIndex < 0 || matchIndex >= matches.Count || scroll is null || textPresenter?.TextLayout is not { TextLines.Count: > 0 } layout)
            return;
        var match = matches[matchIndex];
        if (match.Row >= layout.TextLines.Count) return;
        var line = layout.TextLines[match.Row];
        var lineHeight = line.Height;
        if (!double.IsFinite(lineHeight) || lineHeight <= 0) return;
        following = false;
        var targetY = Math.Clamp(match.Row * lineHeight - scroll.Viewport.Height / 2, 0,
            Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        // The measured line gives the exact column offset, so a match far right is centered instead of guessed.
        var start = line.FirstTextSourceIndex + match.Start;
        var end = start + match.Length;
        var matchLeft = line.GetDistanceFromCharacterHit(new Avalonia.Media.CharacterHit(start, 0));
        var matchRight = line.GetDistanceFromCharacterHit(new Avalonia.Media.CharacterHit(end, 0));
        var center = matchLeft + Math.Max(0, matchRight - matchLeft) / 2;
        var targetX = Math.Clamp(center - scroll.Viewport.Width / 2, 0,
            Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width));
        scroll.Offset = new Vector(targetX, targetY);
    }
}
