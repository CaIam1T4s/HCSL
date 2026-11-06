using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IInstanceStore { InstanceCatalog Execute(WorkspaceRequest workspace, InstanceCommand command); }
