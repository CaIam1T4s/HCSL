using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using TKCode.Arc;
using HCSL.Host.ViewModels;
using Avalonia.Layout;
using HCSL.Core;

namespace HCSL.Host.Views;

public partial class MainWindow : ArcWindow
{
    private Bitmap? _background;
    private TaskCompletionSource<int>? prompt;
    private readonly SemaphoreSlim promptGate = new(1, 1);
    private bool windowClosed;
    public event Action<bool>? PromptVisibilityChanged;
    private bool allowExit, closingFlow;
    public MainWindow()
    {
        InitializeComponent();
        InitializeHomeShell();
        Closed += (_, _) => { windowClosed = true; prompt?.TrySetResult(-1); ResetBackground(); };
        Closing += OnClosing;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && prompt is not null) { prompt.TrySetResult(-1); e.Handled = true; }
        };
    }

    public async Task<(bool Confirmed, bool AllowLowMemory)> ConfirmStartAsync(string text)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "启动服务器", FontSize = 20 });
        content.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var consent = new TKCode.Arc.Checkbox { Content = "本次调试跳过低内存确认（3–4 GiB）", IsChecked = false };
        content.Children.Add(consent);
        content.Children.Add(new TextBlock { Text = "仅本次有效；低于 3 GiB 仍禁止启动，控制组限额与 OOM 风险不变。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var result = await ShowOverlayAsync(content, ["取消", "启动"]);
        return (result == 1, result == 1 && consent.IsChecked == true);
    }

    public Task<int> ChooseAsync(string title, string text, params string[] choices)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 20 });
        content.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return ShowOverlayAsync(content, choices);
    }
    public async Task<bool> EditInstanceAsync(InstanceDefinition? original, Func<HCSL.Frontend.HostInstanceDraft, Task<string?>> save)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = original is null ? "创建实例" : "编辑实例", FontSize = 20 });
        var name = new Input { Text = original?.Name ?? "", PlaceholderText = "实例名称（1–80 字符）" };
        var port = new Input { Text = (original?.GamePort ?? 27015).ToString(), PlaceholderText = "游戏端口" };
        var address = new Input { Text = (DataContext as ShellViewModel)?.Servers.ConnectionAddressText is { Length: > 0 } addresses ? addresses : "未知，请刷新实例列表", IsReadOnly = true };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        void Field(string label, Control field) { content.Children.Add(new TextBlock { Text = label }); content.Children.Add(field); }
        Field("实例名称", name); Field("游戏端口", port); Field("本机连接地址（自动识别，只读）", address);
        content.Children.Add(new TextBlock { Text = "本机网卡地址仅供连接参考，不保证可达或限制访问。127.0.0.1 仅供本机客户端使用；访问控制请配置防火墙。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(error);
        while (await ShowOverlayAsync(content, ["取消", "保存"]) == 1)
        {
            try
            {
                if (!int.TryParse(port.Text, out var number)) throw new InstanceException("HCSL-INSTANCE-PORT", "端口必须为 1–65535 的整数。");
                var draft = new HCSL.Frontend.HostInstanceDraft(name.Text ?? "", number);
                HCSL.Frontend.HostServerController.ValidateDraft(draft);
                var failure = await save(draft);
                if (failure is null) return true;
                error.Text = failure;
            }
            catch (InstanceException e) { error.Text = e.Code + ": " + e.Message; }
        }
        return false;
    }
    public async Task<string?> PasswordAsync(string name, bool hasPassword)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "实例入服密码 · " + name, FontSize = 20 });
        content.Children.Add(new TextBlock { Text = $"当前：{(hasPassword ? "已设置" : "无密码")}。旧值不回显；先停服，下次启动生效。空输入不会清除。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var input = new PasswordField { PlaceholderText = "输入新密码" };
        content.Children.Add(input);
        var result = await ShowOverlayAsync(content, ["取消", "设置 / 替换", "清除密码"]);
        var value = input.Text ?? ""; input.Text = "";
        if (result == 2)
            return await ChooseAsync("清除入服密码", "下次启动后不再要求密码。确定清除？", "取消", "清除") == 1 ? "" : null;
        if (result != 1) return null;
        if (value.Length == 0) { await ChooseAsync("未保存", "请输入新密码，或使用清除按钮。", "返回"); return null; }
        try { InstanceRules.Password(value); return value; }
        catch (InstanceException e) { await ChooseAsync("密码无效", e.Message, "返回"); return null; }
    }
    private async Task<int> ShowOverlayAsync(StackPanel content, string[] choices)
    {
        // Async server failures must not be silently discarded while another prompt is open.
        await promptGate.WaitAsync();
        try { return windowClosed ? -1 : await ShowOverlayCoreAsync(content, choices); }
        finally { promptGate.Release(); }
    }
    private async Task<int> ShowOverlayCoreAsync(StackPanel content, string[] choices)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        prompt = completion;
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = new TKCode.Arc.Button { Content = choices[i], Variant = ButtonVariant.Secondary, Margin = new Avalonia.Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => completion.TrySetResult(index);
            buttons.Children.Add(button);
        }
        content.Children.Add(buttons);
        DialogContent.Content = content; DialogOverlay.IsVisible = true; ShellContent.IsEnabled = false;
        // Owned tool windows stay above their owner on many window managers. Hide
        // them while an in-window confirmation is active so it cannot be obscured.
        PromptVisibilityChanged?.Invoke(true);
        Activate();
        buttons.Children.FirstOrDefault()?.Focus();
        try { return await completion.Task; }
        finally
        {
            content.Children.Remove(buttons);
            DialogContent.Content = null; DialogOverlay.IsVisible = false; ShellContent.IsEnabled = true; prompt = null;
            PromptVisibilityChanged?.Invoke(false);
        }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowExit)
        {
            // Auxiliary GUI windows have no server ownership. Close them only
            // after exit/cleanup has been confirmed, not on a cancelled attempt.
            foreach (var child in OwnedWindows.ToArray()) child.Close();
            return;
        }
        e.Cancel = true;
        if (closingFlow || prompt is not null || DataContext is not ShellViewModel model) return;
        closingFlow = true;
        try
        {
            var server = model.Servers.Server;
            var wasActive = server.Active;
            var confirmedRun = server.RunId;
            var choice = await ChooseAsync("退出服主管理界面", wasActive
                ? "服务器仍在运行。仅退出界面不会停服，SteamCMD 任务也会继续。"
                : "退出仅关闭界面，不发送停服命令。宿主不可达时服务器状态未知。SteamCMD 任务继续。",
                wasActive ? ["保持运行并退出", "停服后退出", "取消"] : ["退出界面", "取消"]);
            if (choice == 0) { allowExit = true; Close(); }
            else if (choice == 1 && wasActive)
            {
                if (server.RunId != confirmedRun) { await ChooseAsync("会话已变化", "请核对新的运行会话后重新选择退出方式。", "返回"); return; }
                if (await model.Servers.StopAndWaitAsync(TimeSpan.FromSeconds(30))) { allowExit = true; Close(); }
                else await ChooseAsync("停服未确认，界面保持打开", server.DetailText, "返回");
            }
        }
        catch (Exception) { await ChooseAsync("退出操作失败", "界面保持打开，请检查宿主状态后重试。", "返回"); }
        finally { closingFlow = false; }
    }

    public void SetBackground(Stream stream)
    {
        var image = new Bitmap(stream);
        BackgroundLayer.ImageSource = image;
        _background?.Dispose();
        _background = image;
    }
    public void ResetBackground()
    {
        BackgroundLayer.ImageSource = null;
        _background?.Dispose();
        _background = null;
    }
}
