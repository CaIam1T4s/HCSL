namespace HCSL.Application.Abstractions;

/// <summary>All three roots must be disjoint. Display names are never path components.</summary>
public sealed record WorkspaceRequest(string SettingsRoot, string DataRoot, string LibraryRoot);
public sealed record WorkspaceLayout(string SettingsRoot, string DataRoot, string LibraryRoot,
    string SteamCmdRoot, string InstancesRoot, string ServerRoot, string RuntimeRoot);
public sealed record WorkspaceIdentity(Guid SettingsId, Guid DataId, Guid LibraryId);
public sealed record WorkspaceCapacity(long DataAvailableBytes, long LibraryAvailableBytes);

/// <summary>Dispose only when every operation using these directories has finished.</summary>
public interface IWorkspaceLease : IDisposable
{
    WorkspaceLayout Layout { get; }
    WorkspaceIdentity Identity { get; }
    WorkspaceCapacity Capacity { get; }
}

public interface IWorkspaceManager
{
    IWorkspaceLease Acquire(WorkspaceRequest request);
}

public sealed class WorkspaceException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
