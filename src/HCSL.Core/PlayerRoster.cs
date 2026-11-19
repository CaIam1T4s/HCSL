using System.Globalization;
using System.Text.RegularExpressions;

namespace HCSL.Core;

/// <summary>One client currently reported by the server's own status output.</summary>
/// <param name="UserId">Engine userid (slot); also what <c>kickid</c> accepts.</param>
/// <param name="Address">Remote address as printed by status; bots and pending clients have none.</param>
/// <param name="SteamId64">Resolved from the server log stream, never from status.</param>
public sealed record RosterPlayer(int UserId, string Name, bool IsBot, string Address, string State,
    string Connected, int Ping, string? SteamId64 = null);

/// <summary>A point-in-time roster. Absence from this list is the only proof that a client left.</summary>
public sealed record PlayerRoster(DateTimeOffset QueriedAt, RosterPlayer[] Players, string Code = "HCSL-PLAYERS-OK")
{
    public int Humans => Players.Count(p => !p.IsBot);
    public int Bots => Players.Count(p => p.IsBot);
}

/// <summary>
/// Parsing rules for CS2's <c>status</c> players table and for the two log lines that carry a SteamID.
/// Verified against a live server: status has no SteamID, and its rows may include a pseudo-client
/// (<c>65535 [NoChan] ... challenging ... ''</c>) that must never be shown as a player.
/// </summary>
public static class PlayerRosterRules
{
    public const long SteamId64Base = 76561197960265728;
    public const int MaxPlayers = 64;
    private const string PlayersHeader = "---------players--------";

    private static readonly Regex SteamNet = new(
        "Accepting Steam Net connection .*?\\bsteamid:(?<id>\\d{17})@(?<addr>[^\\s]+)\\s*$", RegexOptions.CultureInvariant);

    // SV:  "name<uid><[U:1:account]><>" STEAM USERID validated
    private static readonly Regex Validated = new(
        "\"(?<name>[^\"<]*)<(?<uid>\\d+)><\\[U:1:(?<account>\\d+)\\]><>\"\\s+STEAM USERID validated", RegexOptions.CultureInvariant);

    // id  time  ping  loss  state  rate [adr]  'name' — the pseudo-client fails the time column on purpose.
    // CS2 prints mm:ss under an hour and h:mm:ss after it, so both forms must parse.
    private static readonly Regex Row = new(
        "^\\s*(?<id>\\d{1,5})\\s+(?<time>BOT|\\d{1,4}:\\d{2}(?::\\d{2})?)\\s+(?<ping>\\d{1,5})\\s+(?<loss>\\d{1,5})\\s+(?<state>\\S+)\\s+(?<rate>\\d{1,12})(?:\\s+(?<adr>[^\\s']+))?\\s+'(?<name>[^']*)'\\s*$",
        RegexOptions.CultureInvariant);

    public static string SteamId64FromAccount(long accountId) =>
        (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);

    /// <summary><c>Accepting Steam Net connection #… steamid:&lt;64&gt;@ip:port</c>: the earliest reliable identity.</summary>
    public static bool TrySteamNet(string line, out string steamId64, out string address)
    {
        steamId64 = ""; address = "";
        if (!line.Contains("Accepting Steam Net connection", StringComparison.Ordinal)) return false;
        var match = SteamNet.Match(line);
        if (!match.Success) return false;
        steamId64 = match.Groups["id"].Value;
        address = match.Groups["addr"].Value;
        return true;
    }

    /// <summary>The engine's validated line confirms userid identity and the account id independently.</summary>
    public static bool TryValidated(string line, out string steamId64, out int userId)
    {
        steamId64 = ""; userId = -1;
        if (!line.Contains("STEAM USERID validated", StringComparison.Ordinal)) return false;
        var match = Validated.Match(line);
        if (!match.Success || !int.TryParse(match.Groups["uid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId)) return false;
        if (!long.TryParse(match.Groups["account"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var account)) return false;
        steamId64 = SteamId64FromAccount(account);
        return true;
    }

    /// <summary>Player rows between the players header and <c>#end</c>; anything else is ignored, never guessed.</summary>
    public static RosterPlayer[] ParsePlayers(IEnumerable<string> lines)
    {
        var players = new List<RosterPlayer>();
        var inTable = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!inTable)
            {
                if (trimmed == PlayersHeader) inTable = true;
                continue;
            }
            if (trimmed.Length == 0) continue;
            if (trimmed == "#end") break;
            if (players.Count >= MaxPlayers) break;
            var match = Row.Match(line);
            // Column header, the pseudo-client and any trailing block text simply do not match.
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)) continue;
            players.Add(new(userId, match.Groups["name"].Value, match.Groups["time"].Value == "BOT",
                match.Groups["adr"].Success ? match.Groups["adr"].Value : "", match.Groups["state"].Value,
                match.Groups["time"].Value, int.Parse(match.Groups["ping"].Value, CultureInfo.InvariantCulture)));
        }
        return [.. players];
    }
}
