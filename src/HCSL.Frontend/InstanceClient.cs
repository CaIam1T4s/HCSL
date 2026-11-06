using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;
public sealed class InstanceClient(IDaemonClient client)
{
    public async Task<InstanceCatalog> ExecuteAsync(InstanceCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults == null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Instances,
            Options: new(hello.Defaults), InstanceCommandJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<InstanceCatalog>(response.InstanceCatalogJson!) ?? throw new IOException("Missing instance catalog.");
    }
}
