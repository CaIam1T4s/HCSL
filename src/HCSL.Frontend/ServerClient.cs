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
    public async Task<ServerSnapshot> SendAsync(ServerCommand command, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Server,
            Options: workspace == null ? null : new(workspace), ServerCommandJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<ServerSnapshot>(response.ServerSnapshotJson!) ?? throw new IOException("Missing server state.");
    }
}
