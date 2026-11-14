using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.Services;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.ViewModels;

public sealed class InstanceListRow(HostInstanceItem item) : ObservableObject
{
    public HostInstanceItem Item { get; private set; } = item;
    public Guid Id => Item.Entry.Id;
    public string Name => Item.Entry.Definition?.Name ?? Id.ToString("D");
    public string PresetName => Item.Entry.Presets.FirstOrDefault(p => p.Id == Item.Entry.Definition?.SelectedPresetId)?.Name ?? "无预设";
    public string Port => Item.Entry.Definition?.GamePort.ToString() ?? "暂无记录";
    public string Subtitle => Item.Entry.Definition is not null ? $"{PresetName} · {Port}" : "配置异常 · " + Item.Entry.Error;
    public string StartupLabel => "设为启动实例：" + Name;
    public string OperationsLabel => "实例操作：" + Name;
    public string CreatedAt => "暂无记录";
    public string LastRunAt => "暂无记录";
    public bool IsStartupSelected { get; private set; }
    public bool CanSelect { get; private set; }
    public bool CanStart { get; private set; }
    public bool CanEdit { get; private set; }
    public bool CanViewLogs { get; private set; }
    public bool CanMigrate => CanEdit && Item.Entry.Definition?.LayoutVersion < InstanceRules.CurrentLayoutVersion;
    public int StatusOrder { get; private set; } = int.MaxValue;
    public string StatusText { get; private set; } = "状态未知";
    public BadgeTone StatusTone { get; private set; }
    public LucideIconKind StatusIcon { get; private set; } = LucideIconKind.CircleAlert;

    public void Update(HostInstanceItem item, HostServerController model, Guid? startup, bool operating)
    {
        Item = item;
        IsStartupSelected = Id == startup;
        CanSelect = !operating && model.CanSelectInstance && Item.Entry.Definition is not null;
        CanStart = CanEdit = !operating && model.CanCreate && Item.Entry.Definition is not null;
        CanViewLogs = !operating && model.Server.Connected && model.Server.Snapshot?.InstanceId == Id && model.Server.RunId is not null;
        var snapshot = model.Server.Snapshot;
        var known = model.Server.Connected && snapshot is not null && Item.Entry.Definition is not null &&
            !(snapshot is { Active: true, InstanceId: null } || snapshot is { State: ServerState.RecoveryRequired, InstanceId: null });
        var state = snapshot?.InstanceId == Id ? model.Server.State : ServerState.Stopped;
        StatusOrder = !known ? int.MaxValue : state switch
        {
            ServerState.Idle or ServerState.Stopped => 0,
            ServerState.Preparing => 1, ServerState.Starting => 2, ServerState.LoadingMap => 3,
            ServerState.Running => 4, ServerState.Stopping => 5,
            ServerState.Failed => 6, ServerState.RecoveryRequired => 7,
            _ => int.MaxValue
        };
        (StatusText, StatusTone, StatusIcon) = ServerBadgePresentation.For(state, known);
        Notify(null);
    }
}
