using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HCSL.Core;
using HCSL.Infrastructure.Server;
namespace HCSL.ServerTests;

/// <summary>Roster parsing and identity join use the exact lines observed on a live CS2 server.</summary>
public static class PlayerRosterTests
{
    public static async Task RunAsync()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }

        // --- SteamID sources: status has none, the log carries two independent forms ---
        const string steamNet = "Accepting Steam Net connection #1232986315 UDP steamid:76561198000000000@192.168.1.10:54456";
        Check(PlayerRosterRules.TrySteamNet(steamNet, out var netId, out var netAddress)
            && netId == "76561198000000000" && netAddress == "192.168.1.10:54456", "SteamNet line not parsed.");
        Check(!PlayerRosterRules.TrySteamNet("Accepting Steam Net connection #1 UDP steamid:notanid@1.2.3.4:5", out _, out _), "Bad SteamID accepted.");
        const string validated = "SV:  \"Player<2><[U:1:39734272]><>\" STEAM USERID validated";
        Check(PlayerRosterRules.TryValidated(validated, out var validatedId, out var validatedUser)
            && validatedId == "76561198000000000" && validatedUser == 2, "Validated line not parsed.");
        Check(PlayerRosterRules.SteamId64FromAccount(39734272) == "76561198000000000", "SteamID64 math.");
        Check(!PlayerRosterRules.TryValidated("Client 2 'Player' signon state SIGNONSTATE_SPAWN -> SIGNONSTATE_FULL", out _, out _), "Signon line treated as identity.");

        // --- status players table, including the pseudo-client row that must never become a player ---
        string[] block =
        [
            "Server:  Running [192.168.1.11:27016]",
            "---------players--------",
            "  id     time ping loss      state   rate adr name",
            "   0      BOT    0    0     active      0 'Bank'",
            "65535 [NoChan]    0    0 challenging      0unknown ''",
            "   2    00:12    1    0   spawning 786432 192.168.1.10:54456 'Player'",
            "#end"
        ];
        var players = PlayerRosterRules.ParsePlayers(block);
        Check(players.Length == 2, $"Pseudo-client or bot row mis-parsed: got {players.Length} rows.");
        Check(players[0].IsBot && players[0].Name == "Bank" && players[0].UserId == 0, "Bot row.");
        var human = players.Single(p => !p.IsBot);
        Check(human.UserId == 2 && human.Name == "Player" && human.Address == "192.168.1.10:54456"
            && human.State == "spawning" && human.SteamId64 is null, "Human row.");
        Check(PlayerRosterRules.ParsePlayers(["---------players--------", "  id     time ping loss      state   rate adr name"]).Length == 0, "Empty table.");
        Check(PlayerRosterRules.ParsePlayers(["no table here"]).Length == 0, "Missing table invented rows.");

        // --- identity join: address first, userid as the fallback ---
        var identities = new PlayerIdentities();
        identities.BindAddress("192.168.1.10:54456", "76561198000000000");
        Check(identities.Resolve("192.168.1.10:54456", 2) == "76561198000000000", "Address join failed.");
        Check(identities.Resolve("10.0.0.9:1111", 2) is null, "Userid bound without its validated line.");
        identities.BindUser(2, "76561198000000000");
        Check(identities.Resolve("10.0.0.9:1111", 2) == "76561198000000000", "Userid fallback failed.");
        Check(identities.Resolve("10.0.0.9:1111", 7) is null, "Unknown player resolved to a SteamID.");
        identities.BindUser(7, "76561198000000001");
        Check(identities.Resolve("", 7) == "76561198000000001", "Userid bind failed.");
        identities.BindAddress("1.2.3.4:5", "not-a-steamid");
        Check(identities.Resolve("1.2.3.4:5", 99) is null, "Invalid SteamID stored.");

        // --- roster join as the runtime does it ---
        var roster = new PlayerRoster(DateTimeOffset.UtcNow,
            [.. players.Select(p => p.IsBot ? p : p with { SteamId64 = identities.Resolve(p.Address, p.UserId) })]);
        Check(roster.Humans == 1 && roster.Bots == 1, "Roster counts.");
        Check(roster.Players.Single(p => !p.IsBot).SteamId64 == "76561198000000000", "Roster identity join.");

        // --- RCON client against a fake server (framing, auth failure, large single packet) ---
        var payload = string.Join('\n', block);
        var large = string.Concat(Enumerable.Repeat("cvar_placeholder_value_0123456789\n", 12000)); // ~360 KB, matches cvarlist observation
        await using (var server = new FakeRcon("hcsl-secret", payload, large))
        {
            await using (var good = await RconClient.ConnectAsync("127.0.0.1", server.Port, "hcsl-secret", TimeSpan.FromSeconds(3), default))
            {
                var text = await good.ExecuteAsync("status", TimeSpan.FromSeconds(3), default);
                Check(PlayerRosterRules.ParsePlayers(text.Split('\n')).Length == 2, "RCON status round-trip.");
                var big = await good.ExecuteAsync("cvarlist", TimeSpan.FromSeconds(5), default);
                Check(big.Length > 300_000, $"Large single-packet response truncated: {big.Length} bytes.");
            }
            try
            {
                await using var bad = await RconClient.ConnectAsync("127.0.0.1", server.Port, "wrong", TimeSpan.FromSeconds(3), default);
                throw new Exception("Wrong RCON password was accepted.");
            }
            catch (ServerException e) when (e.Code == "HCSL-PLAYERS-AUTH") { }
        }
        try
        {
            await using var refused = await RconClient.ConnectAsync("127.0.0.1", ClosedPort(), "hcsl-secret", TimeSpan.FromMilliseconds(400), default);
            throw new Exception("RCON connected to a closed port.");
        }
        catch (ServerException e) when (e.Code == "HCSL-PLAYERS-TRANSPORT") { }
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Speaks just enough Source RCON: auth echo, single-packet exec responses.</summary>
    private sealed class FakeRcon : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly string password;
        private readonly string status;
        private readonly string large;
        private readonly CancellationTokenSource stop = new();
        private Task? loop;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public FakeRcon(string password, string status, string large)
        {
            this.password = password; this.status = status; this.large = large;
            listener.Start();
            loop = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    while (true)
                    {
                        var (id, type, body) = await ReadAsync(stream);
                        if (type == 3)
                        {
                            var ok = body == password;
                            await WriteAsync(stream, ok ? id : -1, 2, "");
                            if (!ok) return;
                            continue;
                        }
                        if (type == 2)
                        {
                            await WriteAsync(stream, id, 0, body == "cvarlist" ? large : status);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or EndOfStreamException) { }
            }
        }

        private static async Task<(int Id, int Type, string Body)> ReadAsync(NetworkStream stream)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, default);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            var body = new byte[size];
            await stream.ReadExactlyAsync(body, default);
            return (BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)),
                Encoding.UTF8.GetString(body, 8, body.Length - 10));
        }

        private static async Task WriteAsync(NetworkStream stream, int id, int type, string body)
        {
            var text = Encoding.UTF8.GetBytes(body);
            var bytes = new byte[4 + 4 + 4 + text.Length + 2];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 4 + 4 + text.Length + 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), type);
            text.CopyTo(bytes.AsSpan(12));
            await stream.WriteAsync(bytes, default);
            await stream.FlushAsync(default);
        }

        public ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
