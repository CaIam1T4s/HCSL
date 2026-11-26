using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;
public sealed class InstanceClient(IDaemonClient client)
{
    public async Task<MapPoolResult> MapPoolAsync(MapPoolCommand command, CancellationToken ct = default)
    {        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.MapPool,
            Options: new(hello.Defaults), MapPoolJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<MapPoolResult>(response.MapPoolResultJson ?? "null") ?? throw new IOException("Missing map pool result.");
    }

    /// <summary>Instance-level pool library: shared by every preset of that instance.</summary>
    public async Task<MapPoolLibraryResult> MapPoolsAsync(MapPoolLibraryCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.MapPoolLibrary,
            Options: new(hello.Defaults), MapPoolLibraryJson: JsonSerializer.Serialize(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<MapPoolLibraryResult>(response.MapPoolLibraryResultJson ?? "null")
            ?? throw new IOException("Missing map pool library result.");
    }

    /// <summary>
    /// Read-only catalog of the maps one instance runtime can offer. The catalog itself is shared by the app; what is
    /// downloaded is a fact of that runtime, which is why the instance id travels with the request.
    /// </summary>
    public Task<MapCatalogResult> MapCatalogAsync(Guid instanceId, CancellationToken ct = default) => MapCatalogAsync(new MapCatalogCommand(instanceId), ct);

    public async Task<MapCatalogResult> MapCatalogAsync(MapCatalogCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var request = new LocalRequest(LocalProtocol.Version, Guid.NewGuid(), RequestKind.MapCatalog,
            Options: new(hello.Defaults), MapCatalogJson: JsonSerializer.Serialize(command));
        // A Steam lookup runs outside the local catalog's normal five-second budget.
        var response = command.WorkshopId is not null
            ? await client.SendAsync(request, TimeSpan.FromSeconds(25), ct)
            : await client.SendAsync(request, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<MapCatalogResult>(response.MapCatalogResultJson ?? "null")
            ?? throw new IOException("Missing map catalog result.");
    }
    public async Task<PluginResult> PluginAsync(PluginCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Plugin,
            Options: new(hello.Defaults), PluginJson: JsonSerializer.Serialize(command)), TimeSpan.FromMinutes(2), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<PluginResult>(response.PluginResultJson ?? "null") ?? throw new IOException("Missing plugin result.");
    }
    public async Task<PluginImportResult> PluginImportAsync(PluginImportCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.PluginImport,
            Options: new(hello.Defaults), PluginImportJson: JsonSerializer.Serialize(command)), TimeSpan.FromMinutes(5), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<PluginImportResult>(response.PluginImportResultJson ?? "null")
            ?? throw new IOException("Missing plugin import result.");
    }

    public async Task<PluginImportPreview> PluginImportProbeAsync(PluginImportProbeCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.PluginImportProbe,
            Options: new(hello.Defaults), PluginImportProbeJson: JsonSerializer.Serialize(command)), TimeSpan.FromMinutes(5), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<PluginImportPreview>(response.PluginImportPreviewJson ?? "null")
            ?? throw new IOException("Missing plugin import preview.");
    }

    public Task<PluginCfgPreview> PluginCfgPreviewAsync(PluginCfgCommand command, CancellationToken ct = default) =>
        PluginCfgAsync<PluginCfgPreview>(command, ct);
    public Task<PluginCfgResult> PluginCfgApplyAsync(PluginCfgCommand command, CancellationToken ct = default) =>
        PluginCfgAsync<PluginCfgResult>(command, ct);
    private async Task<T> PluginCfgAsync<T>(PluginCfgCommand command, CancellationToken ct)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.PluginCfg,
            Options: new(hello.Defaults), PluginCfgJson: JsonSerializer.Serialize(command)), TimeSpan.FromMinutes(2), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<T>(response.PluginCfgResultJson ?? "null") ?? throw new IOException("Missing plugin cfg result.");
    }

    public async Task<ServerConsoleReceipt[]> DeliverCfgBatchAsync(CfgBatchCommand command, CancellationToken ct = default)
    {
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.CfgBatch,
            PresetCfgJson: JsonSerializer.Serialize(command)), TimeSpan.FromSeconds(15), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<ServerConsoleReceipt[]>(response.ConsoleReceiptJson ?? "null")
            ?? throw new IOException("Missing CFG batch receipt; delivery unknown.");
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
    public Task<string> PresetCfgDirectoryAsync(Guid instanceId, Guid presetId, CancellationToken ct = default) =>
        PresetDirectoryAsync(instanceId, presetId, "cfg", ct);

    /// <summary>Resolves a preset-owned directory (cfg, addons) without reading its contents.
    /// home/userdata belong to the instance and are intentionally not exposed here.</summary>
    public async Task<string> PresetDirectoryAsync(Guid instanceId, Guid presetId, string subdirectory, CancellationToken ct = default)
    {
        if (instanceId == Guid.Empty || presetId == Guid.Empty) throw new IOException("HCSL-PRESET-DIR: Instance and preset IDs are required.");
        if (subdirectory is not ("cfg" or "addons")) throw new IOException("HCSL-PRESET-DIR: Unknown preset directory.");
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        return Path.Combine(hello.Defaults.DataRoot, "instances", instanceId.ToString("D"), "presets", presetId.ToString("D"), subdirectory);
    }

    /// <summary>Resolves the deployed runtime directory for an instance (no contents are read).</summary>
    public async Task<string> RuntimeDirectoryAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (instanceId == Guid.Empty) throw new IOException("HCSL-RUNTIME-DIR: Instance ID is required.");
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        return Path.Combine(hello.Defaults.LibraryRoot, "runtime", instanceId.ToString("D"));
    }
    /// <summary>Resolved workspace roots reported by the daemon; used for read-only plans.</summary>
    public async Task<Contracts.WorkspacePaths> WorkspaceAsync(CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        return hello.Defaults;
    }

    public async Task<InstanceCatalog> ExecuteAsync(InstanceCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults == null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Instances,
            Options: new(hello.Defaults), InstanceCommandJson: JsonSerializer.Serialize(command, WorkspaceJsonContext.Default.InstanceCommand)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize(response.InstanceCatalogJson!, WorkspaceJsonContext.Default.InstanceCatalog) ?? throw new IOException("Missing instance catalog.");
    }
}
