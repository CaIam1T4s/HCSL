using System.ComponentModel;
using HCSL.Core;

namespace HCSL.Frontend;

public sealed record HostInstanceDraft(string Name, int GamePort, ServerNetwork? Network = null);

public sealed record HostInstanceItem(InstanceEntry Entry)
{
    public override string ToString() => Entry.Definition?.Name ?? $"{Entry.Id:D} · {Entry.Error}";
}

/// <summary>Desktop-host orchestration over the same server controller and instance IPC as TUI.</summary>
public sealed partial class HostServerController : INotifyPropertyChanged, IDisposable
{
    private readonly InstanceClient instances;
    private HostInstanceItem? selected;
    private bool disposed, userSelected;
    private int logConsumers;
    private readonly FrameworkClient? frameworks;
    /// <summary>Kept for the online-roster/admin clients; null when the host runs without a daemon (tests).</summary>
    private readonly IDaemonClient? daemon;
    public HostServerController(ServerController server, InstanceClient instances, HCSL.Application.Abstractions.ILocalDirectoryOpener? directoryOpener = null,
        IDaemonClient? daemon = null)
    {
        Server = server; this.instances = instances; this.directoryOpener = directoryOpener; this.daemon = daemon;
        frameworks = daemon is null ? null : new FrameworkClient(daemon);
        Server.PropertyChanged += ServerChanged;
    }
    /// <summary>Preset the UI is looking at: the instance's selected preset.</summary>
    private PresetDefinition? SelectedPreset => Selected?.Entry.Presets.FirstOrDefault(p => p.Id == Selected.Entry.Definition?.SelectedPresetId);
    public ServerController Server { get; }
    public IReadOnlyList<HostInstanceItem> Instances { get; private set; } = [];
    private string[] catalogAddresses = [];
    public IReadOnlyList<string> ConnectionAddresses => Server.Connected ? Server.Snapshot?.ConnectionAddresses ?? catalogAddresses : [];
    public string ConnectionAddressText => string.Join(" / ", ConnectionAddresses);
    public string ConnectionEndpoints(int port) => ConnectionAddresses.Count == 0 ? $"本机连接地址：未知（端口 {port}），请刷新列表"
        : "本机连接地址：" + string.Join(" / ", ConnectionAddresses.Select(a => a + ":" + port));
    public HostInstanceItem? Selected
    {
        get => selected;
        set
        {
            if (value == selected) return;
            if (!CanSelectInstance) { Notify(); return; }
            userSelected = true; Select(value);
        }
    }
    private void Select(HostInstanceItem? value)
    {
        if (value == selected) return;
        selected = value;
        var configured = value?.Entry.Presets.FirstOrDefault(p => p.Id == value.Entry.Definition?.SelectedPresetId);
        Server.ConfiguredMap = configured is null ? null : ServerMapRules.Label(new(configured.MapKind, configured.Map));
        Server.Attach(value?.Entry.Id ?? Guid.Empty);
        Notify();
    }
    public bool CanSelectInstance => !Busy && !Server.Busy &&
        Server.State is not (ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap);
    public bool Busy { get; private set; }
    public string Error { get; private set; } = "";
    public bool CanCreate => !Busy && !Server.Busy && Server.Connected && !Server.Active
        && Server.State is not (ServerState.RecoveryRequired or ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap or ServerState.Stopping);
    public bool CanMutate => CanCreate && Selected?.Entry.Definition is not null;
    public string Header
    {
        get
        {
            if (!Server.Connected) return "HCSL · 宿主状态未知 / 未连接";
            var identity = Server.LiveInstanceId ?? Selected?.Entry.Id;
            var name = Instances.FirstOrDefault(x => x.Entry.Id == identity)?.ToString() ?? identity?.ToString("D") ?? "未选择实例";
            return "HCSL · " + name + " · " + Server.SummaryLine;
        }
    }
    public string SelectionText
    {
        get
        {
            var entry = Selected?.Entry;
            var d = entry?.Definition;
            if (d is null) return "尚未选择有效实例；可在实例列表创建实例。";
            var preset = entry!.Presets.FirstOrDefault(p => p.Id == d.SelectedPresetId);
            var live = Server.LiveInstanceId;
            var running = live is { } id ? Instances.FirstOrDefault(x => x.Entry.Id == id)?.ToString() ?? id.ToString("D") : "无";
            return $"所选：{d.Name} · 预设：{preset?.Name} · 预设地图：{(preset is null ? "—" : ServerMapRules.Label(new(preset.MapKind, preset.Map)))}\n" +
                $"{ConnectionEndpoints(d.GamePort)} · 入服密码：{(entry.HasPassword ? "已设置" : "无")}\n" +
                $"运行实例：{running} · VAC：{(Server.Connected ? (Server.EffectiveVac ? "开启" : "关闭") : "未知")} · 模式：{Server.Mode}";
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify() { if (!disposed) PropertyChanged?.Invoke(this, new(null)); }
    private void ServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!userSelected && Server.LiveInstanceId is { } id && Instances.FirstOrDefault(x => x.Entry.Id == id) is { } item) Select(item);
        Notify();
    }
    public async Task InitializeAsync() { await Server.ConnectAsync(); if (!disposed) await RefreshInstancesAsync(); }
    public async Task RefreshInstancesAsync()
    {
        if (Busy || disposed) return;
        Busy = true; Error = ""; Notify();
        try { Apply(await instances.ExecuteAsync(new(InstanceAction.List))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = e.Message; }
        finally { Busy = false; Notify(); }
    }
    private void Apply(InstanceCatalog catalog)
    {
        if (disposed) return;
        var wanted = !userSelected && Server.LiveInstanceId is { } live ? live : selected?.Entry.Id;
        catalogAddresses = catalog.ConnectionAddresses ?? [];
        Instances = catalog.Instances.Select(e => new HostInstanceItem(e)).ToArray();
        Select(Instances.FirstOrDefault(x => x.Entry.Id == wanted) ?? Instances.FirstOrDefault(x => x.Entry.Definition is not null));
        Notify();
    }
    public static void ValidateDraft(HostInstanceDraft draft)
    {
        InstanceRules.Name(draft.Name);
        if (draft.GamePort is < 1 or > 65535) throw new InstanceException("HCSL-INSTANCE-PORT", "端口必须在 1–65535 之间。");
        InstanceRules.Network(draft.Network);
    }
    public Task<bool> CreateInstanceAsync(HostInstanceDraft draft) => WriteInstanceAsync(
        new(InstanceAction.Create, Name: draft.Name, GamePort: draft.GamePort, Network: draft.Network), draft);
    public Task<bool> UpdateInstanceAsync(Guid id, long revision, HostInstanceDraft draft) => WriteInstanceAsync(
        new(InstanceAction.Update, id, Revision: revision, Name: draft.Name, GamePort: draft.GamePort, Network: draft.Network), draft);
    public Task<bool> DeleteInstanceAsync(Guid id, long revision) => WriteInstanceAsync(new(InstanceAction.Delete, id, Revision: revision));
    public bool CanMigrateLayout => CanMutate && Selected?.Entry.Definition is { } d
        && d.LayoutVersion < InstanceRules.CurrentLayoutVersion;
    public Task<bool> MigrateLayoutAsync() => Selected?.Entry.Definition is { } d
        ? WriteInstanceAsync(new(InstanceAction.MigrateLayout, d.Id, Revision: d.Revision))
        : Task.FromResult(false);
    private async Task<bool> WriteInstanceAsync(InstanceCommand command, HostInstanceDraft? draft = null)
    {
        if (!CanCreate || (command.Action != InstanceAction.Create && !Instances.Any(i => i.Entry.Id == command.InstanceId && i.Entry.Definition is not null)))
        {
            Error = "当前不能修改实例：请连接宿主，先停服并完成恢复检查。"; Notify(); return false;
        }
        Busy = true; Error = ""; Notify();
        try
        {
            if (draft is not null) ValidateDraft(draft);
            var oldIds = Instances.Select(i => i.Entry.Id).ToHashSet();
            var catalog = await instances.ExecuteAsync(command);
            Apply(catalog);
            if (command.Action == InstanceAction.Create)
            {
                userSelected = true;
                Select(Instances.FirstOrDefault(i => !oldIds.Contains(i.Entry.Id)) ?? Selected);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstanceException) { Error = e.Message; return false; }
        finally { Busy = false; Notify(); }
    }
    public Task<bool> StartAsync(bool allowLowMemory = false)
    {
        if (Busy || Selected?.Entry.Definition is null || !Server.CanStart) return Task.FromResult(false);
        Server.Attach(Selected.Entry.Id);
        return Server.StartAsync(autoDeploy: true, allowLowMemory: allowLowMemory);
    }
    /// <summary>Read-only deployment preview for the confirmation dialog (never writes).</summary>
    public async Task<DeploymentPlan?> DeployPreviewAsync(Guid instanceId, Guid presetId)
    {
        if (Busy || Server.Busy || !Server.Connected) return null;
        if (!Instances.Any(i => i.Entry.Id == instanceId && i.Entry.Presets.Any(p => p.Id == presetId))) return null;
        Server.Attach(instanceId);
        var plan = await Server.PlanDeploymentAsync(await instances.WorkspaceAsync());
        return plan;
    }

    /// <summary>Deploy-only action for the UI: no AutoDeploy, no start.</summary>
    public Task<bool> DeployAsync()
    {
        if (Busy || Selected?.Entry.Definition is null || !Server.CanDeploy) return Task.FromResult(false);
        Server.Attach(Selected.Entry.Id);
        return Server.DeployAsync();
    }
    public bool CanDeploy => !Busy && Selected?.Entry.Definition is not null && Server.CanDeploy;

    public Task<bool> RecoverDeploymentAsync()
    {
        if (Busy || Selected?.Entry.Definition is null || !Server.CanRecoverDeployment) return Task.FromResult(false);
        Server.Attach(Selected.Entry.Id);
        return Server.RecoverDeploymentAsync();
    }
    public bool CanRecoverDeployment => !Busy && Selected?.Entry.Definition is not null && Server.CanRecoverDeployment;

    public Task<bool> RecoverAsync()
    {
        if (!Server.CanRecover) return Task.FromResult(false);
        var id = Server.Snapshot?.InstanceId ?? Selected?.Entry.Id;
        if (id is null || id == Guid.Empty) { Error = "HCSL-SERVER-RECOVERY: 无法确定归属实例，请使用 CLI 恢复。"; Notify(); return Task.FromResult(false); }
        Server.Attach(id.Value);
        return Server.RecoverAsync();
    }
    public async Task SetPasswordAsync(string password)
    {
        if (!CanMutate || Selected?.Entry.Definition is not { } d)
        {
            Error = "当前不能修改实例密码：请连接宿主、选择有效实例，先停服并完成恢复检查。"; Notify(); return;
        }
        Busy = true; Error = ""; Notify();
        try
        {
            InstanceRules.Password(password);
            Apply(await instances.ExecuteAsync(new(InstanceAction.SetPassword, d.Id, Revision: d.Revision, Password: password)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstanceException) { Error = e.Message; }
        finally { Busy = false; Notify(); }
    }
    public async Task<bool> StopAndWaitAsync(TimeSpan timeout)
    {
        if (!await Server.StopAsync()) return false;
        var until = System.Diagnostics.Stopwatch.StartNew();
        while (!disposed && until.Elapsed < timeout)
        {
            await Server.RefreshAsync();
            if (Server.Connected && !Server.Active)
                return Server.CleanupConfirmed && Server.State != ServerState.RecoveryRequired;
            await Task.Delay(250);
        }
        return false;
    }
    public void OpenLogs() { if (!disposed) { logConsumers++; Server.SetLogsVisible(true); } }
    public void CloseLogs() { logConsumers = Math.Max(0, logConsumers - 1); if (!disposed) Server.SetLogsVisible(logConsumers > 0); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Server.PropertyChanged -= ServerChanged; Server.Dispose();
    }
}
