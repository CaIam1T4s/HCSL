using System.Diagnostics;
using HCSL.Core;
using HCSL.Application.Instances;
using System.IO.Pipes;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Daemon.Workers;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;

namespace HCSL.Daemon.Ipc;

public sealed class LocalDaemonServer(string pipeName, SteamCmdOperations operations, TimeSpan? idleTimeout = null, ServerOperations? servers = null)
{
    private static readonly HCSL.Infrastructure.Frameworks.FrameworkManager Frameworks = new();
    /// <summary>Running framework installs, keyed by request id, so a later Cancel can abort the download.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> frameworkInstalls = new();
    /// <summary>Latest measured transfer per framework install, read by lightweight progress polls.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TransferSnapshot> frameworkTransfers = new();
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
        HCSL.Application.Diagnostics.DiagnosticsLog.Write("daemon", "listening pipe=" + pipeName + " maxClients=8");
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
                    HCSL.Application.Diagnostics.DiagnosticsLog.Write("daemon", "idle exit: no requests for " + timeout.TotalSeconds + "s");
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
                if (request.Kind == RequestKind.Baseline)
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));
                else if (request.Kind is RequestKind.FrameworkCheck or RequestKind.FrameworkInstall or RequestKind.FrameworkCatalog)
                    deadline.CancelAfter(TimeSpan.FromMinutes(20));
                else if (request.Kind == RequestKind.PluginCfg)
                    deadline.CancelAfter(TimeSpan.FromMinutes(5));
                LocalResponse response;
                try { response = request.Kind == RequestKind.Baseline ? await BaselineAsync(request, deadline.Token)
                    : request.Kind == RequestKind.FrameworkProgress ? FrameworkProgress(request)
                    : request.Kind == RequestKind.FrameworkCatalog ? await FrameworkCatalogAsync(request, deadline.Token)
                    : request.Kind == RequestKind.FrameworkCheck ? await FrameworkCheckAsync(request, deadline.Token)
                    : request.Kind == RequestKind.FrameworkInstall ? await FrameworkInstallAsync(request, deadline.Token)
                    : request.Kind == RequestKind.Admin ? await AdminAsync(request, deadline.Token)
                    : request.Kind == RequestKind.Server && DeserializeServerAction(request) == ServerAction.Kick ? await KickAsync(request, deadline.Token)
                    : request.Kind == RequestKind.Server && DeserializeServerAction(request) == ServerAction.Players ? await PlayersAsync(request, deadline.Token)
                    : request.Kind == RequestKind.ServerConsole ? await ConsoleAsync(request, deadline.Token)
                    : request.Kind == RequestKind.Server && DeserializeServerAction(request) == ServerAction.PlanDeployment ? await PlanDeploymentAsync(request, deadline.Token)
                    : Dispatch(request); }
                catch (ServerException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (DeploymentException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (InstanceException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (WorkspaceException ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (OperationRejected ex) { response = new(LocalProtocol.Version, request.Id, false, ex.Code, ex.Message); }
                catch (Exception) { response = new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-ERROR", "Local request failed."); HCSL.Application.Diagnostics.DiagnosticsLog.Write("daemon", "request failed kind=" + request.Kind); }
                response = response with { InstanceId = instance, ProcessId = Environment.ProcessId, DaemonBuild = build };
                await LocalProtocol.WriteAsync(pipe, response, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException or ArgumentException)
            {
                // Broken/oversized clients are closed; request contents, especially secrets, are never logged.
                HCSL.Application.Diagnostics.DiagnosticsLog.Write("daemon", "client closed: " + ex.GetType().Name + (deadline.IsCancellationRequested ? " (request deadline)" : ""));
            }
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
    /// <summary>Startup baseline status/refresh. Refresh writes only the library-local baseline.</summary>
    private Task<LocalResponse> BaselineAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION"));
        var workspace = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<BaselineCommand>(request.BaselineJson ?? "null")
            ?? throw new InstanceException("HCSL-BASELINE-REQUEST", "Baseline action is required.");
        if (!Enum.IsDefined(command.Action))
            throw new InstanceException("HCSL-BASELINE-REQUEST", "Unknown baseline action.");
        var layout = new WorkspaceLayout(workspace.SettingsRoot, workspace.DataRoot, workspace.LibraryRoot,
            Path.Combine(workspace.LibraryRoot, "steamcmd"), Path.Combine(workspace.DataRoot, "instances"),
            Path.Combine(workspace.LibraryRoot, "server"), Path.Combine(workspace.LibraryRoot, "runtime"));
        var stateFile = Path.Combine(layout.LibraryRoot, ".hcsl", "cs2-install.json");
        if (!File.Exists(stateFile))
            throw new InstanceException("HCSL-BASELINE-NOT-READY", "No managed CS2 installation is available.");
        using var state = JsonDocument.Parse(File.ReadAllText(stateFile));
        var status = state.RootElement.TryGetProperty("Status", out var statusValue) ? statusValue.GetString() : null;
        var buildId = state.RootElement.TryGetProperty("BuildId", out var buildValue) ? buildValue.GetString() : null;
        if (status != "Ready" || string.IsNullOrWhiteSpace(buildId))
            throw new InstanceException("HCSL-BASELINE-NOT-READY", "CS2 installation is not Ready; finish or repair it first.");
        if (command.Action == BaselineAction.Status)
        {
            var (source, installed, localBuild) = RuntimeDeploymentService.BaselineStatus(layout, buildId);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                BaselineResultJson: JsonSerializer.Serialize(new BaselineResult(installed, source, localBuild.Length == 0 ? null : localBuild,
                    Path.Combine(layout.LibraryRoot, ".hcsl", "linux-startup-baseline.json")))));
        }
        if (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running)
            throw new InstanceException("HCSL-BASELINE-BUSY", "Stop the server/SteamCMD and complete recovery before refreshing the baseline.");
        using (new LocalWorkspaceManager().Acquire(new WorkspaceRequest(workspace.SettingsRoot, workspace.DataRoot, workspace.LibraryRoot)))
            RuntimeDeploymentService.RebuildLocalBaseline(layout, buildId, Guid.NewGuid());
        var result = RuntimeDeploymentService.BaselineStatus(layout, buildId);
        return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
            BaselineResultJson: JsonSerializer.Serialize(new BaselineResult(result.BuildId, result.Source, result.LocalBuildId,
                Path.Combine(layout.LibraryRoot, ".hcsl", "linux-startup-baseline.json")))));
    }
    private async Task<LocalResponse> FrameworkCatalogAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var paths = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<FrameworkCatalogCommand>(request.FrameworkCatalogJson ?? "null")
            ?? throw new InstanceException("HCSL-FRAMEWORK-ID", "Framework catalog command is required.");
        var result = await Frameworks.CatalogAsync(new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), command, ct);
        return new(LocalProtocol.Version, request.Id, true, FrameworkCatalogJson: JsonSerializer.Serialize(result));
    }

    private async Task<LocalResponse> FrameworkCheckAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var paths = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = request.FrameworkCheckJson is { Length: > 0 } json
            ? JsonSerializer.Deserialize<FrameworkCheckCommand>(json)
            : null;
        var result = await Frameworks.CheckAsync(new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), command?.Id, ct);
        return new(LocalProtocol.Version, request.Id, true, FrameworkCheckJson: JsonSerializer.Serialize(result));
    }
    /// <summary>
    /// Cancels whichever unit of work owns the id: a framework install in flight, otherwise the
    /// SteamCMD operation. The install's own cleanup runs in its finally block, so a cancelled
    /// download leaves no partial file behind.
    /// </summary>
    private OperationSnapshot CancelWork(Guid? operationId)
    {
        // A framework install is not a SteamCMD task; reporting it to operations.Cancel would raise
        // HCSL-TASK-NOTFOUND even though the cancel itself succeeded.
        if (operationId is { } id && frameworkInstalls.TryRemove(id, out var install))
        {
            install.Cancel();
            return operations.Snapshot();
        }
        return operations.Cancel(operationId);
    }

    private async Task<LocalResponse> FrameworkInstallAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var paths = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<FrameworkInstallCommand>(request.FrameworkInstallJson ?? "null")
            ?? throw new InstanceException("HCSL-FRAMEWORK-ID", "Framework install command is required.");
        if (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running)
            throw new InstanceException("HCSL-FRAMEWORK-BUSY", "Stop the server/SteamCMD and complete recovery before installing a framework.");
        using var install = CancellationTokenSource.CreateLinkedTokenSource(ct);
        frameworkInstalls[request.Id] = install;
        frameworkTransfers[request.Id] = new TransferSnapshot(HCSL.Contracts.TransferPhase.Idle);
        // Progress<TransferProgress> reports on the thread pool; the dictionary is the only shared state,
        // so an abandoned poll simply stops reading a stale value.
        var progress = new Progress<TransferProgress>(value =>
            frameworkTransfers[request.Id] = new TransferSnapshot(Enum.Parse<HCSL.Contracts.TransferPhase>(value.Phase.ToString()),
                value.DoneBytes, value.TotalBytes, value.BytesPerSecond, value.Percent));
        try
        {
            var result = await Frameworks.InstallAsync(new(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot), command, install.Token, progress);
            return new(LocalProtocol.Version, request.Id, true, FrameworkInstallResultJson: JsonSerializer.Serialize(result));
        }
        finally
        {
            frameworkInstalls.TryRemove(request.Id, out _);
            frameworkTransfers.TryRemove(request.Id, out _);
        }
    }

    /// <summary>Read-only progress for one framework install; returns nothing once it finished.</summary>
    private static LocalResponse FrameworkProgress(LocalRequest request)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var transfer = request.OperationId is { } id && frameworkTransfers.TryGetValue(id, out var value) ? value : null;
        return new(LocalProtocol.Version, request.Id, true, TransferJson: transfer is null ? null : JsonSerializer.Serialize(transfer));
    }
    private async Task<LocalResponse> AdminAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<AdminCommand>(request.AdminJson ?? "null")
            ?? throw new InstanceException("HCSL-ADMIN-REQUEST", "Admin command is required.");
        var workspace = new WorkspaceRequest(p.SettingsRoot, p.DataRoot, p.LibraryRoot);
        var store = new LocalAdminStore();
        if (command.Action == AdminAction.Reload)
        {
            var snapshot = store.Execute(workspace, command with { Action = AdminAction.List });
            var reload = await ReloadAdminsAsync(snapshot.Snapshot.SourceId, ct);
            var reloaded = new AdminResult(snapshot.Snapshot, "HCSL-ADMIN-RELOADED", [], reload);
            return new(LocalProtocol.Version, request.Id, true, AdminResultJson: JsonSerializer.Serialize(reloaded));
        }
        var result = store.Execute(workspace, command);
        if (command.Action == AdminAction.Apply)
            result = result with { Reload = await ReloadAdminsAsync(result.Snapshot.SourceId, ct) };
        return new(LocalProtocol.Version, request.Id, true, AdminResultJson: JsonSerializer.Serialize(result));
    }

    /// <summary>Apply a written admin list: frameworks with a console command get it; native readers need a restart.</summary>
    private async Task<string?> ReloadAdminsAsync(string? sourceId, CancellationToken ct)
    {
        if (AdminRules.Capabilities(sourceId) is not { } capabilities) return null;
        if (capabilities.Reload == AdminReloadKind.Restart)
            return capabilities.SourceName + " reads this file in native code; restart the server to apply";
        if (capabilities.Reload == AdminReloadKind.HotReload)
            return capabilities.SourceName + " is expected to hot reload the file; verify in game";
        if (capabilities.ReloadCommand is not { } consoleCommand) return null;
        if (servers is null) return "daemon has no server runtime; applies at next start";
        var snapshot = servers.Snapshot();
        if (!snapshot.Active || snapshot.RunId is not { } runId
            || snapshot.State is not (ServerState.Starting or ServerState.LoadingMap or ServerState.Running))
            return "server is not running; applies at next start";
        var receipt = await servers.SendConsoleAsync(new(runId, Guid.NewGuid(), consoleCommand, ConfirmMutation: true), ct);
        return receipt.Delivery == ConsoleDelivery.Written
            ? consoleCommand + " sent (" + receipt.Code + ")"
            : consoleCommand + " rejected (" + receipt.Code + ")";
    }

    private async Task<LocalResponse> KickAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        if (servers is null) throw new ServerException("HCSL-SERVER-UNAVAILABLE", "Server lifecycle service is unavailable.");
        var command = JsonSerializer.Deserialize<ServerKickCommand>(request.ServerKickJson ?? "null")
            ?? throw new ServerException("HCSL-KICK-TARGET", "Kick command is required.");
        var result = await servers.KickAsync(command, ct);
        return new(LocalProtocol.Version, request.Id, true, ServerKickResultJson: JsonSerializer.Serialize(result));
    }

    private async Task<LocalResponse> PlayersAsync(LocalRequest request, CancellationToken ct)
    {
        if (request.Version != LocalProtocol.Version) return new(LocalProtocol.Version, request.Id, false, "HCSL-IPC-VERSION");
        if (servers is null) throw new ServerException("HCSL-SERVER-UNAVAILABLE", "Server lifecycle service is unavailable.");
        var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson ?? "null")
            ?? throw new ServerException("HCSL-SERVER-OPTIONS", "Server command is required.");
        var roster = await servers.PlayersAsync(command.InstanceId, ct);
        var snapshot = servers.Snapshot(hint: command.InstanceId == Guid.Empty ? null : command.InstanceId) with { PlayerRoster = roster };
        return new(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snapshot));
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
            RequestKind.ContentStatus => new(LocalProtocol.Version, request.Id, true,
                ContentStatusJson: JsonSerializer.Serialize(HCSL.Infrastructure.Steam.Cs2ContentStatusReader.Read(RequiredOptions(request).Workspace.LibraryRoot))),
            RequestKind.Instances => Instances(request),
            RequestKind.PresetCfg => PresetCfg(request),
            RequestKind.MapPool => MapPool(request),
            RequestKind.MapCatalog => MapCatalog(request),
            RequestKind.MapPoolLibrary => MapPoolLibrary(request),
            RequestKind.Plugin => Plugin(request),
            RequestKind.PluginImport => PluginImport(request),
            RequestKind.PluginImportProbe => PluginImportProbe(request),
            RequestKind.PluginCfg => PluginCfg(request),
            RequestKind.Server => Server(request),
            RequestKind.Status => new(LocalProtocol.Version, request.Id, true, Snapshot: operations.Snapshot()),
            RequestKind.StartProbe => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request)),
            RequestKind.InstallOrUpdate => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request, installMode: SteamCmdInstallMode.InstallOrUpdate)),
            RequestKind.ValidateContent => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request, installMode: SteamCmdInstallMode.Validate)),
            RequestKind.Recover => new(LocalProtocol.Version, request.Id, true, Snapshot: StartSteam(request, recover: true)),
            RequestKind.Cancel => new(LocalProtocol.Version, request.Id, true, Snapshot: CancelWork(request.OperationId)),
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
    /// <summary>Instance-level pool library; the write gate matches the other map editors.</summary>
    private LocalResponse MapPoolLibrary(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<MapPoolLibraryCommand>(request.MapPoolLibraryJson ?? "null")
            ?? throw new InstanceException("HCSL-MAPPOOL-REQUEST", "Map pool library command is required.");
        if (command.Action != MapPoolLibraryAction.List &&
            (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running))
            throw new InstanceException("HCSL-MAPPOOL-BUSY", "Stop server/SteamCMD and finish recovery before editing map pools.");
        var result = new LocalMapPoolLibraryStore().Execute(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command);
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true,
            MapPoolLibraryResultJson: JsonSerializer.Serialize(result));
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > LocalProtocol.MaxFrameBytes - 1024)
            throw new InstanceException("HCSL-MAPPOOL-LIMIT", "Map pool library response exceeds the IPC frame limit.");
        return response;
    }
    /// <summary>
    /// Read-only map catalog for one instance: built-in maps and downloaded Workshop items from that runtime. It never
    /// executes anything, so it is safe while the server runs and while SteamCMD is busy.
    /// </summary>
    private LocalResponse MapCatalog(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<MapCatalogCommand>(request.MapCatalogJson ?? "null")
            ?? throw new InstanceException("HCSL-MAPCATALOG-REQUEST", "Map catalog request is required.");
        if (command.InstanceId == Guid.Empty) throw new InstanceException("HCSL-MAPCATALOG-REQUEST", "An instance id is required.");
        var game = Path.Combine(p.LibraryRoot, "runtime", command.InstanceId.ToString("D"), "game");
        var result = MapCatalogScanner.Scan(game);
        MapCatalogRules.Validate(result);
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true, MapCatalogResultJson: JsonSerializer.Serialize(result));
        if (JsonSerializer.SerializeToUtf8Bytes(response).Length > LocalProtocol.MaxFrameBytes - 1024)
            throw new InstanceException("HCSL-MAPCATALOG-LIMIT", "Map catalog response exceeds the IPC frame limit.");
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
    private LocalResponse PluginCfg(LocalRequest request)
    {
        var p = request.Options?.Workspace ?? throw new InstanceException("HCSL-OPTIONS", "Workspace is required.");
        var command = JsonSerializer.Deserialize<PluginCfgCommand>(request.PluginCfgJson ?? "null")
            ?? throw new InstanceException("HCSL-PLUGIN-REQUEST", "Plugin cfg command is required.");
        if (command.Action is PluginCfgAction.Apply or PluginCfgAction.CleanupApply &&
            (servers?.Active == true || servers?.Snapshot().State == ServerState.RecoveryRequired || operations.Snapshot().Running))
            throw new InstanceException("HCSL-PLUGIN-BUSY", "Stop the server/SteamCMD and complete recovery before changing cfg files.");
        var sync = new HCSL.Infrastructure.Storage.LocalPluginCfgSync();
        var result = command.Action switch
        {
            PluginCfgAction.Apply => sync.Apply(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command),
            PluginCfgAction.CleanupApply => sync.CleanupApply(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command),
            _ => (object)(command.Action == PluginCfgAction.Preview
                ? sync.Preview(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command)
                : sync.CleanupPreview(new(p.SettingsRoot, p.DataRoot, p.LibraryRoot), command))
        };
        return new(LocalProtocol.Version, request.Id, true, PluginCfgResultJson: JsonSerializer.Serialize(result));
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
    /// <summary>
    /// Identity of the running daemon: a hash of its own assembly. A rebuild that changes nothing produces the
    /// same hash, so a file timestamp is no longer mistaken for new code.
    /// </summary>
    private static string? BuildStamp()
    {
        try
        {
            var path = typeof(LocalDaemonServer).Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
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
