using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IServerRuntimeService
{
    Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop);
    Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
        => Task.FromResult(new ServerConsoleReceipt(command.RunId, command.CommandId, ConsoleDelivery.Rejected, "HCSL-CONSOLE-UNAVAILABLE"));
    void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null);
}
