using System.Text;

namespace HCSL.Core;

/// <summary>
/// Remove one client from the running server. Identified by userid, optionally pinned to a SteamID64 so a
/// recycled slot can never remove the wrong person.
/// </summary>
public sealed record ServerKickCommand(Guid InstanceId, Guid RunId, int UserId, string? SteamId64 = null, string Reason = "")
{
    public override string ToString() => $"Kick {UserId} for {RunId}";
}

/// <summary>Outcome of one kick. The roster is the post-kick snapshot when it could be read.</summary>
public sealed record ServerKickResult(bool Written, string Code, int UserId, string? SteamId64, string Command,
    RosterPlayer? Target = null, PlayerRoster? Roster = null);

public static class ServerKickRules
{
    public const int MaxReasonLength = 128;

    /// <summary>CS2 treats the reason as free text on one console line; control characters are not allowed.</summary>
    public static string Reason(string? reason)
    {
        var text = (reason ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Length > MaxReasonLength || text.Any(char.IsControl))
            throw new ServerException("HCSL-KICK-REASON", "Kick reason must be one line of at most 128 visible characters.");
        if (Encoding.UTF8.GetByteCount(text) > 512)
            throw new ServerException("HCSL-KICK-REASON", "Kick reason is too long for one console line.");
        return text;
    }

    /// <summary>
    /// CS2's <c>kickid</c> takes a <b>userid</b> in this position, not a SteamID64: passing the 64-bit id makes the
    /// engine parse it as a userid and fail (<c>userid "…" not found</c>, observed on build 2000943). The SteamID
    /// therefore stays an identity pin verified against the live roster right before writing, never the target.
    /// </summary>
    public static string Command(ServerKickCommand command)
    {
        if (command.RunId == Guid.Empty) throw new ServerException("HCSL-KICK-STATE", "A running server is required.");
        if (SteamId(command.SteamId64) is null && command.UserId < 0)
            throw new ServerException("HCSL-KICK-TARGET", "A userid from the current roster is required to kick a client.");
        if (command.UserId < 0)
            throw new ServerException("HCSL-KICK-TARGET", "The roster has no userid for this player; refresh it before kicking.");
        var target = command.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reason = Reason(command.Reason);
        return reason.Length == 0 ? "kickid " + target : "kickid " + target + " " + reason;
    }

    /// <summary>Resolve the roster row this command intends to remove; never guess a different player.</summary>
    public static RosterPlayer Target(PlayerRoster roster, ServerKickCommand command)
    {
        var steamId = SteamId(command.SteamId64);
        var player = roster.Players.FirstOrDefault(p => p.UserId == command.UserId)
            ?? throw new ServerException("HCSL-KICK-TARGET", "That userid is not connected any more; refresh the roster.");
        if (player.IsBot) throw new ServerException("HCSL-KICK-TARGET", "Bots have no SteamID and are not kicked from this page.");
        if (steamId is not null && !string.Equals(player.SteamId64, steamId, StringComparison.Ordinal))
            throw new ServerException("HCSL-KICK-TARGET", "The userid now belongs to another client; refresh the roster before kicking.");
        return player;
    }

    public static string? SteamId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length != 17 || !text.All(char.IsAsciiDigit) || !text.StartsWith("7656", StringComparison.Ordinal))
            throw new ServerException("HCSL-KICK-TARGET", "SteamID64 must be 17 digits starting with 7656.");
        return text;
    }
}
