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
    private static readonly HCSL.Infrastructure.Frameworks.FrameworkManager Frameworks = new();
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
                if (request.Kind is RequestKind.FrameworkCheck or RequestKind.FrameworkInstall)
                    deadline.CancelAfter(TimeSpan.FromMinutes(20));
                LocalResponse response;
                try { response = request.Kind == RequestKind.FrameworkCheck ? await FrameworkCheckAsync(request, deadline.Token)
                    : request.Kind == RequestKind.FrameworkInstall ? await FrameworkInstallAsync(request, deadline.Token)
                    : request.Kind == RequestKind.ServerConsole ? await ConsoleAsync(request, deadline.Token)
                    : request.Kind == RequestKind.Server && DeserializeServerAction(request) == ServerAction.PlanDeployment ? await PlanDeploymentAsync(request, deadline.Token)
                    : Dispatch(request); }
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
    private static ServerAction? DeserializeServerAction(LocalRequest request)
    {
        try { return JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson ?? "null")?.Action; }
        catch (JsonException) { return null; }
    }
    private async Task<LocalResponse> PlanDeploymentAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        if (servers is null) throw new ServerException("HCSL-SERVER-UNAVAILABLE", "Server lifecycle service is unavailable.");
        var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson ?? "null")
            ?? throw new ServerException("HCSL-SERVER-OPTIONS", "Server command is required.");
        var paths = request.Options?.Workspace ?? throw new ServerException("HCSL-OPTIONS", "Workspace is required.");
        var plan = await servers.PlanDeployment(new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), command.InstanceId);
        return new(LocalProtocol.Version, request.Id, true, DeploymentPlanJson: JsonSerializer.Serialize(plan));
    }
    private async Task<LocalResponse> FrameworkCheckAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var paths = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var result = await Frameworks.CheckAsync(new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), ct);
        return new(LocalProtocol.Version, request.Id, true, FrameworkCheckJson: JsonSerializer.Serialize(result));
    }
    private async Task<LocalResponse> FrameworkInstallAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var paths = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<FrameworkInstallCommand>(request.FrameworkInstallJson ?? "null")
            ?? throw new InstanceException("HCSL-FRAMEWORK-ID", "Framework install command is required.");
        if (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running)
            throw new InstanceException("HCSL-FRAMEWORK-BUSY", "Stop the server/SteamCMD and complete recovery before installing a framework.");
        var result = await Frameworks.InstallAsync(new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), command, ct);
        return new(LocalProtocol.Version, request.Id, true, FrameworkInstallResultJson: JsonSerializer.Serialize(result));
    }
    private async Task<LocalResponse> ConsoleAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        if (servers is null) throw new ServerException("HCSL-CONSOLE-UNAVAILABLE", "Server runtime unavailable.");
        var command = JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson ?? "null")
            ?? throw new ServerException("HCSL-CONSOLE-INPUT", "Console query required.");
        var receipt = await servers.SendConsoleAsync(command, ct);
        return new(LocalProtocol.Version, request.Id, true, ConsoleReceiptJson: JsonSerializer.Serialize(receipt));
    }
    private LocalResponse Dispatch(LocalRequest request)
    {
        if (request.Version != LocalProtocol.Version)
            return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION", "Unsupported local protocol version.");
        return request.Kind switch
        {
            RequestKind.Hello => Hello(request),
            RequestKind.Instances => Instances(request),
            RequestKind.PresetCfg => PresetCfg(request),
            RequestKind.MapPool => MapPool(request),
            RequestKind.Plugin => Plugin(request),
            RequestKind.PluginImport => PluginImport(request),
            RequestKind.PluginImportProbe => PluginImportProbe(request),
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
            ServerAction.Deploy => servers.Deploy(Workspace(), command.InstanceId),
            ServerAction.RecoverDeployment => servers.RecoverDeployment(Workspace(), command.InstanceId),
            ServerAction.Recover => servers.Recover(Workspace(), command.InstanceId, command.RunId),
            _ => throw new ServerException("HCSL-SERVER-OPTIONS", "Unknown server action.")
        };
        return new(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snapshot));
    }
    private LocalResponse MapPool(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<MapPoolCommand>(request.MapPoolJson ?? "null")
            ?? throw new InstanceException("HCSL-MAPPOOL-REQUEST", "Map pool command is required.");
        if (command.Action != MapPoolAction.Read &&
            (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running))
            throw new InstanceException("HCSL-MAPPOOL-BUSY", "Stop server/SteamCMD and finish recovery before editing maps.");
        var result = new LocalPresetMapPoolStore().Execute(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true, MapPoolResultJson: JsonSerializer.Serialize(result));
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > LocalProtocol.MaxFrameBytes - 1024)
            throw new InstanceException("HCSL-MAPPOOL-LIMIT", "Map pool response exceeds IPC frame limit.");
        return response;
    }
    private LocalResponse PluginImportProbe(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<PluginImportProbeCommand>(request.PluginImportProbeJson ?? "null")
            ?? throw new InstanceException("HCSL-PLUGIN-REQUEST", "Plugin import probe command is required.");
        var preview = new LocalPluginImporter().Probe(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        return new(LocalProtocol.Version, request.Id, true, PluginImportPreviewJson: JsonSerializer.Serialize(preview));
    }
    private LocalResponse PluginImport(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<PluginImportCommand>(request.PluginImportJson ?? "null")
            ?? throw new InstanceException("HCSL-PLUGIN-REQUEST", "Plugin import command is required.");
        if (command.Enable &&
            (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running))
            throw new InstanceException("HCSL-PLUGIN-BUSY", "Stop the server/SteamCMD and complete recovery before enabling an imported package.");
        var result = new LocalPluginImporter().Execute(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        return new(LocalProtocol.Version, request.Id, true, PluginImportResultJson: JsonSerializer.Serialize(result));
    }
    private LocalResponse Plugin(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<PluginCommand>(request.PluginJson ?? "null")
            ?? throw new InstanceException("HCSL-PLUGIN-REQUEST", "Plugin command is required.");
        if (command.Action != PluginAction.List &&
            (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running))
            throw new InstanceException("HCSL-PLUGIN-BUSY", "Stop the server/SteamCMD and complete recovery before editing plugins.");
        var result = new LocalPluginStore().Execute(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true, PluginResultJson: JsonSerializer.Serialize(result));
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > LocalProtocol.MaxFrameBytes - 1024)
            throw new InstanceException("HCSL-PLUGIN-LIMIT", "Plugin response exceeds the IPC limit.");
        return response;
    }
    private LocalResponse PresetCfg(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<PresetCfgCommand>(request.PresetCfgJson ?? "null")
            ?? throw new InstanceException("HCSL-CFG-REQUEST", "CFG command is required.");
        if (command.Action is not (PresetCfgAction.List or PresetCfgAction.Read) &&
            (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running))
            throw new InstanceException("HCSL-CFG-BUSY", "Stop the server/SteamCMD and complete recovery before editing CFG.");
        var result = new LocalPresetCfgStore().Execute(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true, PresetCfgResultJson: JsonSerializer.Serialize(result));
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > LocalProtocol.MaxFrameBytes - 1024)
            throw new InstanceException("HCSL-CFG-LIMIT", "CFG response exceeds the IPC limit.");
        return response;
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
