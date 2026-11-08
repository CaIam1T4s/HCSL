using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using ArcButton = TKCode.Arc.Button;
using TextBox = TKCode.Arc.Input;
using CheckBox = TKCode.Arc.Checkbox;
using ListBox = TKCode.Arc.Select;
using HCSL.Core;
using HCSL.Frontend;

namespace HCSL.Client.Views;

/// <summary>Presentation only. Storage, validation and game launch belong to injected controller/services.</summary>
public sealed partial class PlayerWindow : Window
{
    private PlayerClientController controller => (PlayerClientController)DataContext!;
    private readonly ListBox list = new() { Placeholder = "选择已保存的服务器" };
    private readonly TextBox name = new() { PlaceholderText = "服务器名称" };
    private readonly TextBox host = new() { PlaceholderText = "IP 或域名，不含端口/协议" };
    private readonly TextBox port = new() { Text = "27015", PlaceholderText = "端口" };
    private readonly TKCode.Arc.PasswordField password = new() { PlaceholderText = "留空保留已有密码；清除用下方选项" };
    private readonly CheckBox clear = new() { Content = "清除已保存密码" };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox manual = new() { IsReadOnly = true, PlaceholderText = "选择服务器后生成命令；密码服会显示已保存密码" };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock gameHint = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private ArcButton startGame = null!;
    private ArcButton connectGame = null!;
    private readonly Avalonia.Threading.DispatcherTimer gameTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool closed;
    private Guid selected;
    private bool refreshing;
    private long manualGeneration;

    public PlayerWindow(PlayerClientController controller) : this() => DataContext = controller;

    public PlayerWindow()
    {
        InitializeComponent();
        var panel = new StackPanel { Margin = new Thickness(32), Spacing = 12 }; 
        panel.Children.Add(new TextBlock { Text = "服务器列表", FontSize = 24 });
        panel.Children.Add(new TextBlock { Text = "仅校验输入格式，不预先探测服务器。连接请求发出不代表成功入服。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        // Keep failures visible above the form, not below the scroll viewport.
        panel.Children.Add(status);
        panel.Children.Add(list);
        foreach (var control in new Control[] { name, host, port, password, clear }) panel.Children.Add(control);
        Add("新建", async () =>
        {
            if (await ShowEditorAsync())
            {
                selected = controller.Servers.Last().Id;
                Refresh();
                if (Selected is { } server)
                {
                    name.Text = server.Name; host.Text = server.Host; port.Text = server.Port.ToString();
                    password.Text = ""; clear.IsChecked = false;
                    await UpdateManualAsync(server);
                }
            }
        });
        Add("保存", Save);
        Add("删除", async () =>
        {
            if (Selected is not { } server) return;
            // Explicit confirmation: two-step rather than immediate deletion.
            var confirm = new Window { Title = "删除服务器？", Width = 360, Height = 140, CanResize = false };
            var yes = new ArcButton { Content = "确认删除", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            yes.Click += (_, _) => confirm.Close(true); confirm.Content = yes;
            if (await confirm.ShowDialog<bool>(this)) { await controller.DeleteAsync(server.Id); Reset(); }
        });
        startGame = Add("启动 CS2", () => controller.StartGameAsync());
        connectGame = Add("连接服务器", async () =>
        {
            if (Selected is { } server)
                await controller.ConnectAsync(server, confirmGameReady: controller.GameState == HCSL.Application.Abstractions.GameRunState.Unknown);
        });
        Add("复制命令", async () =>
        {
            if (Selected is not { } server) return;
            if (await UpdateManualAsync(server) && Clipboard is { } clipboard)
                await clipboard.SetTextAsync(manual.Text);
        });
        panel.Children.Add(actions);
        panel.Children.Add(gameHint);
        panel.Children.Add(manual);
        panel.Children.Add(new TextBlock { Text = "手动方式：将上方命令粘贴到 CS2 控制台。密码服命令包含明文密码，请勿截图或分享；仅点击“复制命令”时写入剪贴板。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        ServerContent.Content = panel;
        list.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            manualGeneration++; manual.Text = "";
            if (Selected is not { } server) { RefreshGameControls(); return; }
            selected = server.Id; name.Text = server.Name; host.Text = server.Host; port.Text = server.Port.ToString();
            password.Text = ""; clear.IsChecked = false;
            _ = UpdateManualAsync(server);
            RefreshGameControls();
            _ = controller.RefreshGameStateAsync();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PlayerClientController model) model.PropertyChanged += ModelChanged;
        };
        gameTimer.Tick += async (_, _) =>
        {
            if (!closed && DataContext is PlayerClientController model && !model.Busy) await model.RefreshGameStateAsync();
        };
        Closed += (_, _) =>
        {
            closed = true;
            manualGeneration++; manual.Text = "";
            gameTimer.Stop();
            if (DataContext is PlayerClientController model) model.PropertyChanged -= ModelChanged;
        };
        Opened += async (_, _) =>
        {
            if (DataContext is PlayerClientController model)
            {
                await model.LoadAsync(); await model.RefreshGameStateAsync();
                if (!closed) gameTimer.Start();
            }
        };
    }
    private async Task<bool> ShowEditorAsync()
    {
        var previousFocus = FocusManager?.GetFocusedElement();
        var editor = new ServerEditorDialog(controller);
        DialogSurface.Child = editor;
        DialogOverlay.IsVisible = true;
        ShellContent.IsEnabled = false;
        void ClosingWithEditor(object? sender, WindowClosingEventArgs e)
        {
            if (controller.Busy) e.Cancel = true;
            else editor.Cancel();
        }
        Closing += ClosingWithEditor;
        try { return await editor.Result; }
        finally
        {
            Closing -= ClosingWithEditor;
            DialogOverlay.IsVisible = false;
            DialogSurface.Child = null;
            ShellContent.IsEnabled = true;
            previousFocus?.Focus();
        }
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (closed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!closed) Refresh(); });
    }
    private void Navigate(string page)
    {
        HomePage.IsVisible = page == "home";
        ServersPage.IsVisible = page == "servers";
        SettingsPage.IsVisible = page == "settings";
        HomeNav.Classes.Set("selected", page == "home");
        ServersNav.Classes.Set("selected", page == "servers");
        SettingsNav.Classes.Set("selected", page == "settings");
    }
    private void ShowHome(object? sender, RoutedEventArgs e) => Navigate("home");
    private void ShowServers(object? sender, RoutedEventArgs e) => Navigate("servers");
    private void ShowSettings(object? sender, RoutedEventArgs e) => Navigate("settings");
    private void DragTitleBar(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximizeWindow(sender, new RoutedEventArgs());
        else BeginMoveDrag(e);
    }
    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleMaximizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
    private void SystemTheme(object? sender, RoutedEventArgs e) => SetTheme(ThemeVariant.Default);
    private void LightTheme(object? sender, RoutedEventArgs e) => SetTheme(ThemeVariant.Light);
    private void DarkTheme(object? sender, RoutedEventArgs e) => SetTheme(ThemeVariant.Dark);
    private static void SetTheme(ThemeVariant theme)
    {
        TKCode.Arc.ArcTheme.Apply(builder => builder
            .Preset(HCSL.UI.Shared.Styles.Themes.ThemeCatalog.ArcPreset)
            .Appearance(theme == ThemeVariant.Light ? TKCode.Arc.ArcAppearance.Light :
                        theme == ThemeVariant.Dark ? TKCode.Arc.ArcAppearance.Dark : TKCode.Arc.ArcAppearance.System));
        if (Avalonia.Application.Current is { } app) app.RequestedThemeVariant = theme;
    }
    private PlayerServer? Selected => list.SelectedItem as PlayerServer;
    private ArcButton Add(string label, Func<Task> action)
    {
        var button = new ArcButton { Content = label, Variant = TKCode.Arc.ButtonVariant.Secondary };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception) { status.Text = "界面操作失败；请重试。敏感详情未写入日志。"; }
        };
        actions.Children.Add(button);
        return button;
    }
    private async Task<bool> UpdateManualAsync(PlayerServer server)
    {
        var generation = ++manualGeneration;
        manual.Text = "";
        try
        {
            var command = await controller.ManualCommandAsync(server);
            if (closed || generation != manualGeneration || Selected?.Id != server.Id) return false;
            manual.Text = command;
            return true;
        }
        catch (Exception)
        {
            if (!closed && generation == manualGeneration)
                status.Text = "无法读取连接密码，未生成命令。请检查系统凭据存储。";
            return false;
        }
    }
    private void Reset()
    { manualGeneration++; selected = Guid.Empty; name.Text = host.Text = password.Text = manual.Text = ""; port.Text = "27015"; clear.IsChecked = false; }
    private async Task Save()
    {
        if (!int.TryParse(port.Text, out var value)) { status.Text = "端口应为 1..65535 的整数。"; return; }
        var secret = clear.IsChecked == true ? "" : string.IsNullOrEmpty(password.Text) ? null : password.Text;
        await controller.SaveAsync(new(selected, name.Text ?? "", host.Text ?? "", value), secret);
        if (controller.LastOperationSucceeded)
        {
            selected = controller.Servers.Last().Id;
            password.Text = ""; clear.IsChecked = false; Refresh();
            if (Selected is { } saved) await UpdateManualAsync(saved);
        }
    }
    private void Refresh()
    {
        status.Text = controller.Status;
        actions.IsEnabled = !controller.Busy;
        list.IsEnabled = !controller.Busy;
        refreshing = true;
        try
        {
            list.ItemsSource = controller.Servers;
            list.SelectedItem = controller.Servers.FirstOrDefault(x => x.Id == selected);
        }
        finally { refreshing = false; }
        RefreshGameControls();
    }
    private void RefreshGameControls()
    {
        var server = Selected;
        var passwordServer = server?.HasPassword == true;
        startGame.IsVisible = passwordServer && controller.GameState != HCSL.Application.Abstractions.GameRunState.Running;
        gameHint.IsVisible = passwordServer;
        gameHint.Text = controller.GameHint;
        connectGame.IsEnabled = controller.CanConnect(server);
        connectGame.Content = passwordServer && controller.GameState == HCSL.Application.Abstractions.GameRunState.Unknown
            ? "确认游戏已就绪并连接" : "连接服务器";
    }
}
