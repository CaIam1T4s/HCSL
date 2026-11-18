using System.IO.Pipes;
using HCSL.Contracts;

namespace HCSL.Frontend;

public interface IDaemonClient
{
    Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default);
    /// <summary>Long-running requests (downloads) opt into their own deadline; fakes keep the default.</summary>
    Task<LocalResponse> SendAsync(LocalRequest request, TimeSpan timeout, CancellationToken ct = default)
        => SendAsync(request, ct);
}
public sealed class DaemonClient(string? pipeName = null) : IDaemonClient
{
    private readonly string pipe = LocalProtocol.PipeName(pipeName);
    /// <summary>A stopped daemon removes its listener: waiting longer than this only delays recovery.</summary>
    private static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(1);
    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default) =>
        SendAsync(request, TimeSpan.FromSeconds(5), ct);
    public async Task<LocalResponse> SendAsync(LocalRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        // Connecting to an absent pipe does not fail fast: give it its own short deadline so a stopped
        // daemon is detected in about a second instead of waiting for a long response deadline.
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(timeout < ConnectDeadline ? timeout : ConnectDeadline);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var options = OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, options);
        try
        {
            await connection.ConnectAsync(connect.Token);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
        {
            HCSL.Application.Diagnostics.DiagnosticsLog.Write("ipc", (request.Kind == RequestKind.Hello ? "startup-probe" : "connect")
                + " failed kind=" + request.Kind + " connect=" + ConnectDeadline.TotalSeconds
                + "s: " + e.GetType().Name + (ct.IsCancellationRequested ? " (caller cancelled)" : ""));
            // No listener answered: the daemon is gone, not busy. Say so, so the caller reconnects
            // instead of ending its poll loop.
            if (!ct.IsCancellationRequested && e is OperationCanceledException)
                throw new DaemonUnavailableException("HCSL-IPC-UNAVAILABLE: no local daemon answered within " + ConnectDeadline.TotalSeconds + "s.");
            throw;
        }
        if (OperatingSystem.IsWindows()) WindowsPipePeer.VerifyServer(connection);
        try
        {
            await LocalProtocol.WriteAsync(connection, request, deadline.Token);
            var response = await LocalProtocol.ReadAsync<LocalResponse>(connection, deadline.Token);
            if (response.Version != LocalProtocol.Version || response.Id != request.Id)
                throw new DaemonProtocolException("Local IPC response does not match this request/version.");
            return response;
        }
        catch (Exception e) when (e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The daemon was still working when the client deadline expired: busy, not necessarily gone.
            HCSL.Application.Diagnostics.DiagnosticsLog.Write("ipc", "request deadline exceeded kind=" + request.Kind + " timeout=" + timeout.TotalSeconds + "s");
            throw new DaemonTimeoutException("HCSL-IPC-TIMEOUT: the daemon did not answer " + request.Kind + " within " + timeout.TotalSeconds + "s.");
        }
    }
}

public sealed class DaemonProtocolException(string message) : Exception(message);

/// <summary>The daemon accepted the connection but answered later than the client deadline.
/// A busy daemon is not a daemon that went away: callers must not treat this as a disconnect.</summary>
public sealed class DaemonTimeoutException(string message) : IOException(message)
{
    public string Code { get; } = "HCSL-IPC-TIMEOUT";
}

/// <summary>Nobody answered on the pipe: the daemon exited, was stopped, or has not started yet.
/// Still recoverable through the bootstrapper, so it is reported as an I/O failure.</summary>
public sealed class DaemonUnavailableException(string message) : IOException(message)
{
    public string Code { get; } = "HCSL-IPC-UNAVAILABLE";
}
