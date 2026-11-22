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
        NameInput.KeyDown += (_, e) => FieldKey(e, ResetName, () => _ = SaveNameAsync());
        NameInput.LostFocus += (_, _) => _ = SaveNameAsync();
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
        if (!NameInput.IsFocused) NameInput.Text = definition?.Name ?? "";
        if (!PortInput.IsFocused) PortInput.Text = definition?.GamePort.ToString() ?? "";
        PasswordInput.PlaceholderText = entry?.HasPassword == true ? "已设置" : "未设置";
        IdText.Text = entry?.Id.ToString("D") ?? "—";
        CopyIdButton.Value = entry?.Id.ToString("D") ?? "";
        CopyIdButton.IsVisible = entry is not null;
        LayoutText.Text = definition is null ? "—" : $"v{definition.LayoutVersion}";
        var idle = !operating;
        NameInput.IsEnabled = PortInput.IsEnabled = PasswordInput.IsEnabled = idle && m.CanMutate && definition is not null;
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

    private void ResetName()
    {
        NameInput.Text = Model?.Selected?.Entry.Definition?.Name ?? "";
        NameInput.ErrorMessage = null;
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

    /// <summary>Settings save in place, so a validation failure lands on the edited field instead of a dialog.</summary>
    private async Task SaveInstanceAsync(bool nameChanged, string? name = null, int? port = null)
    {
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } original) return;
        var nextName = name ?? original.Name;
        var nextPort = port ?? original.GamePort;
        if (nextName == original.Name && nextPort == original.GamePort) return;
        // Each field owns its message: a failed name must not paint the port row red.
        void Fail(string message)
        {
            if (nameChanged) NameInput.ErrorMessage = message; else PortInput.ErrorMessage = message;
        }
        operating = true;
        try
        {
            var draft = new HostInstanceDraft(nextName, nextPort, original.Network);
            HostServerController.ValidateDraft(draft);
            if (await m.UpdateInstanceAsync(original.Id, original.Revision, draft))
                NameInput.ErrorMessage = PortInput.ErrorMessage = null;
            else Fail(m.Error);
        }
        catch (InstanceException e)
        {
            Fail(e.Message);
            if (!nameChanged) PortInput.Text = original.GamePort.ToString();
            else NameInput.Text = original.Name;
        }
        finally { operating = false; Update(); }
    }

    private async Task SaveNameAsync() => await SaveInstanceAsync(nameChanged: true, name: (NameInput.Text ?? "").Trim());

    private async Task SavePortAsync()
    {
        if (Model?.Selected?.Entry.Definition is not { } definition) return;
        if (!int.TryParse(PortInput.Text, out var port))
        {
            PortInput.ErrorMessage = "端口必须为 1–65535 的整数。";
            PortInput.Text = definition.GamePort.ToString();
            return;
        }
        await SaveInstanceAsync(nameChanged: false, port: port);
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
