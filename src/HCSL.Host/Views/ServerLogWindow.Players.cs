using Avalonia.Controls;
using HCSL.Core;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views;

/// <summary>
/// Roster data for the console window: the page (PlayerRosterPage.axaml) owns the layout and every
/// confirmation, while this side reads the daemon's roster and admin list and writes admin changes.
/// </summary>
public sealed partial class ServerLogWindow : IPlayerRosterActions
{
    private readonly PlayerRosterPage playerPage = new();
    private readonly Dictionary<string, string> adminGroupBySteamId = new(StringComparer.OrdinalIgnoreCase);
    private string[] adminGroups = [];
    private string? adminHash;
    private bool adminRefreshInFlight;
    private long nextAdminRefreshAt;
    private bool rosterFailureNotified;

    private Control BuildPlayersPage()
    {
        playerPage.Actions = this;
        return playerPage;
    }

    Task IPlayerRosterActions.KickAsync(PlayerRow row) => KickAsync(row);

    Task IPlayerRosterActions.GrantAsync(PlayerRow row, string group) => ApplyAdminAsync(row, group, remove: false);

    Task IPlayerRosterActions.RevokeAsync(PlayerRow row) => ApplyAdminAsync(row, null, remove: true);

    private async Task KickAsync(PlayerRow row)
    {
        if (model.Server.Snapshot is not { InstanceId: { } instance, RunId: { } run })
        {
            notifications.Show(new ToastOptions { Title = "踢出失败", Description = "当前没有可用的运行实例", Kind = ToastKind.Error });
            return;
        }
        try
        {
            await model.KickAsync(instance, run, row.UserId, row.SteamId, "HCSL console");
            // Refresh immediately instead of waiting for the next poll.
            await model.Server.RefreshRosterAsync();
            notifications.Show(new ToastOptions
            {
                Title = "已发送踢出请求",
                Description = row.Name + " · 名单将在下次刷新时更新",
                Kind = ToastKind.Info
            });
        }
        catch (Exception e) when (e is IOException or ServerException or InvalidOperationException)
        {
            notifications.Show(new ToastOptions { Title = "踢出失败", Description = e.Message, Kind = ToastKind.Error });
        }
    }

    private async Task ApplyAdminAsync(PlayerRow row, string? group, bool remove)
    {
        if (row.SteamId is not { Length: > 0 } steamId)
        {
            notifications.Show(new ToastOptions { Title = "无法修改身份", Description = "该玩家还没有关联 SteamID", Kind = ToastKind.Warning });
            return;
        }
        if (model.Selected is not { Entry: { Definition: { } definition, Presets: var presets } }
            || presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId) is not { } preset)
        {
            notifications.Show(new ToastOptions { Title = "无法修改身份", Description = "先在实例列表中选择该实例与预设", Kind = ToastKind.Warning });
            return;
        }
        try
        {
            var mutation = remove
                ? new AdminMutation(AdminMutationKind.Remove, steamId)
                : new AdminMutation(AdminMutationKind.Upsert, steamId, row.Name, [group!]);
            var command = new AdminCommand(AdminAction.Apply, definition.Id, preset.Id, adminHash, [mutation]);
            await model.ApplyAdminAsync(command);
            if (remove) adminGroupBySteamId.Remove(steamId);
            else adminGroupBySteamId[steamId] = group!;
            foreach (var existing in playerPage.Rows)
                if (string.Equals(existing.SteamId, steamId, StringComparison.OrdinalIgnoreCase))
                    existing.SetAdminGroup(remove ? null : group);
            // Ask the host to re-apply the list now: a plugin that caches admins keeps the old set otherwise.
            try { await model.ReloadAdminsAsync(); }
            catch (Exception e) when (e is IOException or ServerException or InvalidOperationException or UnauthorizedAccessException) { }
            // The file hash changed, so the next write must be planned against the new content.
            await RefreshAdminListAsync();
            notifications.Show(new ToastOptions
            {
                Title = remove ? "已移除管理员" : "已授予管理员",
                Description = row.Name + (remove ? "" : " · " + group),
                Kind = ToastKind.Success
            });
        }
        catch (Exception e) when (e is IOException or ServerException or InvalidOperationException or UnauthorizedAccessException)
        {
            notifications.Show(new ToastOptions { Title = "修改身份失败", Description = e.Message, Kind = ToastKind.Error });
        }
    }

    /// <summary>Rebuilds nothing: rows are matched by identity and only changed fields are pushed.</summary>
    private void UpdatePlayersPage()
    {
        var rows = playerPage.Rows;
        playerPage.IsLive = !closed && model.Server.Active;
        var roster = model.Server.LivePlayerRoster;
        if (!model.Server.Active)
        {
            playerPage.ShowUnavailable("服务器未运行，无法查看玩家");
            return;
        }
        playerPage.ShowReady();
        if (roster is null)
        {
            // No roster yet: keep the last rows and stay quiet. A roster that was never fetched (for example
            // the first moments after reopening the host) is not a failure; only a failed query is.
            playerPage.ShowLoading();
            if (model.Server.RosterFailed && !rosterFailureNotified)
            {
                rosterFailureNotified = true;
                notifications.Show(new ToastOptions
                {
                    Title = "无法读取玩家名单",
                    Description = "保留上次结果；服务器仍在运行时可稍后重试",
                    Kind = ToastKind.Warning
                });
            }
            return;
        }
        rosterFailureNotified = false;
        if (Environment.TickCount64 >= nextAdminRefreshAt)
        {
            nextAdminRefreshAt = Environment.TickCount64 + 30000;
            _ = RefreshAdminListAsync();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var player in roster.Players)
        {
            var key = RowKey(player.SteamId64, player.UserId);
            seen.Add(key);
            var existing = rows.FirstOrDefault(r => RowKey(r.SteamId, r.UserId) == key);
            if (existing is null)
            {
                existing = new PlayerRow(player.UserId, player.Name, player.IsBot, player.SteamId64, player.Address,
                    player.State, player.Ping, player.Connected);
                rows.Add(existing);
            }
            else existing.Update(player.State, player.Ping, player.Connected);
            playerPage.ApplyAdminGroup(existing);
        }
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!seen.Contains(RowKey(rows[i].SteamId, rows[i].UserId))) rows.RemoveAt(i);
    }

    private static string RowKey(string? steamId, int userId) =>
        steamId is { Length: > 0 } ? "s:" + steamId : "u:" + userId;

    /// <summary>Admin groups and the preset hash the write must match; one page is enough to label the roster.</summary>
    private async Task RefreshAdminListAsync()
    {
        if (adminRefreshInFlight || !model.Server.Active) return;
        adminRefreshInFlight = true;
        try
        {
            var result = await model.AdminsAsync(0, 200);
            var snapshot = result.Snapshot;
            adminHash = snapshot.Hash;
            adminGroups = snapshot.Groups.Select(g => g.Name).ToArray();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Entries)
                if (entry.Groups.Length > 0) map[entry.SteamId64] = entry.Groups[0];
            adminGroupBySteamId.Clear();
            foreach (var pair in map) adminGroupBySteamId[pair.Key] = pair.Value;
            playerPage.SetAdminGroups(adminGroups, adminGroupBySteamId);
        }
        catch (Exception e) when (e is IOException or ServerException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The roster is still useful without admin labels.
        }
        finally { adminRefreshInFlight = false; }
    }
}
