using HCSL.Core;
namespace HCSL.Application.Abstractions;
public interface IPresetMapPoolStore
{
    MapPoolResult Execute(WorkspaceRequest workspace, MapPoolCommand command);
}
