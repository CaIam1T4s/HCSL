using System.Diagnostics;
using HCSL.Core;
using HCSL.Application.Instances;
using System.IO.Pipes;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Daemon.Workers;
using HCSL.Infrastructure.Storage;

namespace HCSL.Daemon.Ipc;

public sealed class LocalDaemonServer(string pipeName, SteamCmdOperations operations, TimeSpan? idleTimeout = null, ServerOperations? servers = null)
{
    private readonly object activity = new();
    private int activeRequests;
    private long lastActivity = Stopwatch.GetTimestamp();
    private readonly Guid instance = Guid.NewGuid();
    public async Task RunAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = lifetime.Token;
        var clients = new List<Task>();
        using var slots = new SemaphoreSlim(8);
        NamedPipeServerStream listener = CreateListener();
        var idle = WatchIdleAsync(lifetime);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await slots.WaitAsync(ct);
                bool handedOff = false;
                try
                {
                    try { await listener.WaitForConnectionAsync(ct); }
                    catch (IOException)
                    {
                        var failed = listener;
                        listener = CreateListener();
                        failed.Dispose();
                        continue;
                    }
                    var accepted = listener;
                    // Keep a listener alive before serving: a fast client may finish synchronously.
                    // Otherwise Unix's shared socket can disappear and reset queued connections.
                    listener = CreateListener();
                    lock (activity) activeRequests++;
                    clients.Add(ServeAsync(accepted, slots, ct));
                    handedOff = true;
                    clients.RemoveAll(t => t.IsCompleted);
                }
                finally { if (!handedOff) slots.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            listener.Dispose();
            await Task.WhenAll(clients);
            await idle;
        }
    }
    private async Task WatchIdleAsync(CancellationTokenSource lifetime)
    {
        var timeout = idleTimeout ?? TimeSpan.FromSeconds(60);
        if (timeout <= TimeSpan.Zero) return;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token);
                bool exit;
                lock (activity)
                {
                    exit = activeRequests == 0 && Stopwatch.GetElapsedTime(lastActivity) >= timeout
                        && (servers?.CanIdle(timeout) ?? true) && operations.TryBeginIdleShutdown(timeout);
                    if (exit) servers?.CloseAdmission();
                }
                if (exit)
                {
                    lifetime.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private NamedPipeServerStream CreateListener() => OperatingSystem.IsWindows()
        ? WindowsPipeAccess.Create(pipeName)
        : new(pipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private async Task ServeAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken shutdown)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var request = await LocalProtocol.ReadAsync<LocalRequest>(pipe, deadline.Token);
                LocalResponse response;
                try { response = Dispatch(request); }
                catch (ServerException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (InstanceException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (WorkspaceException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (OperationRejected ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (Exception) { response = new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-ERROR", "Local request failed."); }
                response = response with { InstanceId = instance, ProcessId = Environment.ProcessId, DaemonBuild = build };
                await LocalProtocol.WriteAsync(pipe, response, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException or ArgumentException)
            { /* Broken/oversized clients are closed; request contents, especially secrets, are never logged. */ }
            finally
            {
                lock (activity) { activeRequests--; lastActivity = Stopwatch.GetTimestamp(); }
                slots.Release();
            }
        }
    }
    private LocalResponse Dispatch(LocalRequest request)
    {
        if (request.Version != LocalProtocol.Version)
            return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION", "Unsupported local protocol version.");
        return request.Kind switch
        {
            RequestKind.Hello => Hello(request),
            RequestKind.Instances => Instances(request),
            RequestKind.Server => Server(request),
            RequestKind.Status => new(LocalProtocol.Version, request.Id, true, Snapshot: operations.Snapshot()),
            RequestKind.StartProbe => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request)),
            RequestKind.InstallOrUpdate => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request, installMode: SteamCmdInstallMode.InstallOrUpdate)),
            RequestKind.ValidateContent => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request, installMode: SteamCmdInstallMode.Validate)),
            RequestKind.Recover => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request, recover: true)),
            RequestKind.Cancel => new(LocalProtocol.Version, request.Id, true, Snapshot: operations.Cancel(request.OperationId)),
            RequestKind.SubmitInput => new(LocalProtocol.Version, request.Id, true, Snapshot: operations.Submit(request.OperationId, request.ChallengeId, request.Secret)),
            _ => throw new OperationRejected("HCSL-IPC-REQUEST", "Unknown request.")
        };
    }
    private OperationSnapshot StartSteam(LocalRequest request, bool recover = false, SteamCmdInstallMode? installMode = null)
    {
        if (servers?.Active == true) throw new ServerException("HCSL-SERVER-BUSY", "Stop the server and confirm cleanup before starting SteamCMD.");
        return operations.Start(RequiredOptions(request), recover: recover, installMode: installMode);
    }
    private LocalResponse Server(LocalRequest request)
    {
        if (servers == null) throw new ServerException("HCSL-SERVER-UNAVAILABLE", "Server lifecycle service is unavailable.");
        var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson ?? "null") ?? throw new ServerException("HCSL-SERVER-OPTIONS", "Server command is required.");
        WorkspaceRequest Workspace()
        {
            var paths = request.Options?.Workspace ?? throw new ServerException("HCSL-OPTIONS", "Workspace is required.");
            return new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot);
        }
        if (command.Action == ServerAction.Start && operations.Snapshot().Running) throw new ServerException("HCSL-TASK-BUSY", "SteamCMD task is active.");
        var snapshot = command.Action switch
        {
            ServerAction.Start => servers.Start(Workspace(), new(command.InstanceId, command.StartTimeoutSeconds, command.AutoDeploy, command.AllowLowMemory)),
            ServerAction.Status => servers.Snapshot(hint: command.InstanceId),
            ServerAction.Logs => servers.Snapshot(command.AfterSequence, command.RunId),
            ServerAction.Stop => servers.Stop(command.RunId),
            ServerAction.Recover => servers.Recover(Workspace(), command.InstanceId, command.RunId),
            _ => throw new ServerException("HCSL-SERVER-OPTIONS", "Unknown server action.")
        };
        return new(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snapshot));
    }
    private static LocalResponse Instances(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson ?? "null")
            ?? throw new InstanceException("HCSL-INSTANCE-REQUEST", "Instance command is required.");
        var catalog = new InstanceService(new LocalInstanceStore()).Execute(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(catalog));
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > LocalProtocol.MaxFrameBytes - 1024)
            throw new InstanceException("HCSL-INSTANCE-LIMIT", "Catalog exceeds the local IPC frame limit.");
        return response;
    }
    /// <summary>Write time of the running daemon assembly. A frontend compares it with the payload it would
    /// launch, so an old process left over from a previous build can be replaced instead of silently used.</summary>
    private static readonly string? build = BuildStamp();
    private static string? BuildStamp()
    {
        try
        {
            var path = typeof(LocalDaemonServer).Assembly.Location;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null
                : File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
    private LocalResponse Hello(LocalRequest request)
    {
        var paths = LocalPaths.Defaults();
        return new(LocalProtocol.Version, request.Id, true,
            Defaults: new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), Snapshot: operations.Snapshot());
    }
    private static ProbeOptions RequiredOptions(LocalRequest request) => request.Options
        ?? throw new OperationRejected("HCSL-OPTIONS", "Probe options are required.");
}
