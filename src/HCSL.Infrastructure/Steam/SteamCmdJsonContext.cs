using System.Text.Json.Serialization;

namespace HCSL.Infrastructure.Steam;

[JsonSerializable(typeof(Cs2Installation.State))]
[JsonSerializable(typeof(SteamCmdJournal))]
[JsonSerializable(typeof(SteamCmdGateCommand))]
internal sealed partial class SteamCmdJsonContext : JsonSerializerContext;
