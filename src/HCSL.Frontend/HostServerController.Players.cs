using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>
/// Online roster and framework admin list for the host UI. Everything goes through the daemon; the page never
/// touches the filesystem, and the roster/kick gates are derived from the live server snapshot only.
/// </summary>
public sealed partial class HostServerController
{
    private AdminClient? admins;
    private AdminClient Admins => admins ??= new(daemon ?? throw new IOException("HCSL-ADMIN-REQUEST: This host has no daemon connection."));

    /// <summary>Roster is meaningful only while a run is starting or running.</summary>
    public bool CanQueryPlayers => !Busy && Server.Connected && Server.Snapshot is { Active: true } snapshot
        && snapshot.State is ServerState.Starting or ServerState.LoadingMap or ServerState.Running;

    /// <summary>Admin lists may be edited while the server runs (the daemon reloads); only a busy host blocks it.</summary>
    public bool CanEditAdmins => !Busy && Server.Connected;

    public async Task<PlayerRoster> PlayersAsync()
    {
        if (!Server.Connected) throw new IOException("HCSL-PLAYERS-STATE: Host is disconnected.");
        if (Selected is not { } item) throw new IOException("HCSL-PLAYERS-STATE: Select an instance first.");
        if (!CanQueryPlayers)
            throw new IOException("HCSL-PLAYERS-STATE: The server is not running for this instance; start it first.");
        Busy = true; Notify();
        try { return await new ServerClient(daemon!).PlayersAsync(item.Entry.Id); }
        finally { Busy = false; Notify(); }
    }

    /// <summary>Kick is confirmed by the caller; the daemon re-reads the roster so a recycled userid cannot hit another player.</summary>
    public async Task<ServerKickResult> KickAsync(Guid instanceId, Guid runId, int userId, string? steamId64, string reason)
    {
        if (!Server.Connected) throw new IOException("HCSL-KICK-STATE: Host is disconnected.");
        if (!CanQueryPlayers) throw new IOException("HCSL-KICK-STATE: The server is not running; refresh the roster.");
        Busy = true; Notify();
        try { return await new ServerClient(daemon!).KickAsync(new(instanceId, runId, userId, steamId64, reason)); }
        finally { Busy = false; Notify(); }
    }

    public async Task<AdminResult> AdminsAsync(int offset = 0, int limit = 0, string? query = null)
    {
        if (Selected is not { } item) throw new IOException("HCSL-ADMIN-REQUEST: Select an instance first.");
        if (!Server.Connected) throw new IOException("HCSL-ADMIN-REQUEST: Host is disconnected.");
        var preset = SelectedPreset ?? throw new IOException("HCSL-ADMIN-REQUEST: Select a preset first.");
        Busy = true; Notify();
        try { return await Admins.ListAsync(item.Entry.Id, preset.Id, offset, limit, query); }
        finally { Busy = false; Notify(); }
    }

    public async Task<AdminResult> ApplyAdminAsync(AdminCommand command)
    {
        if (!CanEditAdmins) throw new IOException("HCSL-ADMIN-CONFLICT: Another operation is active; retry when it finishes.");
        if (Selected is not { } item || SelectedPreset is not { } preset || command.InstanceId != item.Entry.Id || command.PresetId != preset.Id)
            throw new IOException("HCSL-ADMIN-REQUEST: Refresh the instance/preset selection.");
        Busy = true; Notify();
        try { return await Admins.ApplyAsync(command); }
        finally { Busy = false; Notify(); }
    }

    public async Task<AdminResult> ReloadAdminsAsync()
    {
        if (Selected is not { } item || SelectedPreset is not { } preset)
            throw new IOException("HCSL-ADMIN-REQUEST: Select an instance and preset first.");
        if (!Server.Connected) throw new IOException("HCSL-ADMIN-REQUEST: Host is disconnected.");
        Busy = true; Notify();
        try { return await Admins.ReloadAsync(item.Entry.Id, preset.Id); }
        finally { Busy = false; Notify(); }
    }
}
