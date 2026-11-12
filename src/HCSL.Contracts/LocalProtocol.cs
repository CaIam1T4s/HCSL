using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HCSL.Contracts;

public enum RequestKind { Hello, Status, StartProbe, Cancel, SubmitInput, Recover, InstallOrUpdate, ValidateContent, Instances, Server, ServerConsole, PresetCfg, MapPool, Plugin, PluginImport, PluginImportProbe, FrameworkCheck, FrameworkInstall, PluginCfg, Baseline }
public enum ProbeStage
{
    Idle, Preparing, DownloadingTool, Starting, SelfUpdating, Ready, Authenticating,
    WaitingForPassword, WaitingForGuardCode, WaitingForMobileApproval,
    Authenticated, DownloadingContent, ValidatingContent, InstallingContent, Cancelling, Succeeded, Failed, Cancelled, TimedOut, RecoveryRequired
}
public enum ChallengeKind { Password, GuardCode }
public sealed record WorkspacePaths(string SettingsRoot, string DataRoot, string LibraryRoot);
public sealed record ProbeOptions(WorkspacePaths Workspace, string? ExecutablePath = null,
    bool InstallIfMissing = true, string? AccountName = null, int TimeoutSeconds = 600, int InstallTimeoutSeconds = 21600);
public sealed record Challenge(Guid Id, ChallengeKind Kind);
public sealed record OperationEvent(long Revision, ProbeStage Stage, string Message, double? Percent = null);
public sealed record OperationResult(string Code, int? ExitCode);
public sealed record OperationSnapshot(Guid? Id, long Revision, bool Running, ProbeStage Stage,
    string Message, Challenge? Challenge, OperationResult? Result, OperationEvent[] Events, double? Percent = null);
public sealed record LocalRequest(int Version, Guid Id, RequestKind Kind, ProbeOptions? Options = null,
    Guid? OperationId = null, Guid? ChallengeId = null, string? Secret = null, string? InstanceCommandJson = null, string? ServerCommandJson = null, string? ServerConsoleJson = null, string? PresetCfgJson = null, string? MapPoolJson = null, string? PluginJson = null, string? PluginImportJson = null, string? PluginImportProbeJson = null, string? FrameworkInstallJson = null, string? PluginCfgJson = null, string? FrameworkCheckJson = null, string? BaselineJson = null);
public sealed record LocalResponse(int Version, Guid Id, bool Success, string? Code = null,
    string? Message = null, WorkspacePaths? Defaults = null, OperationSnapshot? Snapshot = null, Guid InstanceId = default, int ProcessId = 0, string? InstanceCatalogJson = null, string? ServerSnapshotJson = null,
    string? DaemonBuild = null, string? ConsoleReceiptJson = null, string? PresetCfgResultJson = null, string? MapPoolResultJson = null, string? PluginResultJson = null, string? DeploymentPlanJson = null, string? PluginImportResultJson = null, string? PluginImportPreviewJson = null,
    string? FrameworkCheckJson = null, string? FrameworkInstallResultJson = null, string? PluginCfgResultJson = null, string? BaselineResultJson = null);

public static class LocalProtocol
{
    public const int Version = 4;
    public const int MaxFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }, MaxDepth = 16,
        PropertyNameCaseInsensitive = false
    };
    public static string DefaultPipeName
    {
        get
        {
            var identity = Environment.UserDomainName + "\\" + Environment.UserName;
            return "hcsl-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        }
    }
    public static string PipeName(string? name = null)
    {
        name ??= Environment.GetEnvironmentVariable("HCSL_PIPE_NAME") ?? DefaultPipeName;
        if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9_.-]{1,100}\z")) throw new ArgumentException("Invalid local pipe name.");
        return name;
    }
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        try
        {
            if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("IPC frame too large.");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid IPC frame size.");
        var bytes = new byte[size];
        try
        {
            await stream.ReadExactlyAsync(bytes, ct);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty IPC message.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
