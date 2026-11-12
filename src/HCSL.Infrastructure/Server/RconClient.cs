using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using HCSL.Core;

namespace HCSL.Infrastructure.Server;

/// <summary>
/// Minimal Source RCON client (TCP, size-prefixed packets). Only used to query the server HCSL started.
/// Verified against CS2 1.41.9.7: single packet responses (361 KB observations never fragmented), no
/// terminator packet, and RCON replies never appear in the game console log.
/// </summary>
public sealed class RconClient : IAsyncDisposable
{
    private const int AuthId = 100;
    private const int ExecId = 1;
    private const int TypeResponse = 0;
    private const int TypeExecCommand = 2;
    private const int TypeAuth = 3;
    private const int MaxPacketBytes = 16 * 1024 * 1024;

    private readonly TcpClient tcp;
    private readonly NetworkStream stream;

    private RconClient(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    /// <summary>Connect and authenticate. Throws <see cref="ServerException"/> with a stable code on failure.</summary>
    public static async Task<RconClient> ConnectAsync(string host, int port, string password, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) throw new ServerException("HCSL-PLAYERS-STATE", "The server has no RCON password for this run.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, deadline.Token);
            var client = new RconClient(tcp);
            if (!await client.AuthenticateAsync(password, deadline.Token))
            {
                await client.DisposeAsync();
                throw new ServerException("HCSL-PLAYERS-AUTH", "RCON authentication was rejected.");
            }
            return client;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            tcp.Dispose();
            throw new ServerException("HCSL-PLAYERS-TRANSPORT", "RCON connection to " + host + ":" + port + " failed.");
        }
    }

    private async Task<bool> AuthenticateAsync(string password, CancellationToken ct)
    {
        await SendAsync(AuthId, TypeAuth, password, ct);
        // A rejected password answers with id -1; a correct one echoes the request id with the auth response
        // type (2 on CS2 1.41.9.7, verified live), not the exec response type (0).
        while (true)
        {
            var (id, _, _) = await ReceiveAsync(ct);
            if (id == -1) return false;
            if (id == AuthId) return true;
        }
    }

    /// <summary>Run one console command and return its response text. Empty when the command prints nothing.</summary>
    public async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        await SendAsync(ExecId, TypeExecCommand, command, deadline.Token);
        var text = new StringBuilder();
        var received = false;
        while (true)
        {
            (int id, int type, byte[] body) packet;
            try { packet = await ReceiveAsync(deadline.Token); }
            catch (OperationCanceledException) when (received && !ct.IsCancellationRequested) { break; } // no terminator: stop after a quiet read
            if (packet.id != ExecId && packet.id != -1) continue;         // junk from a previous request
            if (packet.id == -1) throw new ServerException("HCSL-PLAYERS-AUTH", "RCON command was rejected.");
            if (packet.type != TypeResponse) continue;
            text.Append(Encoding.UTF8.GetString(packet.body));
            received = true;
            // Large responses were observed as one packet; also absorb extra packets if a build splits them.
            deadline.CancelAfter(TimeSpan.FromMilliseconds(150));
        }
        if (!received) throw new ServerException("HCSL-PLAYERS-PARSE", "RCON returned no response for " + command + ".");
        return text.ToString();
    }

    private async Task SendAsync(int id, int type, string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var bytes = new byte[4 + 4 + 4 + payload.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 4 + 4 + payload.Length + 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), type);
        payload.CopyTo(bytes.AsSpan(12));
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<(int Id, int Type, byte[] Body)> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 10 or > MaxPacketBytes) throw new ServerException("HCSL-PLAYERS-PARSE", "RCON packet size is outside the accepted range.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        var id = BinaryPrimitives.ReadInt32LittleEndian(body);
        var type = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        var text = body.AsSpan(8);
        // Trailing NUL terminators are transport framing, not content.
        var end = text.Length;
        while (end > 0 && text[end - 1] == 0) end--;
        return (id, type, text[..end].ToArray());
    }

    public ValueTask DisposeAsync()
    {
        try { stream.Dispose(); } finally { tcp.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
