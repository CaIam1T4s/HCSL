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
    public async Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
    {
        var query = ServerConsoleRules.ValidateConfirmed(command);
        ServerConsoleReceipt Result(ConsoleDelivery delivery, string code) => new(command.RunId, command.CommandId, delivery, code);
        if (!consoleSessions.TryGetValue(command.RunId, out var session) || session.Stop.IsCancellationRequested)
            return Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-STALE");
        if (!await session.Admission.WaitAsync(0, ct)) return Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-BUSY");
        try
        {
            if (session.History.TryGetValue(command.CommandId, out var previous))
                return session.Queries[command.CommandId] == query ? previous : Result(ConsoleDelivery.Rejected, "HCSL-CONSOLE-ID");
            if (session.LastSend != 0 && Stopwatch.GetElapsedTime(session.LastSend) < TimeSpan.FromMilliseconds(250))
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
                    await session.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command with { Text = query }).AsMemory(), timeout.Token);
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
        finally { session.Admission.Release(); }
    }
}
