using System.Diagnostics;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Server;

/// <summary>Windows x64 local dedicated server backend. External networking is not enabled here.</summary>
public sealed class WindowsServerRuntime : IServerRuntimeService
{
    public Task<DeploymentPlan> PlanDeploymentAsync(WorkspaceRequest workspace, Guid instanceId, CancellationToken ct = default) =>
        Task.Run(() => new RuntimeDeploymentService().Plan(workspace, new(instanceId, DeploymentCopyMode.FullCopy), ct), ct);

    public Task<DeploymentReceipt> DeployAsync(WorkspaceRequest workspace, Guid instanceId,
        IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default) =>
        Task.Run(() => new RuntimeDeploymentService().Deploy(workspace, new(instanceId, DeploymentCopyMode.FullCopy), null, ct), ct);

    public Task<string> RecoverDeploymentAsync(WorkspaceRequest workspace, Guid instanceId,
        IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default) =>
        Task.Run(() => new RuntimeDeploymentService().Recover(workspace, instanceId), ct);

    public async Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options,
        IProgress<ServerRuntimeEvent> events, CancellationToken stop)
    {
        var plan = await PlanDeploymentAsync(workspace, options.InstanceId, stop);
        if (plan.RotationCollection is not null)
            throw new ServerException("HCSL-SERVER-WORKSHOP", "Windows startup currently supports installed maps only.");
        if (options.AutoDeploy) await DeployAsync(workspace, options.InstanceId, events, stop);
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        var receipt = RuntimeDeploymentService.ValidateExisting(lease, options.InstanceId, plan.RuntimePath,
            verifyRotation: false, allowLegacy: false);
        if (receipt is null || ServerPreparation.IsStale(receipt, plan) || !RuntimeDeploymentService.RotationFileMatches(receipt, plan.RuntimePath))
            throw new ServerException("HCSL-SERVER-DEPLOY", "Deploy the current preset before starting it.");
        var entry = LocalInstanceStore.Load(Path.Combine(lease.Layout.InstancesRoot, options.InstanceId.ToString("D")), options.InstanceId);
        if (entry.Definition is not { } definition || definition.Revision != plan.InstanceRevision ||
            entry.Presets.FirstOrDefault(p => p.Id == plan.PresetId && p.Revision == plan.PresetRevision) is not { } preset)
            throw new ServerException("HCSL-SERVER-PRESET", "The preset changed before startup.");
        if (InstanceRules.Resolve(definition.Network).Mode != NetworkAccessMode.Loopback)
            throw new ServerException("HCSL-SERVER-NETWORK", "Windows startup currently requires loopback access.");
        using var password = InstancePasswordConfig.Bind(Path.Combine(lease.Layout.InstancesRoot,
            options.InstanceId.ToString("D")), plan.PresetId.ToString("D"));
        var game = Path.Combine(plan.RuntimePath, "game");
        var executable = Path.Combine(game, "bin", "win64", "cs2.exe");
        LocalPaths.RejectLink(executable);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = game, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-dedicated", "-console", "-insecure", "-nomaster", "-ip", "127.0.0.1", "-port", definition.GamePort.ToString(),
            "+sv_lan", "1", "+game_type", preset.GameType.ToString(), "+game_mode", preset.GameMode.ToString(), "+servercfgfile", InstancePasswordConfig.ExecName, "+map", preset.Map })
            start.ArgumentList.Add(arg);
        if (plan.RotationGroup is { } group) { start.ArgumentList.Insert(start.ArgumentList.Count - 2, "+mapgroup"); start.ArgumentList.Insert(start.ArgumentList.Count - 2, group); }
        var identity = new ServerPresetIdentity(preset.Id, preset.Revision, preset.Name, preset.Map);
        stop.ThrowIfCancellationRequested();
        events.Report(new(ServerState.Starting, "HCSL-SERVER-STARTING", Preset: identity));
        using var process = Process.Start(start) ?? throw new ServerException("HCSL-SERVER-START", "Could not start cs2.exe.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = PumpAsync(process.StandardOutput);
        var errors = PumpAsync(process.StandardError);
        var exited = process.WaitForExitAsync();
        var deadline = Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.StartTimeoutSeconds, 1, 600)), stop);
        var winner = await Task.WhenAny(ready.Task, exited, deadline);
        if (winner == ready.Task) await Task.WhenAny(exited, Task.Delay(Timeout.InfiniteTimeSpan, stop));
        var timedOut = winner == deadline && !stop.IsCancellationRequested;
        if (!process.HasExited)
        {
            events.Report(new(ServerState.Stopping, "HCSL-SERVER-STOPPING"));
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) { return new("HCSL-SERVER-CLEANUP", null, ready.Task.IsCompleted, false); }
        }
        try { await exited.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { return new("HCSL-SERVER-CLEANUP", null, ready.Task.IsCompleted, false); }
        await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(TimeSpan.FromSeconds(2)));
        return new(timedOut ? "HCSL-SERVER-START-TIMEOUT" : stop.IsCancellationRequested ? "HCSL-SERVER-STOPPED" : "HCSL-SERVER-EXITED",
            process.ExitCode, ready.Task.IsCompleted, true);

        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Contains("ss_loading -> ss_active", StringComparison.Ordinal) && ready.TrySetResult())
                    events.Report(new(ServerState.Running, "HCSL-SERVER-RUNNING", Map: preset.Map, Preset: identity));
                events.Report(new(ready.Task.IsCompleted ? ServerState.Running : ServerState.LoadingMap,
                    "HCSL-SERVER-LOG", line, LogReceivedAt: DateTimeOffset.UtcNow));
            }
        }
    }

    public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) =>
        throw new ServerException("HCSL-SERVER-RECOVERY", "Stop the verified CS2 process before recovering a Windows runtime.");
}
