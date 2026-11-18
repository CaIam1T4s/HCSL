using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Steam;

public sealed class SteamCmdService(string? windowsGateAssembly = null, HttpClient? httpClient = null,
    Action<WorkspaceLayout, string, Guid>? baselineRefresh = null) : ISteamCmdService
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public Task<SteamCmdResult> ProbeAsync(SteamCmdProbeRequest request,
        IProgress<SteamCmdEvent>? events = null, ISteamCmdInteraction? interaction = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(request, null, events, interaction, cancellationToken);

    public Task<SteamCmdResult> InstallAsync(SteamCmdInstallRequest request,
        IProgress<SteamCmdEvent>? events = null, ISteamCmdInteraction? interaction = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Mode)) throw new SteamCmdException("HCSL-CS2-OPTIONS", "Unknown installation mode.");
        return ExecuteAsync(request.Session, request.Mode, events, interaction, cancellationToken);
    }

    private async Task<SteamCmdResult> ExecuteAsync(SteamCmdProbeRequest request, SteamCmdInstallMode? installMode,
        IProgress<SteamCmdEvent>? events, ISteamCmdInteraction? interaction, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        void Report(SteamCmdStage stage, string message, double? percent = null, TransferProgress? transfer = null)
        {
            // Observers cannot be allowed to bypass process cleanup or cancel a task implicitly.
            try { events?.Report(new(id, stage, message, percent, transfer)); }
            catch (Exception) { /* event delivery is best-effort; operation ownership is independent */ }
        }
        if (request.AccountName is not null && !Regex.IsMatch(request.AccountName, @"\A[A-Za-z0-9_.@-]{1,64}\z"))
            throw new SteamCmdException("HCSL-STEAMCMD-ACCOUNT", "Account name contains unsupported characters.");
        var timeout = request.Timeout ?? (installMode is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > (installMode is null ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24)))
            throw new SteamCmdException("HCSL-STEAMCMD-TIMEOUT", "Timeout must be 1..3600 seconds for probes or 1..86400 seconds for installation.");
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = new LocalWorkspaceManager().Acquire(request.Workspace);
        var userDirectory = UserCoordinationDirectory();
        LocalPaths.CreatePrivateDirectory(userDirectory);
        using var userLock = AcquireSessionLock(userDirectory);
        SteamCmdJournal.RefuseExisting(userDirectory);
        var executable = SteamCmdTool.Locate(lease.Layout, request.ExecutablePath);
        var directory = Path.GetDirectoryName(executable)!;
        if (request.ExecutablePath is null)
        {
            LocalPaths.RejectLink(directory);
            LocalPaths.RejectLink(executable);
        }
        LocalPaths.CreatePrivateDirectory(directory);
        using var sessionLock = AcquireSessionLock(directory);
        SteamCmdJournal.RefuseExisting(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        Process? process = null;
        WindowsProcessJob? job = null;
        int? group = null;
        bool journalWritten = false, userJournalWritten = false;
        Task? stdout = null, stderr = null, interactionTask = null;
        using var inputStop = new CancellationTokenSource();
        var challenges = Channel.CreateBounded<SteamCmdChallenge>(new BoundedChannelOptions(4)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var parser = new SteamCmdOutput(id, (stage, message) => Report(stage, message), challenges.Writer,
            (stage, message, percent, transfer) => Report(stage, message, percent, transfer));
        Cs2Installation? installation = null;
        SteamCmdResult Result(SteamCmdStage stage, string code, int? exit = null)
        {
            Report(stage, code);
            return new(id, stage, code, exit);
        }
        try
        {
            Report(SteamCmdStage.Preparing, "Preparing SteamCMD and acquiring exclusive ownership.");
            if (!File.Exists(executable) && request.ExecutablePath is null && request.InstallIfMissing)
            {
                Report(SteamCmdStage.DownloadingTool, "Downloading the official SteamCMD bootstrap archive.");
                await SteamCmdTool.InstallAsync(executable, httpClient ?? DefaultClient, ct);
            }
            if (File.Exists(Path.Combine(directory, ".hcsl-installing")))
                throw new SteamCmdException("HCSL-STEAMCMD-INSTALL", "An interrupted bootstrap needs inspection/reinstallation.");
            SteamCmdTool.Validate(executable);
            if (installMode is not null)
            {
                installation = new Cs2Installation(lease.Layout, baselineRefresh);
                installation.Begin();
            }
            // Publish before launch: a crash at any later point fails closed until explicit recovery.
            if (OperatingSystem.IsWindows()) job = new WindowsProcessJob();
            var initialJournal = SteamCmdJournal.New(jobName: job?.Name);
            initialJournal.Save(userDirectory);
            userJournalWritten = true;
            initialJournal.Save(directory);
            journalWritten = true;
            Report(SteamCmdStage.Starting, "Starting a supervised SteamCMD process.");
            var arguments = new List<string> { "+@ShutdownOnFailedCommand", "1" };
            if (installation is not null) arguments.AddRange(["+force_install_dir", lease.Layout.ServerRoot]);
            arguments.AddRange(["+login", request.AccountName ?? "anonymous"]);
            if (installation is not null)
            {
                arguments.AddRange(["+app_update", "730", "-beta", "public"]);
                if (installMode == SteamCmdInstallMode.Validate) arguments.Add("validate");
            }
            arguments.Add("+quit");
            var start = new ProcessStartInfo
            {
                UseShellExecute = false, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (OperatingSystem.IsLinux())
            {
                start.FileName = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
                foreach (var arg in new[] { "--wait", "/bin/sh", "-c",
                    "printf 'HCSL_GATE:%s\\n' \"$$\"; IFS= read -r gate || exit 125; [ \"$gate\" = HCSL_START ] || exit 125; exec \"$@\"", "hcsl-gate", executable }.Concat(arguments))
                    start.ArgumentList.Add(arg);
            }
            else
            {
                if (windowsGateAssembly is null || !File.Exists(windowsGateAssembly))
                    throw new SteamCmdException("HCSL-STEAMCMD-PROCESS", "Windows requires the SteamCmdCheck process gate assembly.");
                start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
                start.ArgumentList.Add(windowsGateAssembly);
                start.ArgumentList.Add("--process-gate");
            }
            process = new Process { StartInfo = start };
            process.Start();
            job?.Assign(process.Handle);
            var handshake = await process.StandardOutput.ReadLineAsync(ct);
            if (handshake is null || !handshake.StartsWith("HCSL_GATE:") ||
                !int.TryParse(handshake[10..], out var gatePid) || gatePid <= 1)
                throw new SteamCmdException("HCSL-STEAMCMD-PROCESS", "Supervision gate failed before SteamCMD could start.");
            if (OperatingSystem.IsLinux())
            {
                group = gatePid;
                var groupJournal = SteamCmdJournal.New(group);
                groupJournal.Save(userDirectory);
                groupJournal.Save(directory);
                await process.StandardInput.WriteLineAsync("HCSL_START".AsMemory(), ct);
            }
            else
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new SteamCmdGateCommand(executable, arguments.ToArray())).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            stdout = ReadOutputAsync(process.StandardOutput, false, parser);
            stderr = ReadOutputAsync(process.StandardError, true, parser);
            interactionTask = RespondAsync(process, challenges.Reader, interaction, inputStop.Token);
            var exitTask = process.WaitForExitAsync(ct);
            // Input failures cannot be ignored while SteamCMD waits indefinitely for credentials.
            var first = await Task.WhenAny(exitTask, interactionTask);
            if (first == interactionTask) await interactionTask;
            await exitTask;
            await StopAsync(process, job, group);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10), ct);
            inputStop.Cancel();
            challenges.Writer.TryComplete();
            await FinishInteractionAsync(interactionTask);
            File.Delete(SteamCmdJournal.PathFor(directory));
            File.Delete(SteamCmdJournal.PathFor(userDirectory));
            journalWritten = userJournalWritten = false;
            if (parser.ChallengeOverflow)
                return Result(SteamCmdStage.Failed, "HCSL-STEAMCMD-INTERACTION", process.ExitCode);
            if (parser.MissingDependency)
                return Result(SteamCmdStage.Failed, "HCSL-STEAMCMD-DEPENDENCY", process.ExitCode);
            if (process.ExitCode != 0 || parser.Failed)
                return Result(SteamCmdStage.Failed, installation is null ? "HCSL-STEAMCMD-LOGIN" : "HCSL-CS2-INSTALL", process.ExitCode);
            if (installation is not null)
            {
                if (!parser.AppInstalled) return Result(SteamCmdStage.Failed, "HCSL-CS2-UNCONFIRMED", process.ExitCode);
                if (installation.Complete(id, () => Report(SteamCmdStage.CheckingBaseline, "Checking deployment verification records.")) is { } baselineWarning)
                {
                    Report(SteamCmdStage.CheckingBaseline, baselineWarning);
                    return Result(SteamCmdStage.Succeeded, "HCSL-CS2-BASELINE-PENDING", process.ExitCode);
                }
                return Result(SteamCmdStage.Succeeded, "HCSL-CS2-OK", process.ExitCode);
            }
            return parser.Authenticated ? Result(SteamCmdStage.Succeeded, "HCSL-STEAMCMD-OK", process.ExitCode)
                : Result(SteamCmdStage.Failed, "HCSL-STEAMCMD-UNCONFIRMED", process.ExitCode);
        }
        catch (Exception ex)
        {
            if (process is not null) Report(SteamCmdStage.Cancelling, "Stopping the supervised SteamCMD session.");
            inputStop.Cancel();
            challenges.Writer.TryComplete();
            try
            {
                if (process is not null) await StopAsync(process, job, group);
                if (stdout is not null && stderr is not null)
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
                if (interactionTask is not null) await FinishInteractionAsync(interactionTask, ignoreFailure: true);
                if (journalWritten) File.Delete(SteamCmdJournal.PathFor(directory));
                if (userJournalWritten) File.Delete(SteamCmdJournal.PathFor(userDirectory));
                if (File.Exists(SteamCmdJournal.PathFor(directory)) || File.Exists(SteamCmdJournal.PathFor(directory) + ".tmp") ||
                    File.Exists(SteamCmdJournal.PathFor(userDirectory)) || File.Exists(SteamCmdJournal.PathFor(userDirectory) + ".tmp"))
                    return Result(SteamCmdStage.RecoveryRequired, "HCSL-STEAMCMD-RECOVERY");
            }
            catch
            {
                return Result(SteamCmdStage.RecoveryRequired, "HCSL-STEAMCMD-RECOVERY");
            }
            if (ex is OperationCanceledException)
                return cancellationToken.IsCancellationRequested
                    ? Result(SteamCmdStage.Cancelled, "HCSL-STEAMCMD-CANCELLED")
                    : Result(SteamCmdStage.TimedOut, "HCSL-STEAMCMD-TIMEOUT");
            return Result(SteamCmdStage.Failed, ex is SteamCmdException known ? known.Code : ex is HttpRequestException ? "HCSL-STEAMCMD-DOWNLOAD" : "HCSL-STEAMCMD-PROCESS");
        }
        finally
        {
            installation?.Finish();
            inputStop.Cancel();
            job?.Dispose();
            process?.Dispose();
        }
    }

    public void Recover(WorkspaceRequest workspace, string? executablePath = null)
    {
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        var userDirectory = UserCoordinationDirectory();
        LocalPaths.CreatePrivateDirectory(userDirectory);
        using var userLock = AcquireSessionLock(userDirectory);
        var executable = SteamCmdTool.Locate(lease.Layout, executablePath);
        var directory = Path.GetDirectoryName(executable)!;
        LocalPaths.CreatePrivateDirectory(directory);
        using var sessionLock = AcquireSessionLock(directory);
        try
        {
            SteamCmdJournal.Recover(userDirectory, verifyOnly: true);
            SteamCmdJournal.Recover(directory, verifyOnly: true);
            SteamCmdJournal.Recover(directory);
            SteamCmdJournal.Recover(userDirectory);
        }
        catch (SteamCmdException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Recovery could not verify that the previous session has stopped; nothing was removed.", ex);
        }
    }

    internal static string UserCoordinationDirectory()
    {
        string root;
        if (OperatingSystem.IsWindows())
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HCSL", "coordination", "steamcmd");
        else
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(state) || !Path.IsPathFullyQualified(state))
                state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            root = Path.Combine(state, "hcsl", "steamcmd");
        }
        var canonical = LocalPaths.Canonicalize(root);
        LocalPaths.ValidateRoots(canonical);
        return canonical;
    }

    private static FileStream AcquireSessionLock(string directory)
    {
        var path = Path.Combine(directory, ".hcsl-session.lock");
        LocalPaths.RejectLink(path);
        try { return LocalPaths.CreateFile(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new SteamCmdException("HCSL-STEAMCMD-BUSY", "The SteamCMD directory is busy or unavailable.", ex); }
    }
    private static async Task ReadOutputAsync(StreamReader reader, bool error, SteamCmdOutput parser)
    {
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0) parser.Accept(buffer.AsSpan(0, count), error);
        parser.Finish(error);
    }
    private static async Task RespondAsync(Process process, ChannelReader<SteamCmdChallenge> reader,
        ISteamCmdInteraction? interaction, CancellationToken ct)
    {
        await foreach (var challenge in reader.ReadAllAsync(ct))
        {
            if (interaction is null)
                throw new SteamCmdException("HCSL-STEAMCMD-INTERACTION", "SteamCMD requested interactive input; an input provider is required.");
            // WaitAsync enforces cancellation even if a provider ignores its supplied token.
            var input = await interaction.RespondAsync(challenge, ct).AsTask().WaitAsync(ct);
            if (string.IsNullOrEmpty(input) || input.Length > 512 || input.Any(ch => ch is '\r' or '\n' or '\0'))
                throw new SteamCmdException("HCSL-STEAMCMD-INTERACTION", "Invalid response to authentication challenge.");
            await process.StandardInput.WriteLineAsync(input.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            input = "";
        }
    }
    private static async Task FinishInteractionAsync(Task task, bool ignoreFailure = false)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        catch (Exception) when (ignoreFailure) { }
    }
    private static async Task StopAsync(Process process, WindowsProcessJob? job, int? group)
    {
        if (OperatingSystem.IsLinux() && group is { } pg)
        {
            if (SteamCmdJournal.GroupExists(pg))
            {
                SteamCmdJournal.SignalGroup(pg, 2); // Ctrl+C-equivalent before escalating termination.
                for (int i = 0; i < 10 && SteamCmdJournal.GroupExists(pg); i++) await Task.Delay(50);
                if (SteamCmdJournal.GroupExists(pg)) SteamCmdJournal.SignalGroup(pg, 15);
                for (int i = 0; i < 20 && SteamCmdJournal.GroupExists(pg); i++) await Task.Delay(50);
                if (SteamCmdJournal.GroupExists(pg)) SteamCmdJournal.SignalGroup(pg, 9);
            }
        }
        else if (job is not null) job.Terminate();
        // Before handshake, the blocked shell/gate cannot run SteamCMD; EOF or tree kill is sufficient.
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (job is not null)
        {
            for (int i = 0; i < 40 && job.HasActiveProcesses; i++) await Task.Delay(50);
            if (job.HasActiveProcesses)
                throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Windows job remains active; journal retained.");
        }
        if (OperatingSystem.IsLinux() && group is { } check)
        {
            for (int i = 0; i < 40 && SteamCmdJournal.GroupExists(check); i++) await Task.Delay(50);
            if (SteamCmdJournal.GroupExists(check))
                throw new SteamCmdException("HCSL-STEAMCMD-RECOVERY", "Process group remains active; journal retained.");
        }
    }
}

/// <summary>Non-secret startup payload for the Windows gated worker.</summary>
public sealed record SteamCmdGateCommand(string Executable, string[] Arguments);
