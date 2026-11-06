using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IRuntimeDeploymentService
{
    DeploymentPlan Plan(WorkspaceRequest workspace, DeploymentOptions options, CancellationToken cancellationToken = default);
    DeploymentReceipt Deploy(WorkspaceRequest workspace, DeploymentOptions options, IProgress<DeploymentEvent>? progress = null, CancellationToken cancellationToken = default);
    string Recover(WorkspaceRequest workspace, Guid instanceId);
}
