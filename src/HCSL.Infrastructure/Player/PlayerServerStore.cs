using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;

namespace HCSL.Infrastructure.Player;

public sealed class PlayerServerStore(string root, IPlayerSecretStore secrets) : IPlayerServerStore
{
    private string Catalog => Path.Combine(root, "servers.json");
    private FileStream Lease()
    {
        Directory.CreateDirectory(root);
        return new FileStream(Path.Combine(root, ".servers.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private async Task<List<PlayerServer>> Read(CancellationToken ct)
    {
        if (!File.Exists(Catalog)) return [];
        try
        {
            var rows = JsonSerializer.Deserialize<List<PlayerServer>>(await File.ReadAllTextAsync(Catalog, ct))
                ?? throw new JsonException();
            if (rows.Any(x => x is null || x.Id == Guid.Empty) || rows.Select(x => x.Id).Distinct().Count() != rows.Count)
                throw new JsonException();
            return rows.Select(PlayerServerRules.Normalize).ToList();
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new IOException("HCSL-CLIENT-CATALOG: Server list is invalid; not overwritten."); }
    }
    private async Task Write(List<PlayerServer> rows, CancellationToken ct)
    {
        var temp = Catalog + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, Catalog, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<IReadOnlyList<PlayerServer>> LoadAsync(CancellationToken ct = default)
    { using var lease = Lease(); return await Read(ct); }
    public async Task SaveAsync(PlayerServer server, string? password, CancellationToken ct = default)
    {
        server = PlayerServerRules.Normalize(server);
        if (password is not null) PlayerServerRules.Password(password);
        using var lease = Lease();
        var rows = await Read(ct);
        var old = rows.FirstOrDefault(x => x.Id == server.Id);
        if (server.Id == Guid.Empty) server = server with { Id = Guid.NewGuid() };
        server = server with { HasPassword = password is null ? old?.HasPassword == true : password.Length > 0 };
        // Empty explicitly clears; null preserves the stored secret. Never serialize a password.
        if (password is { Length: > 0 }) await secrets.WriteAsync(server.Id, password, ct);
        rows.RemoveAll(x => x.Id == server.Id);
        rows.Add(server);
        await Write(rows, ct);
        if (password is { Length: 0 } && old?.HasPassword == true) await secrets.DeleteAsync(server.Id, ct);
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = Lease();
        var rows = await Read(ct);
        var old = rows.FirstOrDefault(x => x.Id == id);
        rows.RemoveAll(x => x.Id == id);
        await Write(rows, ct);
        if (old?.HasPassword == true) await secrets.DeleteAsync(id, ct);
    }
    public Task<string> PasswordAsync(Guid id, CancellationToken ct = default) => secrets.ReadAsync(id, ct);
}
