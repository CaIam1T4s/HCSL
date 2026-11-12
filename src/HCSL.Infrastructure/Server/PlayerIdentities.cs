namespace HCSL.Infrastructure.Server;

/// <summary>
/// Bounded, in-memory SteamID mapping built from the server's own log lines. Never persisted:
/// status carries no SteamID, so identity is learned as clients connect and is only valid for one run.
/// </summary>
internal sealed class PlayerIdentities
{
    private const int Capacity = 256;
    private readonly Dictionary<string, string> byAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> byUser = [];
    private readonly List<long> order = [];
    private long sequence;

    internal void BindAddress(string address, string steamId64)
    {
        if (!IsSteamId64(steamId64) || address.Length == 0) return;
        Prune();
        if (!byAddress.ContainsKey(address)) order.Add(++sequence);
        byAddress[address] = steamId64;
    }

    internal void BindUser(int userId, string steamId64)
    {
        if (!IsSteamId64(steamId64) || userId < 0) return;
        Prune();
        byUser[userId] = steamId64;
    }

    /// <summary>Address is exact (verified against a live server); userid is the fallback when addresses drift.</summary>
    internal string? Resolve(string address, int userId)
    {
        if (address.Length > 0 && byAddress.TryGetValue(address, out var known)) return known;
        return byUser.TryGetValue(userId, out var other) ? other : null;
    }

    private void Prune()
    {
        if (byAddress.Count + byUser.Count < Capacity) return;
        byAddress.Clear();
        byUser.Clear();
        order.Clear();
    }

    private static bool IsSteamId64(string value) =>
        value.Length == 17 && value.All(char.IsAsciiDigit) && value.StartsWith("7656", StringComparison.Ordinal);
}
