namespace HCSL.Core;
public enum ServerState { Idle, Preparing, Starting, LoadingMap, Running, Stopping, Stopped, Failed, RecoveryRequired }
public enum ServerAction { Start, Status, Logs, Stop, Recover }
public sealed record ServerCommand(ServerAction Action, Guid InstanceId = default, Guid? RunId = null, long AfterSequence = 0, int StartTimeoutSeconds = 120, bool AutoDeploy = false, bool AllowLowMemory = false);
public sealed record ServerStartOptions(Guid InstanceId, int StartTimeoutSeconds = 120, bool AutoDeploy = false, bool AllowLowMemory = false);
public sealed record ServerLogLine(long Sequence, string Text);
public sealed record ServerSnapshot(Guid? RunId, Guid? InstanceId, ServerState State, bool Active, string Code,
    bool MapLoaded, bool CleanupConfirmed, int? ExitCode, long NextSequence, bool HistoryTruncated, ServerLogLine[] Lines, string? Detail = null, string Mode = "LoopbackLanValidation", bool EffectiveVac = false, string? Map = null, bool MapHistorical = false,
    string BindAddress = "127.0.0.1", NetworkAccessMode Access = NetworkAccessMode.Loopback, ServerNetworkReadiness? NetworkReadiness = null, string[]? ConnectionAddresses = null);
public sealed record ServerRuntimeEvent(ServerState State, string Code, string? Log = null, string? Map = null, ServerNetworkReadiness? NetworkReadiness = null);
public sealed record ServerOutcome(string Code, int? ExitCode, bool MapLoaded, bool CleanupConfirmed);
/// <summary>The map a finished run was observed to load, kept so a fresh daemon can still name it.</summary>
public sealed record ServerLastRun(Guid InstanceId, Guid RunId, string Map, DateTimeOffset RecordedAt);
public sealed class ServerException(string code, string message) : Exception(message) { public string Code { get; } = code; }
