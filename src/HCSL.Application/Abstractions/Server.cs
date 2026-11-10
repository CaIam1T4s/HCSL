using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IServerRuntimeService
{
    Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop);
    /// <summary>Deploy the selected preset without launching CS2. Frontends must not run deployment themselves.</summary>
    Task<DeploymentReceipt> DeployAsync(WorkspaceRequest workspace, Guid instanceId, IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default)
        => Task.FromException<DeploymentReceipt>(new ServerException("HCSL-SERVER-DEPLOY-UNAVAILABLE", "Deployment-only is unavailable in this runtime."));

    Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
        => Task.FromResult(new ServerConsoleReceipt(command.RunId, command.CommandId, ConsoleDelivery.Rejected, "HCSL-CONSOLE-UNAVAILABLE"));
    void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null);
}
