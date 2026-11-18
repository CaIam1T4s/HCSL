using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Framework catalog checks and installs; downloads get a long deadline, everything else stays short.</summary>
public sealed class FrameworkClient(IDaemonClient client)
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(25);

    public async Task<FrameworkCatalogResult> CatalogAsync(FrameworkCatalogCommand command, WorkspacePaths? workspace = null, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.FrameworkCatalog,
            Options: new(workspace ?? hello.Defaults), FrameworkCatalogJson: JsonSerializer.Serialize(command)), CheckTimeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<FrameworkCatalogResult>(response.FrameworkCatalogJson ?? "null")
            ?? throw new IOException("Missing framework catalog result.");
    }

    public async Task<FrameworkCheckResult> CheckAsync(string? id = null, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.FrameworkCheck,
            Options: new(hello.Defaults), FrameworkCheckJson: JsonSerializer.Serialize(new FrameworkCheckCommand(id))), CheckTimeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<FrameworkCheckResult>(response.FrameworkCheckJson ?? "null")
            ?? throw new IOException("Missing framework check result.");
    }

    public async Task<FrameworkInstallResult> InstallAsync(FrameworkInstallCommand command, CancellationToken ct = default)
        => await InstallAsync(command, null, Guid.NewGuid(), ct);

    /// <summary>
    /// Installs with a caller-chosen request id, so the caller can cancel this very request through
    /// <see cref="CancelInstallAsync"/> while it is still running.
    /// </summary>
    public async Task<FrameworkInstallResult> InstallAsync(FrameworkInstallCommand command, WorkspacePaths? workspace, Guid requestId, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, requestId, RequestKind.FrameworkInstall,
            Options: new(workspace ?? hello.Defaults), FrameworkInstallJson: JsonSerializer.Serialize(command)), InstallTimeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<FrameworkInstallResult>(response.FrameworkInstallResultJson ?? "null")
            ?? throw new IOException("Missing framework install result.");
    }

    /// <summary>Asks the host to abort the framework install that was started with this request id.</summary>
    public async Task<TransferSnapshot?> ProgressAsync(Guid requestId, CancellationToken ct = default)
    {
        // Deliberately tiny: the reply carries one transfer record and nothing else.
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.FrameworkProgress,
            OperationId: requestId), ct);
        if (!response.Success || response.TransferJson is null) return null;
        return JsonSerializer.Deserialize<TransferSnapshot>(response.TransferJson);
    }    public async Task CancelInstallAsync(Guid requestId, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Cancel,
            Options: new(hello.Defaults), OperationId: requestId), CheckTimeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
    }
}
