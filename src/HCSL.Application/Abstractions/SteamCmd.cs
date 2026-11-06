namespace HCSL.Application.Abstractions;

public enum SteamCmdStage
{
    Preparing, DownloadingTool, Starting, SelfUpdating, Ready, Authenticating,
    WaitingForPassword, WaitingForGuardCode, WaitingForMobileApproval,
    Authenticated, DownloadingContent, ValidatingContent, InstallingContent, Cancelling, Succeeded, Failed, Cancelled, TimedOut, RecoveryRequired
}
public enum SteamCmdInstallMode { InstallOrUpdate, Validate }
public sealed record SteamCmdInstallRequest(SteamCmdProbeRequest Session, SteamCmdInstallMode Mode = SteamCmdInstallMode.InstallOrUpdate);
public enum SteamCmdChallengeKind { Password, GuardCode }
public sealed record SteamCmdChallenge(Guid OperationId, Guid ChallengeId, SteamCmdChallengeKind Kind);
public sealed record SteamCmdEvent(Guid OperationId, SteamCmdStage Stage, string Message, double? Percent = null);
public sealed record SteamCmdProbeRequest(WorkspaceRequest Workspace, string? ExecutablePath = null,
    bool InstallIfMissing = false, string? AccountName = null, TimeSpan? Timeout = null);
public sealed record SteamCmdResult(Guid OperationId, SteamCmdStage Stage, string Code, int? ExitCode = null);

/// <summary>Responses are ephemeral; implementations must never log or persist their contents.</summary>
public interface ISteamCmdInteraction
{
    ValueTask<string> RespondAsync(SteamCmdChallenge challenge, CancellationToken cancellationToken);
}

public interface ISteamCmdService
{
    Task<SteamCmdResult> ProbeAsync(SteamCmdProbeRequest request,
        IProgress<SteamCmdEvent>? events = null, ISteamCmdInteraction? interaction = null,
        CancellationToken cancellationToken = default);

    Task<SteamCmdResult> InstallAsync(SteamCmdInstallRequest request,
        IProgress<SteamCmdEvent>? events = null, ISteamCmdInteraction? interaction = null,
        CancellationToken cancellationToken = default);

    // Recovery never kills a process or guesses that a PID is stale. Busy/unknown sessions are refused.
    void Recover(WorkspaceRequest workspace, string? executablePath = null);
}

public sealed class SteamCmdException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
