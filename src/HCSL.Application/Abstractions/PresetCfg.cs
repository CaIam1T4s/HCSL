using HCSL.Core;
namespace HCSL.Application.Abstractions;

public interface IPresetCfgStore
{
    PresetCfgResult Execute(WorkspaceRequest workspace, PresetCfgCommand command);
}
