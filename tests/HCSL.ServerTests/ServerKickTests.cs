using HCSL.Core;
namespace HCSL.ServerTests;

/// <summary>Kick rules: engine-native command text, target pinning and refusal cases.</summary>
public static class ServerKickTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (ServerException e) when (e.Code == code) { }
        }

        var run = Guid.NewGuid();
        var players = new[]
        {
            new RosterPlayer(0, "Bank", true, "", "active", "BOT", 0),
            new RosterPlayer(2, "Player", false, "192.168.1.10:61466", "active", "00:12", 1, "76561198000000000"),
            new RosterPlayer(3, "Other", false, "192.168.1.10:50000", "active", "00:05", 2, "76561198000000001")
        };
        var roster = new PlayerRoster(DateTimeOffset.UtcNow, players);

        // Command text is exactly what `kickid` accepts; the reason is trimmed and optional.
        Check(ServerKickRules.Command(new(Guid.Empty, run, 2, null, "  afk  ")) == "kickid 2 afk", "Userid command.");
        Check(ServerKickRules.Command(new(Guid.Empty, run, 2)) == "kickid 2", "Command without reason.");
        // CS2 rejects a SteamID64 in kickid's target slot, so the userid is always used; the SteamID stays a pin.
        Check(ServerKickRules.Command(new(Guid.Empty, run, 2, "76561198000000000", "cheating")) == "kickid 2 cheating", "Userid must win over SteamID.");
        Reject(() => ServerKickRules.Command(new(Guid.Empty, run, -1, "76561198000000000", "cheating")), "HCSL-KICK-TARGET");

        Reject(() => ServerKickRules.Command(new(Guid.Empty, Guid.Empty, 2)), "HCSL-KICK-STATE");
        Reject(() => ServerKickRules.Command(new(Guid.Empty, run, -1)), "HCSL-KICK-TARGET");
        Reject(() => ServerKickRules.Command(new(Guid.Empty, run, 2, "12345")), "HCSL-KICK-TARGET");
        Reject(() => ServerKickRules.Command(new(Guid.Empty, run, 2, "12345678901234567")), "HCSL-KICK-TARGET");
        Reject(() => ServerKickRules.Command(new(Guid.Empty, run, 2, null, new string('a', ServerKickRules.MaxReasonLength + 1))), "HCSL-KICK-REASON");
        Reject(() => ServerKickRules.Command(new(Guid.Empty, run, 2, null, "two\nlines")), "HCSL-KICK-REASON");

        // Target resolution never guesses: a recycled userid or a bot is refused.
        Check(ServerKickRules.Target(roster, new(Guid.Empty, run, 2)).Name == "Player", "Target lookup.");
        Check(ServerKickRules.Target(roster, new(Guid.Empty, run, 2, "76561198000000000")).SteamId64 == "76561198000000000", "Pinned target lookup.");
        Reject(() => ServerKickRules.Target(roster, new(Guid.Empty, run, 9)), "HCSL-KICK-TARGET");
        Reject(() => ServerKickRules.Target(roster, new(Guid.Empty, run, 0)), "HCSL-KICK-TARGET");
        Reject(() => ServerKickRules.Target(roster, new(Guid.Empty, run, 2, "76561198000000001")), "HCSL-KICK-TARGET");

        // SteamID validation matches what the roster can hold.
        Check(ServerKickRules.SteamId(null) is null && ServerKickRules.SteamId("  ") is null, "Blank SteamID is not an error.");
        Check(ServerKickRules.SteamId(" 76561198000000000 ") == "76561198000000000", "SteamID trimming.");
    }
}
