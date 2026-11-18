using System.Diagnostics;
using HCSL.Application.Abstractions;
using HCSL.Contracts;

namespace HCSL.Daemon.Workers;

/// <summary>One user-wide SteamCMD task, owned by the daemon, independent of any client connection.</summary>
public sealed class SteamCmdOperations(ISteamCmdService service) : IAsyncDisposable
{
    private readonly object sync = new();
    private Guid? id;
    private long revision;
    private double? percent;
    private TransferSnapshot? transfer;
    private bool running;
    private bool stopping;
    private long lastBusy = Stopwatch.GetTimestamp();

    // Atomically close admission only after all task/process cleanup has completed.
    internal bool TryBeginIdleShutdown(TimeSpan timeout)
    {
        lock (sync)
        {
            if (stopping || running || Stopwatch.GetElapsedTime(lastBusy) < timeout) return false;
            stopping = true;
            return true;
        }
    }
    private ProbeStage stage = ProbeStage.Idle;
    private string message = "No SteamCMD task.";
    private Challenge? challenge;
    private OperationResult? result;
    private readonly Queue<OperationEvent> history = new();
    private TaskCompletionSource<string>? input;
    private CancellationTokenSource? cancellation;
    private Task? task;

    public OperationSnapshot Snapshot()
    {
        lock (sync) return new(id, revision, running, stage, message, challenge, result, history.ToArray(), percent, transfer);
    }
    public OperationSnapshot Start(ProbeOptions options, bool recover = false, SteamCmdInstallMode? installMode = null)
    {
        Validate(options);
        lock (sync)
        {
            if (stopping || running) throw new OperationRejected("HCSL-TASK-BUSY", "A SteamCMD operation is already running or the daemon is stopping.");
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            id = Guid.NewGuid();
            running = true;
            result = null;
            percent = null;
            transfer = null;
            challenge = null;
            history.Clear();
            Publish(ProbeStage.Preparing, recover ? "Checking unfinished SteamCMD sessions." : installMode is null ? "Preparing SteamCMD probe." : "Preparing CS2 installation task.");
            var current = id.Value;
            var token = cancellation.Token;
            task = Task.Run(() => ExecuteAsync(current, options, recover, installMode, token));
            return Snapshot();
        }
    }
    public OperationSnapshot Cancel(Guid? operation)
    {
        lock (sync)
        {
            RequireCurrent(operation);
            if (!running || result is not null) throw new OperationRejected("HCSL-TASK-FINISHED", "The operation has already finished.");
            Publish(ProbeStage.Cancelling, "Cancellation requested; waiting for process cleanup.");
            cancellation!.Cancel();
            return Snapshot();
        }
    }
    public OperationSnapshot Submit(Guid? operation, Guid? challengeId, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 512 || secret.Any(char.IsControl))
            throw new OperationRejected("HCSL-INPUT", "Invalid authentication response.");
        TaskCompletionSource<string> pending;
        lock (sync)
        {
            RequireCurrent(operation);
            if (!running || challenge is null || challenge.Id != challengeId || input is null || cancellation!.IsCancellationRequested)
                throw new OperationRejected("HCSL-CHALLENGE-STALE", "The authentication challenge is no longer active.");
            pending = input;
            input = null;
            challenge = null;
            Publish(ProbeStage.Authenticating, "Authentication response submitted.");
        }
        pending.TrySetResult(secret);
        return Snapshot();
    }
    private void RequireCurrent(Guid? operation)
    {
        if (operation is null || operation != id)
            throw new OperationRejected("HCSL-TASK-NOTFOUND", "The requested operation is not the current task.");
    }
    private async Task ExecuteAsync(Guid current, ProbeOptions options, bool recover, SteamCmdInstallMode? installMode, CancellationToken ct)
    {
        try
        {
            var workspace = new WorkspaceRequest(options.Workspace.SettingsRoot, options.Workspace.DataRoot, options.Workspace.LibraryRoot);
            if (recover)
            {
                service.Recover(workspace, options.ExecutablePath);
                Complete(ProbeStage.Succeeded, "HCSL-STEAMCMD-RECOVERED", null);
            }
            else
            {
                var session = new SteamCmdProbeRequest(workspace, options.ExecutablePath, options.InstallIfMissing,
                    options.AccountName, TimeSpan.FromSeconds(installMode is null ? options.TimeoutSeconds : options.InstallTimeoutSeconds));
                var response = installMode is { } mode
                    ? await service.InstallAsync(new(session, mode), new ProgressSink(this), new InputProvider(this, current), ct)
                    : await service.ProbeAsync(session, new ProgressSink(this), new InputProvider(this, current), ct);
                Complete(Enum.Parse<ProbeStage>(response.Stage.ToString()), response.Code, response.ExitCode);
            }
        }
        catch (WorkspaceException ex) { Complete(ProbeStage.Failed, ex.Code, null, ex.Message); }
        catch (SteamCmdException ex) { Complete(ex.Code == "HCSL-STEAMCMD-RECOVERY" ? ProbeStage.RecoveryRequired : ProbeStage.Failed, ex.Code, null, ex.Message); }
        catch (OperationCanceledException) { Complete(ProbeStage.Cancelled, "HCSL-STEAMCMD-CANCELLED", null); }
        catch (Exception) { Complete(ProbeStage.Failed, "HCSL-DAEMON-TASK", null); }
        finally
        {
            lock (sync)
            {
                input?.TrySetCanceled();
                input = null;
                challenge = null;
                lastBusy = Stopwatch.GetTimestamp();
                running = false;
                revision++;
            }
        }
    }
    private void Complete(ProbeStage value, string code, int? exit, string? detail = null)
    {
        lock (sync) { result = new(code, exit); Publish(value, detail is null ? code : code + ": " + detail); }
    }
    private void Publish(ProbeStage value, string text, double? valuePercent = null, TransferSnapshot? valueTransfer = null)
    {
        stage = value;
        percent = valuePercent;
        transfer = valueTransfer;
        message = text.Length > 512 ? text[..512] : text;
        revision++;
        history.Enqueue(new(revision, stage, message, percent, valueTransfer));
        while (history.Count > 64) history.Dequeue();
    }
    private sealed class ProgressSink(SteamCmdOperations owner) : IProgress<SteamCmdEvent>
    {
        public void Report(SteamCmdEvent value)
        {
            var snapshot = value.Transfer is { } t
                ? new TransferSnapshot(Enum.Parse<HCSL.Contracts.TransferPhase>(t.Phase.ToString()), t.DoneBytes, t.TotalBytes,
                    t.BytesPerSecond, t.Percent)
                : null;
            lock (owner.sync) owner.Publish(Enum.Parse<ProbeStage>(value.Stage.ToString()), value.Message, value.Percent, snapshot);
        }
    }
    private sealed class InputProvider(SteamCmdOperations owner, Guid current) : ISteamCmdInteraction
    {
        public async ValueTask<string> RespondAsync(SteamCmdChallenge value, CancellationToken ct)
        {
            TaskCompletionSource<string> pending;
            lock (owner.sync)
            {
                if (owner.id != current || !owner.running || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.input = pending;
                owner.challenge = new(value.ChallengeId, Enum.Parse<ChallengeKind>(value.Kind.ToString()));
                owner.revision++;
            }
            try { return await pending.Task.WaitAsync(ct); }
            finally
            {
                lock (owner.sync)
                    if (owner.input == pending) { owner.input = null; owner.challenge = null; owner.revision++; }
            }
        }
    }
    private static void Validate(ProbeOptions options)
    {
        if (options.Workspace is null || options.TimeoutSeconds is < 1 or > 3600 || options.InstallTimeoutSeconds is < 1 or > 86400)
            throw new OperationRejected("HCSL-OPTIONS", "Invalid workspace or timeout (1..3600 seconds required).");
        foreach (var path in new[] { options.Workspace.SettingsRoot, options.Workspace.DataRoot, options.Workspace.LibraryRoot })
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
                throw new OperationRejected("HCSL-OPTIONS", "All workspace paths must be absolute.");
        if (options.ExecutablePath is { Length: > 4096 } || options.AccountName is { Length: > 64 })
            throw new OperationRejected("HCSL-OPTIONS", "Tool path or account name is too long.");
    }
    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (sync) { stopping = true; cancellation?.Cancel(); pending = task; }
        if (pending is not null) await pending;
        cancellation?.Dispose();
    }
}
public sealed class OperationRejected(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
