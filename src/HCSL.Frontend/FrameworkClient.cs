using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Framework catalog checks and installs; downloads get a long deadline, everything else stays short.</summary>
public sealed class FrameworkClient(IDaemonClient client)
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(25);

    public async Task<FrameworkCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.FrameworkCheck,
            Options: new(hello.Defaults)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<FrameworkCheckResult>(response.FrameworkCheckJson ?? "null")
            ?? throw new IOException("Missing framework check result.");
    }

    public async Task<FrameworkInstallResult> InstallAsync(FrameworkInstallCommand command, CancellationToken ct = default)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.FrameworkInstall,
            Options: new(hello.Defaults), FrameworkInstallJson: JsonSerializer.Serialize(command)), InstallTimeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return JsonSerializer.Deserialize<FrameworkInstallResult>(response.FrameworkInstallResultJson ?? "null")
            ?? throw new IOException("Missing framework install result.");
    }
}
