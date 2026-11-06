using HCSL.Core;
using HCSL.Application.Abstractions;
namespace HCSL.Application.Instances;

public sealed class InstanceService(IInstanceStore store)
{
    public InstanceCatalog Execute(WorkspaceRequest workspace, InstanceCommand command) => store.Execute(workspace, command);
}
