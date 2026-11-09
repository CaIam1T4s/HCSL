using HCSL.Core;

namespace HCSL.Frontend;

/// <summary>The single write rule for preset-owned data (plugins, CFG, map pools).</summary>
internal static class PresetEditGate
{
    internal static bool CanWrite(ServerController server) =>
        server.Connected && !server.Busy && !server.Active && server.State != ServerState.RecoveryRequired;
}
