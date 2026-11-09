using HCSL.Core;
namespace HCSL.Application.Abstractions;

public interface IPluginStore
{
    PluginResult Execute(WorkspaceRequest workspace, PluginCommand command);
}
