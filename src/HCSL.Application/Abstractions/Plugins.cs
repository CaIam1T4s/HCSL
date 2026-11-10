using HCSL.Core;
namespace HCSL.Application.Abstractions;

public interface IPluginStore
{
    PluginResult Execute(WorkspaceRequest workspace, PluginCommand command);
}

/// <summary>Copy an extracted release into the plugin library and optionally select it.</summary>
public interface IPluginImporter
{
    PluginImportResult Execute(WorkspaceRequest workspace, PluginImportCommand command);
    PluginImportPreview Probe(WorkspaceRequest workspace, PluginImportProbeCommand command);
}
