namespace HCSL.Core;

public enum DeploymentCopyMode { Reflink, FullCopy }
public sealed record DeploymentOptions(Guid InstanceId, DeploymentCopyMode CopyMode = DeploymentCopyMode.Reflink, long? ExpectedRevision = null);
public sealed record DeploymentMapping(string RuntimePath, string PersistentPath);
public sealed record DeploymentPlan(Guid InstanceId, long InstanceRevision, Guid PresetId, long PresetRevision,
    string BuildId, string RuntimePath, DeploymentCopyMode CopyMode, int FileCount, long LogicalBytes,
    long MinimumLibraryFreeBytes, long MinimumDataFreeBytes, DeploymentMapping[] PersistentMappings,
    string[] Notes, string? RotationGroup = null, string? RotationHash = null, string? RotationCollection = null,
    string? PluginSetHash = null);
public sealed record DeploymentReceipt(int FormatVersion, Guid OperationId, string Status, Guid DataId, Guid LibraryId,
    Guid InstanceId, Guid PresetId, long InstanceRevision, long PresetRevision, string BuildId,
    DeploymentCopyMode CopyMode, DateTimeOffset CreatedAt, DeploymentMapping[] PersistentMappings,
    string? RotationGroup = null, string? RotationHash = null, string? RotationCollection = null,
    string? PluginSetHash = null);
public sealed record DeploymentEvent(string Stage, int FilesCompleted = 0, int TotalFiles = 0);
public sealed class DeploymentException(string code, string message) : Exception(message) { public string Code { get; } = code; }
