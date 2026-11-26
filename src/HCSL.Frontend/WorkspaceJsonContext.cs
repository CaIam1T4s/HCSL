using System.Text.Json.Serialization;
using HCSL.Core;

namespace HCSL.Frontend;

// These command bodies use the daemon's default JSON format (including numeric enums).
[JsonSerializable(typeof(InstanceCommand))]
[JsonSerializable(typeof(InstanceCatalog))]
[JsonSerializable(typeof(ServerCommand))]
[JsonSerializable(typeof(DeploymentPlan))]
[JsonSerializable(typeof(Cs2ContentStatus))]
[JsonSerializable(typeof(BaselineCommand))]
[JsonSerializable(typeof(BaselineResult))]
[JsonSerializable(typeof(ServerSnapshot))]
internal sealed partial class WorkspaceJsonContext : JsonSerializerContext;
