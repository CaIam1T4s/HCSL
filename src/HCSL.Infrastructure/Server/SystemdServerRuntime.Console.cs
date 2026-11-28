using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HCSL.Core;
namespace HCSL.Infrastructure.Server;

public sealed partial class SystemdServerRuntime
{
    private readonly ConcurrentDictionary<Guid, ConsoleSession> consoleSessions = new();
    private sealed class ConsoleSession(Process process, CancellationToken stop, Func<string, string> redact)
    {
        internal readonly PlayerIdentities Identities = new();
        internal int GamePort;
        internal string RconPassword = "";
        internal bool RconIssued;
        internal readonly SemaphoreSlim Writer = new(1, 1);
        internal readonly SemaphoreSlim Admission = new(1, 1);
        internal readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> Acks = new();
        internal readonly Dictionary<Guid, ServerConsoleReceipt> History = new();
        internal readonly Dictionary<Guid, string> Queries = new();
        internal long LastSend;
        internal string Display(string text) => ServerConsoleRules.IsSensitive(text)
            ? ServerConsoleRules.Display(text) : ServerConsoleRules.Display(redact(text));
        internal Process Process => process;
        internal CancellationToken Stop => stop;
    }
    /// <summary>
    /// Ask the running server for its own player list over RCON. Returns identity-joined players; the caller
    /// must treat a missing player as "left" only after a later successful query.
    /// </summary>
    public async Task<PlayerRoster> QueryPlayersAsync(Guid runId, CancellationToken ct = default)
    {
        if (!consoleSessions.TryGetValue(runId, out var session) || session.Stop.IsCancellationRequested)
            throw new ServerException("HCSL-PLAYERS-STATE", "The server is not running; start it before listing players.");
        if (session.GamePort == 0) throw new ServerException("HCSL-PLAYERS-STATE", "This run has no game port for RCON.");
        if (!session.RconIssued) await IssueRconPasswordAsync(runId, session, ct);
        Exception? last = null;
        foreach (var host in Candidates())
        {
            try
            {
                await using var rcon = await RconClient.ConnectAsync(host, session.GamePort, session.RconPassword, TimeSpan.FromMilliseconds(700), ct);
                var text = await rcon.ExecuteAsync("status", TimeSpan.FromSeconds(3), ct);
                var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
                var parsed = PlayerRosterRules.ParsePlayers(lines);
                if (parsed.Length == 0 && !text.Contains("---------players--------", StringComparison.Ordinal))
                    throw new ServerException("HCSL-PLAYERS-PARSE", "RCON status output had no players table.");
                var players = parsed
                    .Select(p => p.IsBot ? p : p with { SteamId64 = session.Identities.Resolve(p.Address, p.UserId) })
                    .ToArray();
                return new(DateTimeOffset.UtcNow, players);
            }
            catch (Exception e) when (e is ServerException or OperationCanceledException or IOException or System.Net.Sockets.SocketException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                last = e;
            }
        }
        throw last as ServerException
            ?? new ServerException("HCSL-PLAYERS-TRANSPORT", "No RCON endpoint accepted the connection for this run.");
    }

    /// <summary>RCON binds the game port on a specific address (not loopback), so candidates are probed in order.</summary>
    private static IEnumerable<string> Candidates()
    {
        yield return "127.0.0.1";
        foreach (var address in LocalServerAddresses.Discover()) yield return address;
    }

    private async Task IssueRconPasswordAsync(Guid runId, ConsoleSession session, CancellationToken ct)
    {
        // The console has a 250 ms per-session rate limit; an operator command may have just used it.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var receipt = await SendConsoleAsync(new(runId, Guid.NewGuid(), "rcon_password " + session.RconPassword), timeout.Token);
            if (receipt.Delivery == ConsoleDelivery.Written) { session.RconIssued = true; return; }
            if (receipt.Code != "HCSL-CONSOLE-RATE")
                throw new ServerException("HCSL-PLAYERS-STATE", "The RCON password could not be applied to this run (" + receipt.Code + ").");
            await Task.Delay(300, ct);
        }
        throw new ServerException("HCSL-PLAYERS-STATE", "The RCON password could not be applied: the console stayed busy (HCSL-CONSOLE-RATE).");
    }

    public async Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
    {
        var query = ServerConsoleRules.ValidateConfirmed(command);
        ServerConsoleReceipt Result(ConsoleDelivery delivery, string code) => new(command.RunId, command.CommandId, delivery, code);
        if (!consoleSessions.TryGetValue(command.RunId, out var session) || session.Stop.IsCancellationRequested)
            return Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-STALE");
        if (!await session.Admission.WaitAsync(0, ct)) return Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-BUSY");
        try { return await WriteConsoleAsync(command, query, session, enforceRate: true, ct); }
        finally { session.Admission.Release(); }
    }

    public async Task<ServerConsoleReceipt[]> SendCfgBatchAsync(Guid runId, string[] paths, Action<ServerConsoleReceipt> onReceipt, CancellationToken ct = default)
    {
        if (!consoleSessions.TryGetValue(runId, out var session) || session.Stop.IsCancellationRequested)
            throw new ServerException("HCSL-CONSOLE-STALE", "Server session changed.");
        if (!await session.Admission.WaitAsync(0, ct))
            throw new ServerException("HCSL-CONSOLE-BUSY", "Console is busy.");
        try
        {
            var receipts = new List<ServerConsoleReceipt>();
            foreach (var path in paths)
            {
                var command = new ServerConsoleCommand(runId, Guid.NewGuid(), "exec " + PresetCfgRules.Path(path, requireCfgFile: true));
                var receipt = await WriteConsoleAsync(command, ServerConsoleRules.ValidateConfirmed(command), session, receipts.Count == 0, ct);
                receipts.Add(receipt);
                onReceipt(receipt);
                if (receipt.Delivery != ConsoleDelivery.Written) break;
            }
            return [.. receipts];
        }
        finally { session.Admission.Release(); }
    }

    private static async Task<ServerConsoleReceipt> WriteConsoleAsync(ServerConsoleCommand command, string query,
        ConsoleSession session, bool enforceRate, CancellationToken ct)
    {
            ServerConsoleReceipt Result(ConsoleDelivery delivery, string code) => new(command.RunId, command.CommandId, delivery, code);
            if (session.History.TryGetValue(command.CommandId, out var previous))
                return session.Queries[command.CommandId] == query ? previous : Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-ID");
            if (enforceRate && session.LastSend != 0 && Stopwatch.GetElapsedTime(session.LastSend) < TimeSpan.FromMilliseconds(250))
                return Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-RATE");
            session.LastSend = Stopwatch.GetTimestamp();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Acks[command.CommandId] = completion;
            var attempted = false;
            ServerConsoleReceipt receipt;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await session.Writer.WaitAsync(timeout.Token);
                try
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    attempted = true;
                    await session.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command with { Text = query }, ServerGateJsonContext.Default.ServerConsoleCommand).AsMemory(), timeout.Token);
                    await session.Process.StandardInput.FlushAsync(timeout.Token);
                }
                finally { session.Writer.Release(); }
                var written = await completion.Task.WaitAsync(timeout.Token);
                receipt = Result(written ? ConsoleDelivery.Written : ConsoleDelivery.Rejected, written ? "HCSL-CONSOLE-WRITTEN" : "HCSL-CONSOLE-REJECTED") with { DisplayText = session.Display(query) };
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException)
            { receipt = Result(attempted ? ConsoleDelivery.Unknown : ConsoleDelivery.Rejected, attempted ? "HCSL-CONSOLE-UNKNOWN" : "HCSL-CONSOLE-STOPPING"); }
            finally { session.Acks.TryRemove(command.CommandId, out _); }
            receipt = receipt with { DisplayText = session.Display(query) };
            if (session.History.Count >= 64) { var oldest = session.History.Keys.First(); session.History.Remove(oldest); session.Queries.Remove(oldest); }
            session.History[command.CommandId] = receipt; session.Queries[command.CommandId] = query;
            return receipt;
    }
}
