using HCSL.Core;
namespace HCSL.Application.Abstractions;

/// <summary>Synchronizes package-owned cfg files with a preset; always preview-then-apply.</summary>
public interface IPluginCfgSync
{
    PluginCfgPreview Preview(WorkspaceRequest workspace, PluginCfgCommand command);
    PluginCfgResult Apply(WorkspaceRequest workspace, PluginCfgCommand command);
    PluginCfgPreview CleanupPreview(WorkspaceRequest workspace, PluginCfgCommand command);
    PluginCfgResult CleanupApply(WorkspaceRequest workspace, PluginCfgCommand command);
}
