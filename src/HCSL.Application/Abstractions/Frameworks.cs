using HCSL.Core;
namespace HCSL.Application.Abstractions;

/// <summary>
/// Curated framework installer. Version checks are read-only; installs download only from the
/// catalog's official releases and write a normal plugin package that the existing deploy path consumes.
/// </summary>
public interface IFrameworkCatalog
{
    Task<FrameworkCatalogResult> CatalogAsync(WorkspaceRequest workspace, FrameworkCatalogCommand command, CancellationToken ct = default);
}

public interface IFrameworkManager
{
    Task<FrameworkCheckResult> CheckAsync(WorkspaceRequest workspace, string? frameworkId = null, CancellationToken ct = default);
    Task<FrameworkInstallResult> InstallAsync(WorkspaceRequest workspace, FrameworkInstallCommand command, CancellationToken ct = default,
        IProgress<TransferProgress>? progress = null);
}
