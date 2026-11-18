using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcInput = TKCode.Arc.Input;

namespace HCSL.Host.Views;

public sealed partial class ServerLogWindow
{
    private GlassMenu? consoleMenu;
    private bool showTimestamps = true;
    private int displayLimit = 2000, findIndex = -1;
    private readonly ArcInput findInput = new() { Label = "查找日志", IsLabelVisible = false,
        PlaceholderText = "查找日志 · Enter 下一处 · Shift+Enter 上一处 · Esc 关闭" };
    private readonly TextBlock findResult = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid findBar = new() { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), IsVisible = false };

    private Control BuildConsoleToolbar()
    {
        var view = new ExpandingButtonGroupItem { Label = "查看", Icon = Lucide.Geometry(LucideIconKind.Eye) };
        var options = new ExpandingButtonGroupItem { Label = "选项", Icon = Lucide.Geometry(LucideIconKind.Settings) };
        var group = new ExpandingButtonGroup { Label = "控制台操作", Size = ControlSize.Small, DefaultExpandedItem = view };
        group.Items.Add(view); group.Items.Add(options);
        group.Classes.Add("console-actions");        view.Click += (_, _) => OpenViewMenu(view);
        options.Click += (_, _) => OpenOptionsMenu(options);
        var toolbar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        certificateBadge.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(certificateBadge);
        Grid.SetColumn(group, 1); toolbar.Children.Add(group);
        findBar.Children.Add(findInput);
        Grid.SetColumn(findResult, 1); findBar.Children.Add(findResult);
        ArcTokens.Bind(findBar, Grid.ColumnSpacingProperty, ArcDesignTokenKind.Space2);
        ArcTokens.Bind(findResult, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        findInput.TextChanged += (_, _) => FindNext(restart: true);
        findInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); }
            else if (e.Key == Key.Escape) { e.Handled = true; findBar.IsVisible = false; text.Focus(); }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!text.IsEffectivelyVisible) return;
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { e.Handled = true; OpenFind(); }
            else if (e.Key == Key.F3)
            { e.Handled = true; if (!findBar.IsVisible) OpenFind(); else FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return toolbar;
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
            MenuEntry(menu, "显示最近 " + limit + " 行", () => { displayLimit = limit; Update(); }, displayLimit == limit);
        menu.Items.Add(new GlassMenuSeparator());
        MenuEntry(menu, "显示采集时间", () => { showTimestamps = !showTimestamps; Update(); }, showTimestamps);
        MenuEntry(menu, "自动跟随", () => { if (following) { following = false; Update(); } else JumpLatest(); }, following);
        MenuEntry(menu, "跳到最新", JumpLatest);
        MenuEntry(menu, "查找日志 · Ctrl+F", OpenFind);
        menu.Open(anchor);
    }
    private void OpenOptionsMenu(Control anchor)
    {
        var menu = NewConsoleMenu("日志选项");
        MenuEntry(menu, "导出当前显示日志…", async () => await ExportConsoleAsync(), enabled: rows.Count > 0);
        MenuEntry(menu, "清空显示（不删除文件）", () =>
        {
            model.Server.ClearConsoleDisplay(); reading.Reset(); findIndex = -1; JumpLatest();
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
        findBar.IsVisible = true;
        Dispatcher.UIThread.Post(() => { if (!closed) { findInput.Focus(); findInput.SelectAll(); } }, DispatcherPriority.Loaded);
    }
    private void RestoreFindSelection(string body)
    {
        if (!findBar.IsVisible || findIndex < 0) return;
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
