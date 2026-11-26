using HCSL.Contracts;
using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Framework admin lists: read, edit with hash conflict detection, and reload.</summary>
public sealed class AdminClient(IDaemonClient client)
{
    public async Task<AdminResult> ListAsync(Guid instanceId, Guid presetId, int offset = 0, int limit = 0,
        string? query = null, CancellationToken ct = default) =>
        await SendAsync(new(AdminAction.List, instanceId, presetId, Offset: offset, Limit: limit, Query: query), ct);

    public async Task<AdminResult> ApplyAsync(AdminCommand command, CancellationToken ct = default) =>
        await SendAsync(command with { Action = AdminAction.Apply }, ct);

    public async Task<AdminResult> ReloadAsync(Guid instanceId, Guid presetId, CancellationToken ct = default) =>
        await SendAsync(new(AdminAction.Reload, instanceId, presetId), ct);

    private async Task<AdminResult> SendAsync(AdminCommand command, CancellationToken ct)
    {
        var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
        if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
        var response = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Admin,
            Options: new(hello.Defaults), AdminJson: WorkspaceJsonContext.Encode(command)), ct);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
        return WorkspaceJsonContext.Decode<AdminResult>(response.AdminResultJson ?? "null")
            ?? throw new IOException("HCSL-ADMIN-REQUEST: The daemon returned no admin result.");
    }
}
