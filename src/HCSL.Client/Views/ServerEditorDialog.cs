using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HCSL.Core;
using HCSL.Frontend;
using ArcButton = TKCode.Arc.Button;
using TextBox = TKCode.Arc.Input;

namespace HCSL.Client.Views;

/// <summary>Input/feedback only. The injected controller performs validation and persistence.</summary>
public sealed class ServerEditorDialog : UserControl
{
    private readonly TaskCompletionSource<bool> completion = new();
    private bool saving;
    public Task<bool> Result => completion.Task;
    public void Cancel() { if (!saving) completion.TrySetResult(false); }
    public ServerEditorDialog(PlayerClientController controller)
    {
        MinWidth = 320;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { Cancel(); e.Handled = true; } };
        var name = new TextBox { PlaceholderText = "例如：我的 CS2 服务器" };
        var host = new TextBox { PlaceholderText = "IP 或域名，不含协议和端口" };
        var port = new TextBox { Text = "27015" };
        var password = new TKCode.Arc.PasswordField { PlaceholderText = "可选；保存到系统密码存储" };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new ArcButton { Content = "创建并保存", Variant = TKCode.Arc.ButtonVariant.Secondary };
        var cancel = new ArcButton { Content = "取消", Variant = TKCode.Arc.ButtonVariant.Secondary };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "新建服务器", FontSize = 22, FontWeight = FontWeight.SemiBold });
        foreach (var (label, field) in new (string, Avalonia.Controls.TextBox)[] { ("名称", name), ("IP / 域名", host), ("端口", port), ("密码（可选）", password) })
        { panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(field); }
        panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        cancel.Click += (_, _) => Cancel();
        save.Click += async (_, _) =>
        {
            if (!int.TryParse(port.Text, out var number))
            { feedback.Text = "端口应为 1..65535 的整数。"; return; }
            saving = true; save.IsEnabled = cancel.IsEnabled = false;
            try
            {
                await controller.SaveAsync(new PlayerServer(Guid.Empty, name.Text ?? "", host.Text ?? "", number), password.Text ?? "");
                if (controller.LastOperationSucceeded)
                { password.Text = ""; saving = false; completion.TrySetResult(true); }
                else feedback.Text = controller.Status;
            }
            catch (Exception)
            { feedback.Text = "保存失败。请检查存储权限或系统密码服务；敏感详情不会输出到终端。"; }
            finally { saving = false; save.IsEnabled = cancel.IsEnabled = true; }
        };
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => name.Focus());
    }
}
