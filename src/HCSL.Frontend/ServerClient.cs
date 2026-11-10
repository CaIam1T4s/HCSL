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

    public async Task<ServerSnapshot> SendAsync(ServerCommand command, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Server,
            Options: workspace == null ? null : new(workspace), ServerCommandJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<ServerSnapshot>(response.ServerSnapshotJson!) ?? throw new IOException("Missing server state.");
    }
}
