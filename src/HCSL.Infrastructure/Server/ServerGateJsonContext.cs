using System.Text.Json.Serialization;
using HCSL.Core;

namespace HCSL.Infrastructure.Server;

[JsonSerializable(typeof(ServerGateCommand))]
[JsonSerializable(typeof(ServerConsoleCommand))]
internal sealed partial class ServerGateJsonContext : JsonSerializerContext;
