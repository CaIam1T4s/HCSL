using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IServerRuntimeService
{
    Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop);
    void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null);
}
