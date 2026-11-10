using HCSL.Core;
namespace HCSL.Frontend;

public sealed partial class HostServerController
{
    public bool CanInstallFramework => !Busy && !Server.Busy && Server.Connected && !Server.Active
        && Server.State is not (ServerState.RecoveryRequired or ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap or ServerState.Stopping);

    public async Task<FrameworkCheckResult> CheckFrameworksAsync()
    {
        if (Busy || !Server.Connected || frameworks is null)
            throw new IOException("HCSL-FRAMEWORK-NET: Host is disconnected or another operation is active.");
        Busy = true; Notify();
        try { return await frameworks.CheckAsync(); }
        finally { Busy = false; Notify(); }
    }

    public async Task<FrameworkInstallResult> InstallFrameworkAsync(string id, string? version = null)
    {
        if (!CanInstallFramework || frameworks is null)
            throw new IOException("HCSL-FRAMEWORK-BUSY: Stop the server/SteamCMD and finish recovery before installing a framework.");
        Busy = true; Notify();
        try { return await frameworks.InstallAsync(new(id, version)); }
        finally { Busy = false; Notify(); }
    }
}
