using HCSL.Core;
namespace HCSL.Application.Abstractions;

/// <summary>
/// Curated framework installer. Version checks are read-only; installs download only from the
/// catalog's official releases and write a normal plugin package that the existing deploy path consumes.
/// </summary>
public interface IFrameworkManager
{
    Task<FrameworkCheckResult> CheckAsync(WorkspaceRequest workspace, CancellationToken ct = default);
    Task<FrameworkInstallResult> InstallAsync(WorkspaceRequest workspace, FrameworkInstallCommand command, CancellationToken ct = default);
}
