namespace HCSL.Core;

public enum DeploymentCopyMode { Reflink, FullCopy }
public enum DeploymentMappingKind { Link, Materialized }
public sealed record DeploymentOptions(Guid InstanceId, DeploymentCopyMode CopyMode = DeploymentCopyMode.Reflink, long? ExpectedRevision = null);
public sealed record DeploymentMapping(string RuntimePath, string PersistentPath,
    DeploymentMappingKind Kind = DeploymentMappingKind.Link, bool SeedDefaults = false);
public sealed record DeploymentPlan(Guid InstanceId, long InstanceRevision, Guid PresetId, long PresetRevision,
    string BuildId, string RuntimePath, DeploymentCopyMode CopyMode, int FileCount, long LogicalBytes,
    long MinimumLibraryFreeBytes, long MinimumDataFreeBytes, DeploymentMapping[] PersistentMappings,
    string[] Notes, string? RotationGroup = null, string? RotationHash = null, string? RotationCollection = null,
    string? PluginSetHash = null, string? Framework = null,
    FrameworkReadinessProfile FrameworkReadiness = FrameworkReadinessProfile.Standard);
public sealed record DeploymentReceipt(int FormatVersion, Guid OperationId, string Status, Guid DataId, Guid LibraryId,
    Guid InstanceId, Guid PresetId, long InstanceRevision, long PresetRevision, string BuildId,
    DeploymentCopyMode CopyMode, DateTimeOffset CreatedAt, DeploymentMapping[] PersistentMappings,
    string? RotationGroup = null, string? RotationHash = null, string? RotationCollection = null,
    string? PluginSetHash = null, string? Framework = null,
    FrameworkReadinessProfile FrameworkReadiness = FrameworkReadinessProfile.Standard,
    DeployedPluginPackage[]? PluginManifest = null);
public sealed record DeployedPluginPackage(string Id, string Version, string Hash, bool FrameworkPayload, string? Framework,
    DeployedPluginFile[] Files);
public sealed record DeployedPluginFile(string Path, string Sha256);
public sealed record DeploymentEvent(string Stage, int FilesCompleted = 0, int TotalFiles = 0);

/// <summary>Startup baseline maintenance: read-only status, or re-verify the installed build and record it.</summary>
public enum BaselineAction { Status, Refresh }

public sealed record BaselineCommand(BaselineAction Action);

/// <summary>Source is Local (library baseline), Embedded (shipped baseline) or None (must refresh before deploying).</summary>
public sealed record BaselineResult(string BuildId, string Source, string? LocalBuildId = null, string? LocalBaselinePath = null);
public sealed class DeploymentException(string code, string message) : Exception(message) { public string Code { get; } = code; }
