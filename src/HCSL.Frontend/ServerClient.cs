using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;
public sealed class ServerClient(IDaemonClient client)
{
    public async Task<ServerSnapshot> SendAsync(ServerCommand command, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Server,
            Options: workspace == null ? null : new(workspace), ServerCommandJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<ServerSnapshot>(response.ServerSnapshotJson!) ?? throw new IOException("Missing server state.");
    }
}
