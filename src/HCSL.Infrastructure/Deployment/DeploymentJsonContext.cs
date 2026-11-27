using System.Text.Json;
using System.Text.Json.Serialization;
using HCSL.Core;
using HCSL.Infrastructure.Server;

namespace HCSL.Infrastructure.Deployment;

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(RuntimeDeploymentService.InstallState))]
[JsonSerializable(typeof(RuntimeDeploymentService.PluginLedger))]
[JsonSerializable(typeof(RuntimeDeploymentService.Transaction))]
[JsonSerializable(typeof(DeploymentReceipt))]
[JsonSerializable(typeof(SystemdServerRuntime.Session))]
[JsonSerializable(typeof(ServerLastRun))]
internal sealed partial class DeploymentJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(RuntimeDeploymentService.LocalBaselineDocument))]
internal sealed partial class DeploymentBaselineJsonContext : JsonSerializerContext;
