using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;
public sealed class InstanceClient(IDaemonClient client)
{
    public async Task<MapPoolResult> MapPoolAsync(MapPoolCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.MapPool,
            Options: new(hello.Defaults), MapPoolJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<MapPoolResult>(response.MapPoolResultJson ?? "null") ?? throw new IOException("Missing map pool result.");
    }
    public async Task<PluginResult> PluginAsync(PluginCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Plugin,
            Options: new(hello.Defaults), PluginJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<PluginResult>(response.PluginResultJson ?? "null") ?? throw new IOException("Missing plugin result.");
    }
    public async Task<PresetCfgResult> CfgAsync(PresetCfgCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.PresetCfg,
            Options: new(hello.Defaults), PresetCfgJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<PresetCfgResult>(response.PresetCfgResultJson ?? "null") ?? throw new IOException("Missing CFG result.");
    }
    public async Task<string> PresetCfgDirectoryAsync(Guid instanceId, Guid presetId, CancellationToken ct = default)
    {
        if (instanceId == Guid.Empty || presetId == Guid.Empty) throw new IOException("HCSL-PRESET-CFG: Instance and preset IDs are required.");
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        return Path.Combine(hello.Defaults.DataRoot, "instances", instanceId.ToString("D"), "presets", presetId.ToString("D"), "cfg");
    }
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
