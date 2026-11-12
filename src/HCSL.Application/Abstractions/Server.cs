using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IServerRuntimeService
{
    Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop);
    /// <summary>Deploy the selected preset without launching CS2. Frontends must not run deployment themselves.</summary>
    Task<DeploymentReceipt> DeployAsync(WorkspaceRequest workspace, Guid instanceId, IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default)
        => Task.FromException<DeploymentReceipt>(new ServerException("HCSL-SERVER-DEPLOY-UNAVAILABLE", "Deployment-only is unavailable in this runtime."));

    /// <summary>Read-only deployment plan for the GUI preview; never writes the runtime.</summary>
    Task<DeploymentPlan> PlanDeploymentAsync(WorkspaceRequest workspace, Guid instanceId, CancellationToken ct = default)
        => Task.FromException<DeploymentPlan>(new ServerException("HCSL-SERVER-DEPLOY-UNAVAILABLE", "Deployment planning is unavailable in this runtime."));

    /// <summary>Recover an interrupted deployment journal. Returns the RuntimeDeploymentService outcome text.</summary>
    Task<string> RecoverDeploymentAsync(WorkspaceRequest workspace, Guid instanceId, IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default)
        => Task.FromException<string>(new ServerException("HCSL-SERVER-DEPLOY-UNAVAILABLE", "Deployment recovery is unavailable in this runtime."));

    /// <summary>Point-in-time online roster. Queried out of band (RCON); never derived from the log ring.</summary>
    Task<PlayerRoster> QueryPlayersAsync(Guid runId, CancellationToken ct = default)
        => Task.FromException<PlayerRoster>(new ServerException("HCSL-PLAYERS-UNAVAILABLE", "Online roster is unavailable in this runtime."));

    Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
        => Task.FromResult(new ServerConsoleReceipt(command.RunId, command.CommandId, ConsoleDelivery.Rejected, "HCSL-CONSOLE-UNAVAILABLE"));
    void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null);
}
