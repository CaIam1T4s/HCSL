using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HCSL.Contracts;
using HCSL.Core;

namespace HCSL.Frontend;

// These command bodies use the daemon's default JSON format (including numeric enums).
[JsonSerializable(typeof(InstanceCommand))]
[JsonSerializable(typeof(InstanceCatalog))]
[JsonSerializable(typeof(MapPoolCommand))]
[JsonSerializable(typeof(MapPoolResult))]
[JsonSerializable(typeof(MapPoolLibraryCommand))]
[JsonSerializable(typeof(MapPoolLibraryResult))]
[JsonSerializable(typeof(MapCatalogCommand))]
[JsonSerializable(typeof(MapCatalogResult))]
[JsonSerializable(typeof(ServerCommand))]
[JsonSerializable(typeof(DeploymentPlan))]
[JsonSerializable(typeof(Cs2ContentStatus))]
[JsonSerializable(typeof(BaselineCommand))]
[JsonSerializable(typeof(BaselineResult))]
[JsonSerializable(typeof(ServerSnapshot))]
[JsonSerializable(typeof(AdminCommand))]
[JsonSerializable(typeof(AdminResult))]
[JsonSerializable(typeof(FrameworkCatalogCommand))]
[JsonSerializable(typeof(FrameworkCatalogResult))]
[JsonSerializable(typeof(FrameworkCheckCommand))]
[JsonSerializable(typeof(FrameworkCheckResult))]
[JsonSerializable(typeof(FrameworkInstallCommand))]
[JsonSerializable(typeof(FrameworkInstallResult))]
[JsonSerializable(typeof(TransferSnapshot))]
[JsonSerializable(typeof(PluginCommand))]
[JsonSerializable(typeof(PluginResult))]
[JsonSerializable(typeof(PluginImportCommand))]
[JsonSerializable(typeof(PluginImportResult))]
[JsonSerializable(typeof(PluginImportProbeCommand))]
[JsonSerializable(typeof(PluginImportPreview))]
[JsonSerializable(typeof(PluginCfgCommand))]
[JsonSerializable(typeof(PluginCfgPreview))]
[JsonSerializable(typeof(PluginCfgResult))]
[JsonSerializable(typeof(PresetCfgCommand))]
[JsonSerializable(typeof(PresetCfgResult))]
[JsonSerializable(typeof(ServerConsoleCommand))]
[JsonSerializable(typeof(ServerConsoleReceipt))]
[JsonSerializable(typeof(ServerConsoleReceipt[]))]
[JsonSerializable(typeof(ServerKickCommand))]
[JsonSerializable(typeof(ServerKickResult))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext
{
    internal static string Encode<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());
    internal static T? Decode<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

    private static JsonTypeInfo<T> TypeInfo<T>() => Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"Unsupported workspace IPC payload: {typeof(T)}.");
}
