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
    // Last password loaded from the daemon for the selected instance; Escape restores it and the poll never rewrites it.
    private Guid? passwordInstance;
    private string storedPassword = "";
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
        Tooltip.SetTip(DeployHelpButton, "当前部署范围待检查");
        DeployHelpButton.PointerEntered += async (_, _) => await RefreshDeployHelpAsync();
        DeployHelpButton.GotFocus += async (_, _) => await RefreshDeployHelpAsync();
        Tooltip.SetTip(RecoverHelpButton, "清理先前部署留下的无用文件，运行目录不受影响");
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Update(); };
    }

    private int deployHelpRequest;
    private readonly SemaphoreSlim deployPreviewGate = new(1, 1);

    private async Task<DeploymentPlan?> GetDeployPreviewAsync(HostServerController model, Guid instance, Guid preset)
    {
        await deployPreviewGate.WaitAsync();
        try
        {
            if (Model != model || model.Selected?.Entry.Definition is not { } current ||
                current.Id != instance || current.SelectedPresetId != preset) return null;
            return await model.DeployPreviewAsync(instance, preset);
        }
        finally { deployPreviewGate.Release(); }
    }

    private async Task RefreshDeployHelpAsync()
    {
        if (operating || deployPreviewGate.CurrentCount == 0) return;
        var request = ++deployHelpRequest;
        Tooltip.SetTip(DeployHelpButton, "当前正在检查部署范围");
        if (Model is not { } model || model.Selected?.Entry.Definition is not { } definition || !model.CanDeploy)
        {
            Tooltip.SetTip(DeployHelpButton, "当前无法检查部署范围");
            return;
        }
        try
        {
            var plan = await GetDeployPreviewAsync(model, definition.Id, definition.SelectedPresetId);
            if (request != deployHelpRequest || Model != model || model.Selected?.Entry.Definition != definition) return;
            Tooltip.SetTip(DeployHelpButton, plan?.Scope switch
            {
                DeploymentScope.Plugins => "当前仅同步插件",
                DeploymentScope.Rotation => "当前仅更新地图轮换",
                DeploymentScope.PluginsAndRotation => "当前仅同步插件和地图轮换",
                DeploymentScope.None => "当前无需部署",
                DeploymentScope.Full => "当前需要完整部署",
                _ => "当前无法检查部署范围"
            });
        }
        catch (Exception e)
        {
            if (request == deployHelpRequest && Model == model && model.Selected?.Entry.Definition == definition)
                Tooltip.SetTip(DeployHelpButton, e is IOException && e.Message.StartsWith("HCSL-MAPGROUP-STARTUP:", StringComparison.Ordinal)
                    ? "当前图池缺少启动地图" : "当前无法检查部署范围");
        }
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
        // The stored password is loaded once per instance and then left alone: polling must never rewrite the field, or
        // a half-typed value disappears on the next status tick. A successful save replaces the cache.
        var hasPassword = entry?.HasPassword == true;
        PasswordInput.PlaceholderText = hasPassword ? "已设置，输入新值替换；空回车清除" : "无密码";
        if (entry is not null && passwordInstance != entry.Id)
        {
            passwordInstance = entry.Id;
            storedPassword = "";
            if (!PasswordInput.IsFocused) PasswordInput.Text = "";
            if (hasPassword) _ = LoadPasswordAsync(entry.Id);
        }
        else if (!hasPassword && storedPassword.Length > 0)
        {
            storedPassword = "";
            if (!PasswordInput.IsFocused) PasswordInput.Text = "";
        }
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
        PasswordInput.ErrorMessage = null;
        PasswordInput.Text = storedPassword;
    }

    /// <summary>Loads the stored password once; the field keeps it masked until the eye toggle reveals it.</summary>
    private async Task LoadPasswordAsync(Guid instanceId)
    {
        if (Model is not { } m) return;
        try
        {
            var value = await m.RevealPasswordAsync();
            if (passwordInstance != instanceId) return;
            storedPassword = value;
            if (!PasswordInput.IsFocused) PasswordInput.Text = value;
        }
        catch (IOException error) { Error("无法读取入服密码", error.Message); }
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
        DeploymentPlan? plan;
        try { plan = await GetDeployPreviewAsync(m, d.Id, d.SelectedPresetId); }
        catch (IOException e) { Error("无法获取部署预览", e.Message); return; }
        if (plan is null) { Error("无法获取部署预览", "宿主未连接或已有操作。"); return; }
        if (plan.Scope == DeploymentScope.None)
        {
            owner.ShowInstanceNotification("当前无需部署", "现有部署已匹配当前配置。", ToastKind.Success);
            return;
        }
        if (await owner.ChooseAsync("部署预览（不启动）", DevFormat.Plan(plan, m.Selected.Entry, preset), "取消", "确认部署") != 1) return;
        if (m.Selected?.Entry.Id != d.Id) return;
        var previousRun = m.Server.RunId;
        if (!await m.DeployAsync()) { Error("部署未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error); return; }
        for (var waited = 0; waited < 1800; waited++)
        {
            if (m.Server.Snapshot is { RunId: { } run, InstanceId: { } instance } status &&
                instance == d.Id && run != previousRun && (!status.Active || status.State == ServerState.RecoveryRequired))
            {
                var done = m.Server.Code == "HCSL-SERVER-DEPLOYED";
                Owner?.ShowInstanceNotification(done ? "部署完成" : "部署未完成", m.Server.DetailText,
                    done ? ToastKind.Success : ToastKind.Error);
                return;
            }
            if (m.Selected?.Entry.Id != d.Id || !m.Server.Connected) return;
            await Task.Delay(500);
        }
    });

    private async void RecoverDeployment(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        if (await owner.ChooseAsync("清理无用部署", "删除上次部署中断留下的 journal 与临时目录；运行目录不受影响，不会启动 CS2。", "取消", "确认清理") != 1) return;
        if (m.Selected?.Entry.Id != d.Id) return;
        var previousRun = m.Server.RunId;
        if (!m.CanRecoverDeployment) { Error("当前无法清理", "请确认服务器已停止且宿主空闲。"); return; }
        if (!await m.RecoverDeploymentAsync())
        {
            Error("清理未提交", m.Server.ErrorText is { Length: > 0 } error ? error : "请检查宿主连接后重试。");
            return;
        }
        for (var waited = 0; waited < 1200; waited++)
        {
            if (m.Selected?.Entry.Id != d.Id) return;
            if (m.Server.RunId != previousRun && m.Server.InstanceId == d.Id && !m.Server.Active) break;
            await Task.Delay(500);
        }
        if (m.Server.RunId == previousRun || m.Server.InstanceId != d.Id || m.Server.Active)
        {
            Error("尚未确认清理结果", "请查看宿主状态。");
            return;
        }
        var done = m.Server.Code == "HCSL-SERVER-DEPLOY-RECOVERED";
        Owner?.ShowInstanceNotification(done ? "已清理无用部署" : "清理未完成", m.Server.DetailText,
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
        try
        {
            await m.SetPasswordAsync(value);
            // The save accepted the value, so it becomes the cached one; the field then shows it masked.
            storedPassword = value;
            PasswordInput.Text = value;
            Owner?.ShowInstanceNotification(value.Length == 0 ? "已清除入服密码" : "已设置入服密码", null, ToastKind.Success);
        }
        catch (IOException error) { PasswordInput.Text = storedPassword; Error("密码未保存", error.Message); }
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
