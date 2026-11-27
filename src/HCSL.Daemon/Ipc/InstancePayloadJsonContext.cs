using System.Text.Json.Serialization;
using HCSL.Contracts;
using HCSL.Core;

namespace HCSL.Daemon.Ipc;

// Inner IPC payloads and the existing size estimate use the default JSON format.
[JsonSerializable(typeof(InstanceCommand))]
[JsonSerializable(typeof(InstanceCatalog))]
[JsonSerializable(typeof(MapPoolCommand))]
[JsonSerializable(typeof(MapPoolResult))]
[JsonSerializable(typeof(MapPoolLibraryCommand))]
[JsonSerializable(typeof(MapPoolLibraryResult))]
[JsonSerializable(typeof(LocalResponse))]
[JsonSerializable(typeof(ServerCommand))]
[JsonSerializable(typeof(ServerSnapshot))]
[JsonSerializable(typeof(DeploymentPlan))]
internal sealed partial class InstancePayloadJsonContext : JsonSerializerContext;
