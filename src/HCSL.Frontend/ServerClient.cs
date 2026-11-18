using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;
public sealed class ServerClient(IDaemonClient client)
{
    public async Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
    {
        ServerConsoleRules.ValidateConfirmed(command);
        try
        {
            var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.ServerConsole,
                ServerConsoleJson: JsonSerializer.Serialize(command)), ct);
            if (!response.Success) return new(command.RunId, command.CommandId, ConsoleDelivery.Rejected, response.Code ?? "HCSL-CONSOLE-REJECTED");
            var receipt = JsonSerializer.Deserialize<ServerConsoleReceipt>(response.ConsoleReceiptJson ?? "null");
            return receipt is not null && receipt.RunId == command.RunId && receipt.CommandId == command.CommandId
                ? receipt : new(command.RunId, command.CommandId, ConsoleDelivery.Unknown, "HCSL-CONSOLE-UNKNOWN");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or DaemonProtocolException or UnauthorizedAccessException)
        { return new(command.RunId, command.CommandId, ConsoleDelivery.Unknown, "HCSL-CONSOLE-UNKNOWN"); }
    }
    /// <summary>Read-only deployment preview (no runtime write).</summary>
    public async Task<DeploymentPlan> PlanDeploymentAsync(Guid instanceId, WorkspacePaths workspace, CancellationToken ct = default)
    {
        if (instanceId == Guid.Empty) throw new IOException("HCSL-SERVER-ID: Instance ID is required.");
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Server,
            Options: new(workspace), ServerCommandJson: JsonSerializer.Serialize(new ServerCommand(ServerAction.PlanDeployment, instanceId))), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<DeploymentPlan>(response.DeploymentPlanJson ?? "null")
            ?? throw new IOException("Missing deployment plan.");
    }

    /// <summary>Point-in-time roster for a running instance. Throws with the daemon's stable code on failure.</summary>
    public async Task<PlayerRoster> PlayersAsync(Guid instanceId, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        if (instanceId == Guid.Empty) throw new IOException("HCSL-PLAYERS-STATE: Instance ID is required.");
        var snapshot = await SendAsync(new(ServerAction.Players, instanceId), workspace, ct);
        return snapshot.PlayerRoster ?? throw new IOException("HCSL-PLAYERS-STATE: The daemon returned no roster.");
    }

    /// <summary>Remove one client; the daemon re-reads the roster first so a recycled userid cannot hit another player.</summary>
    public async Task<ServerKickResult> KickAsync(ServerKickCommand command, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Server,
            Options: workspace == null ? null : new(workspace),
            ServerCommandJson: JsonSerializer.Serialize(new ServerCommand(ServerAction.Kick, command.InstanceId)),
            ServerKickJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<ServerKickResult>(response.ServerKickResultJson ?? "null")
            ?? throw new IOException("HCSL-KICK-STATE: The daemon returned no kick result.");
    }

    public async Task<ServerSnapshot> SendAsync(ServerCommand command, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        // Status/log polling shares the daemon with deployments and installs; five seconds is shorter
        // than a legitimate busy period, so polling opts into a longer deadline than mutations.
        var timeout = command.Action is ServerAction.Status or ServerAction.Logs ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(10);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Server,
            Options: workspace == null ? null : new(workspace), ServerCommandJson: JsonSerializer.Serialize(command)), timeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<ServerSnapshot>(response.ServerSnapshotJson!) ?? throw new IOException("Missing server state.");
    }
}
