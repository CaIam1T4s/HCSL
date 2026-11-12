using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Startup baseline status/refresh through the daemon. The UI never reads game files directly.</summary>
public sealed class BaselineClient(IDaemonClient client)
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(10);

    public Task<BaselineResult> StatusAsync(CancellationToken ct = default) => SendAsync(BaselineAction.Status, StatusTimeout, ct);
    public Task<BaselineResult> RefreshAsync(CancellationToken ct = default) => SendAsync(BaselineAction.Refresh, RefreshTimeout, ct);

    private async Task<BaselineResult> SendAsync(BaselineAction action, TimeSpan timeout, CancellationToken ct)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Baseline,
            Options: new(hello.Defaults), BaselineJson: System.Text.Json.JsonSerializer.Serialize(new BaselineCommand(action))), timeout, ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return System.Text.Json.JsonSerializer.Deserialize<BaselineResult>(response.BaselineResultJson ?? "null")
            ?? throw new IOException("Missing baseline result.");
    }
}
