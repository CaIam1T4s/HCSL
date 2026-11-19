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
using ArcSearchField = TKCode.Arc.SearchField;

namespace HCSL.Host.Views;

public sealed partial class ServerLogWindow
{
    private GlassMenu? consoleMenu;
    private bool showTimestamps = true;
    private int displayLimit = 2000, findIndex = -1;
    private readonly ArcSearchField findInput = new() { Label = "查找日志", IsLabelVisible = false,
        Placeholder = "查找日志", Size = ControlSize.Small };
    private readonly TextBlock findResult = new() { VerticalAlignment = VerticalAlignment.Center };

    private Control BuildConsoleToolbar()
    {
        var view = new ExpandingButtonGroupItem { Label = "查看", Icon = Lucide.Geometry(LucideIconKind.Eye) };
        var share = new ExpandingButtonGroupItem { Label = "分享", Icon = Lucide.Geometry(LucideIconKind.Send) };
        var options = new ExpandingButtonGroupItem { Label = "选项", Icon = Lucide.Geometry(LucideIconKind.Settings) };
        var group = new ExpandingButtonGroup { Label = "控制台操作", Size = ControlSize.Small, DefaultExpandedItem = view };
        group.Items.Add(view); group.Items.Add(share); group.Items.Add(options);
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
        ArcTokens.Bind(search, Layoutable.MaxWidthProperty, ArcDesignTokenKind.ContentMaxWidthSM);
        ArcTokens.Bind(search, Layoutable.MinWidthProperty, ArcDesignTokenKind.MediaSizeXL);
        ArcTokens.Bind(findResult, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        var searchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { search, findResult }
        };
        ArcTokens.Bind(searchRow, StackPanel.SpacingProperty, ArcDesignTokenKind.Space2);
        toolbar.Children.Add(searchRow);
        Grid.SetColumn(group, 1); toolbar.Children.Add(group);
        findInput.TextChanged += (_, _) => FindNext(restart: true);
        // Tunnel first: the field would otherwise swallow Enter as SearchSubmitted and Escape as a clear.
        findInput.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); }
            else if (e.Key == Key.Escape) { e.Handled = true; findInput.Text = ""; findIndex = -1; findResult.Text = ""; text.Focus(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!text.IsEffectivelyVisible) return;
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { e.Handled = true; OpenFind(); }
            else if (e.Key == Key.F3)
            { e.Handled = true; FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return toolbar;
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
            model.Server.ClearConsoleDisplay(); reading.Reset(); findIndex = -1; historyNoticeShown = false; limitNoticeShown = false; JumpLatest();
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
    private void RestoreFindSelection(string body)
    {
        if (string.IsNullOrEmpty(findInput.Text) || findIndex < 0) return;
        var needle = findInput.Text ?? "";
        if (needle.Length == 0 || findIndex + needle.Length > body.Length
            || !body.AsSpan(findIndex, needle.Length).Equals(needle.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            findIndex = -1;
            findResult.Text = "匹配位置已变化，请按 Enter 重新查找";
            return;
        }
        text.SelectionStart = findIndex;
        text.SelectionEnd = findIndex + needle.Length;
        text.CaretIndex = findIndex;
    }
    private void FindNext(bool backwards = false, bool restart = false)
    {
        var needle = findInput.Text ?? "";
        var body = text.Text ?? "";
        if (needle.Length == 0 || body.Length == 0) { findIndex = -1; findResult.Text = needle.Length == 0 ? "" : "无匹配"; return; }
        var found = -1;
        if (backwards)
        {
            var start = Math.Min(findIndex - 1, body.Length - 1);
            if (start >= 0) found = body.LastIndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0) found = body.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var start = restart ? 0 : Math.Clamp(findIndex + 1, 0, body.Length);
            found = body.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0 && start > 0) found = body.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        findIndex = found;
        findResult.Text = found < 0 ? "无匹配" : "已定位";
        if (found < 0) return;
        following = false;
        text.SelectionStart = found;
        text.SelectionEnd = found + needle.Length;
        text.CaretIndex = found;
    }
}
