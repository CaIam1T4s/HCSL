using System.IO.Pipes;
using HCSL.Contracts;

namespace HCSL.Frontend;

public interface IDaemonClient
{
    Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default);
}
public sealed class DaemonClient(string? pipeName = null) : IDaemonClient
{
    private readonly string pipe = LocalProtocol.PipeName(pipeName);
    public async Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var options = OperatingSystem.IsWindows() ? PipeOptions.Asynchronous : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, options);
        await connection.ConnectAsync(deadline.Token);
        if (OperatingSystem.IsWindows()) WindowsPipePeer.VerifyServer(connection);
        await LocalProtocol.WriteAsync(connection, request, deadline.Token);
        var response = await LocalProtocol.ReadAsync<LocalResponse>(connection, deadline.Token);
        if (response.Version != LocalProtocol.Version || response.Id != request.Id)
            throw new DaemonProtocolException("Local IPC response does not match this request/version.");
        return response;
    }
}

public sealed class DaemonProtocolException(string message) : Exception(message);
