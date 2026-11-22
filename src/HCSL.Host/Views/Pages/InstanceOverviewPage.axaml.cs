using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend.Dev;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

/// <summary>
/// 概览: the instance's own configuration. Runtime actions (start, stop, map change, logs) stay in the console window and
/// on the home page, so this scope never duplicates them.
/// </summary>
public partial class InstanceOverviewPage : UserControl
{
    private HostServerController? subscribed;
    private bool operating;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public InstanceOverviewPage()
    {
        InitializeComponent();
        DeleteConfirm.Icon = Lucide.Geometry(LucideIconKind.Trash);
        // Settings save in place: Enter commits, Escape restores the stored value, leaving the field commits the edit.
        PortInput.KeyDown += (_, e) => FieldKey(e, ResetPort, () => _ = SavePortAsync());
        PortInput.LostFocus += (_, _) => _ = SavePortAsync();
        PasswordInput.KeyDown += (_, e) => FieldKey(e, ResetPassword, () => _ = SavePasswordAsync());
        PasswordInput.LostFocus += (_, _) => { if ((PasswordInput.Text ?? "").Length > 0) _ = SavePasswordAsync(); };
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Update(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        Update();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = null;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible) Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var entry = m.Selected?.Entry;
        var definition = entry?.Definition;
        var preset = entry?.Presets.FirstOrDefault(p => p.Id == definition?.SelectedPresetId);
        if (!PortInput.IsFocused) PortInput.Text = definition?.GamePort.ToString() ?? "";
        PasswordInput.PlaceholderText = entry?.HasPassword == true ? "已设置" : "未设置";
        CopyIdButton.Value = entry?.Id.ToString("D") ?? "";
        CopyIdButton.IsVisible = entry is not null;
        LayoutText.Text = definition is null ? "—" : $"v{definition.LayoutVersion}";
        var idle = !operating;
        PortInput.IsEnabled = PasswordInput.IsEnabled = idle && m.CanMutate && definition is not null;
        DeployButton.IsEnabled = idle && m.CanDeploy;
        RecoverDeployButton.IsEnabled = idle && m.CanRecoverDeployment;
        MigrateButton.IsVisible = definition is { LayoutVersion: var version } && version < InstanceRules.CurrentLayoutVersion;
        MigrateButton.IsEnabled = idle && m.CanMutate && m.Selected is not null;
        OpenRuntimeButton.IsEnabled = !operating && !m.Busy && m.Server.Connected;
        DeleteConfirm.IsEnabled = idle && definition is not null && m.CanCreate;
    }

    /// <summary>Enter commits the field, Escape puts the stored value back and clears the validation message.</summary>
    private static void FieldKey(KeyEventArgs e, Action reset, Action commit)
    {
        if (e.Key is Key.Enter) { e.Handled = true; commit(); }
        else if (e.Key is Key.Escape) { e.Handled = true; reset(); }
    }

    private void ResetPort()
    {
        PortInput.Text = Model?.Selected?.Entry.Definition?.GamePort.ToString() ?? "";
        PortInput.ErrorMessage = null;
    }

    private void ResetPassword()
    {
        PasswordInput.Text = "";
        PasswordInput.ErrorMessage = null;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        Update();
        try { await action(); }
        catch (Exception) { Error("操作未完成", "请检查宿主状态后重试。"); }
        finally { operating = false; Update(); }
    }

    private async void DeployServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var preset = m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == d.SelectedPresetId);
        var plan = await m.DeployPreviewAsync(d.Id, d.SelectedPresetId);
        if (plan is null) { Error("无法获取部署预览", "宿主未连接或已有操作。"); return; }
        if (await owner.ChooseAsync("部署预览（不启动）", DevFormat.Plan(plan, m.Selected.Entry, preset), "取消", "部署") != 1) return;
        if (m.Selected?.Entry.Id != d.Id) return;
        if (!await m.DeployAsync()) { Error("部署未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error); return; }
        for (var waited = 0; m.Server.Active && waited < 1800; waited++) await Task.Delay(500);
        var done = m.Server.Code == "HCSL-SERVER-DEPLOYED";
        Owner?.ShowInstanceNotification(done ? "部署完成" : "部署未完成", m.Server.DetailText,
            done ? ToastKind.Success : ToastKind.Error);
    });

    private async void RecoverDeployment(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        if (await owner.ChooseAsync("恢复部署", "保留旧运行目录并删除 journal，不会启动 CS2。", "取消", "恢复") != 1) return;
        if (m.Selected?.Entry.Id != d.Id) return;
        if (!await m.RecoverDeploymentAsync()) { Error("恢复未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error); return; }
        for (var waited = 0; m.Server.Active && waited < 1200; waited++) await Task.Delay(500);
        var done = m.Server.Code == "HCSL-SERVER-DEPLOY-RECOVERED";
        Owner?.ShowInstanceNotification(done ? "恢复完成" : "恢复未完成", m.Server.DetailText,
            done ? ToastKind.Success : ToastKind.Error);
    });

    /// <summary>The port saves in place; a validation failure lands on the field instead of a dialog.</summary>
    private async Task SavePortAsync()
    {
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } original) return;
        if (!int.TryParse(PortInput.Text, out var port))
        {
            PortInput.ErrorMessage = "端口必须为 1–65535 的整数。";
            PortInput.Text = original.GamePort.ToString();
            return;
        }
        if (port == original.GamePort) { PortInput.ErrorMessage = null; return; }
        operating = true;
        try
        {
            var draft = new HostInstanceDraft(original.Name, port, original.Network);
            HostServerController.ValidateDraft(draft);
            if (await m.UpdateInstanceAsync(original.Id, original.Revision, draft)) PortInput.ErrorMessage = null;
            else PortInput.ErrorMessage = m.Error;
        }
        catch (InstanceException e) { PortInput.ErrorMessage = e.Message; PortInput.Text = original.GamePort.ToString(); }
        finally { operating = false; Update(); }
    }

    private async Task SavePasswordAsync()
    {
        if (operating || Model is not { } m) return;
        var value = PasswordInput.Text ?? "";
        PasswordInput.Text = "";
        try
        {
            await m.SetPasswordAsync(value);
            Owner?.ShowInstanceNotification(value.Length == 0 ? "已清除入服密码" : "已设置入服密码", null, ToastKind.Success);
        }
        catch (IOException error) { Error("密码未保存", error.Message); }
        finally { Update(); }
    }

    private async void MigrateLayout(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } original) return;
        if (await owner.ChooseAsync("迁移实例布局", $"迁移“{original.Name}”的旧目录布局；旧目录归档，共用安装不变。", "取消", "迁移") != 1) return;
        if (m.Selected?.Entry.Id != original.Id || m.Selected?.Entry.Definition?.Revision != original.Revision) return;
        if (!await m.MigrateLayoutAsync()) Error("迁移失败", m.Error);
        else Owner?.ShowInstanceNotification("布局已迁移", null, ToastKind.Success);
    });

    private async void OpenRuntime(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } instance) return;
        if (!await m.OpenRuntimeDirectoryAsync(instance.Id)) Error("无法打开运行目录", m.Error);
    });

    private async void DeleteInstance(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } target) return;
        if (!m.CanCreate || m.Selected?.Entry.Definition?.Revision != target.Revision)
        {
            DeleteConfirm.IsConfirmed = false;
            Error("删除未提交", "实例或宿主状态已变化，请刷新后重试。");
            return;
        }
        operating = true;
        DeleteConfirm.IsConfirmed = false;
        try
        {
            if (!await m.DeleteInstanceAsync(target.Id, target.Revision)) Error("删除失败", m.Error);
            else Owner?.ShowInstanceNotification("实例已删除", target.Name, ToastKind.Success);
        }
        finally { operating = false; Update(); }
    }
}
