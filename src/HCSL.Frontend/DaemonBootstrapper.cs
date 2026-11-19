using System.ComponentModel;
using System.Diagnostics;
using HCSL.Contracts;
using HCSL.Core;

namespace HCSL.Frontend;

public interface IDaemonBootstrapper
{
    Task EnsureRunningAsync(CancellationToken cancellationToken = default);
}
public sealed class DaemonStartupException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>Application startup transport composition, not a SteamCMD/task launcher.</summary>
public sealed class DaemonBootstrapper(string? pipeName = null, string? payloadDirectory = null) : IDaemonBootstrapper
{
    private readonly string pipe = LocalProtocol.PipeName(pipeName);
    private readonly string payload = Path.GetFullPath(payloadDirectory ?? Path.Combine(AppContext.BaseDirectory, "daemon"));

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        try
        {
            if (await EnsureCurrentAsync(ct)) return;
            var state = StateDirectory();
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(state);
            else Directory.CreateDirectory(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            FileStream? ownership = null;
            var lockPath = Path.Combine(state, pipe + ".bootstrap.lock");
            while (ownership is null)
            {
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(lockPath).LinkTarget is not null)
                    throw new DaemonStartupException("HCSL-DAEMON-ACCESS", "Invalid daemon startup lock.");
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    ownership = new FileStream(lockPath, options);
                }
                catch (IOException)
                {
                    if (await EnsureCurrentAsync(ct)) return;
                    await Task.Delay(100, ct);
                }
            }
            using (ownership)
            {
                // Another frontend may have completed startup while this one waited for the lock.
                if (await EnsureCurrentAsync(ct)) return;
                ct.ThrowIfCancellationRequested();
                Launch();
                while (!await EnsureCurrentAsync(ct)) await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DaemonStartupException("HCSL-DAEMON-TIMEOUT", "The local daemon did not become ready within 15 seconds. Retry connection or check the installation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new DaemonStartupException("HCSL-DAEMON-START", "Cannot start or authenticate the local daemon. Check installation, directory permissions and matching user/elevation.", ex);
        }
    }
    /// <summary>Ready and running the bundled binary. A stale process is retired here, before any request can
    /// be answered by old code; returning false then makes the caller launch the fresh payload.</summary>
    private async Task<bool> EnsureCurrentAsync(CancellationToken ct)
    {
        var hello = await ProbeAsync(ct);
        if (hello is null) return false;
        if (!IsStale(hello)) return true;
        await ReplaceAsync(hello, ct);
        return false;
    }
    private bool IsStale(LocalResponse hello) =>
        DaemonPayload.IsStale(hello.DaemonBuild, payload);
    /// <summary>Stop an old daemon so the next launch uses the rebuilt payload. Never trades away a running
    /// server or an in-flight SteamCMD task: the operator stops those, the frontend does not guess.</summary>
    private async Task ReplaceAsync(LocalResponse hello, CancellationToken ct)
    {
        await RequireIdleAsync(ct);
        if (hello.ProcessId <= 0)
            throw new DaemonStartupException("HCSL-DAEMON-STALE",
                "A newer daemon is bundled but the running daemon did not report its process. Stop it and retry.");
        Process process;
        try { process = Process.GetProcessById(hello.ProcessId); }
        catch (ArgumentException) { return; } // Already gone; the launch path handles the rest.
        using (process)
        {
            if (!IsDaemon(process))
                throw new DaemonStartupException("HCSL-DAEMON-STALE",
                    "A newer daemon is bundled but the process on the pipe is not the bundled daemon; refusing to terminate it.");
            Terminate(process);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProbeAsync(ct) is null) return;
                await Task.Delay(100, ct);
            }
            throw new DaemonStartupException("HCSL-DAEMON-STALE",
                "The previous daemon did not exit. Stop it manually and retry.");
        }
    }
    private async Task RequireIdleAsync(CancellationToken ct)
    {
        var client = new DaemonClient(pipe);
        try
        {
            var steam = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Status), ct);
            if (steam.Snapshot?.Running == true)
                throw new DaemonStartupException("HCSL-DAEMON-STALE",
                    "A newer daemon is bundled but a SteamCMD task is running. Let it finish, then restart the interface.");
            var server = await new ServerClient(client).SendAsync(new(ServerAction.Status), null, ct);
            if (server.Active)
                throw new DaemonStartupException("HCSL-DAEMON-STALE",
                    "A newer daemon is bundled but the server is running. Stop it from the manage page, then restart the interface.");
        }
        catch (Exception e) when (e is IOException or HCSL.Core.ServerException) { /* Unreadable state is not permission to kill. */
            throw new DaemonStartupException("HCSL-DAEMON-STALE",
                "A newer daemon is bundled and its state could not be read. Stop it and retry.", e); }
    }
    /// <summary>Only a process whose executable really is the daemon is terminated; a recycled PID must never
    /// take an unrelated program with it.</summary>
    private static bool IsDaemon(Process process)
    {
        try
        {
            var path = OperatingSystem.IsLinux()
                ? new FileInfo("/proc/" + process.Id + "/exe").LinkTarget
                : process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return false;
            var name = Path.GetFileName(path);
            return name.StartsWith("HCSL.Daemon", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static void Terminate(Process process)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                using var kill = Process.Start(new ProcessStartInfo { FileName = File.Exists("/bin/kill") ? "/bin/kill" : "/usr/bin/kill",
                    ArgumentList = { "-TERM", process.Id.ToString() }, UseShellExecute = false, CreateNoWindow = true });
                kill?.WaitForExit(2000);
                return;
            }
            if (!process.CloseMainWindow()) process.Kill(entireProcessTree: false);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
    }
    private async Task<LocalResponse?> ProbeAsync(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(250);
        try
        {
            var response = await new DaemonClient(pipe).SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), probe.Token);
            if (!response.Success)
            {
                if (response.Code == "HCSL-IPC-VERSION")
                    throw new DaemonStartupException("HCSL-DAEMON-PROTOCOL", "The running daemon uses an incompatible protocol. Restart it before reconnecting.");
                throw new DaemonStartupException(response.Code ?? "HCSL-DAEMON-ERROR", response.Message ?? "Cannot read daemon workspace settings.");
            }
            return response;
        }
        catch (DaemonProtocolException ex)
        {
            throw new DaemonStartupException("HCSL-DAEMON-PROTOCOL", "The running daemon uses an incompatible protocol. Restart it before reconnecting.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
    }
    private void Launch()
    {
        var appHost = Path.Combine(payload, OperatingSystem.IsWindows() ? "HCSL.Daemon.exe" : "HCSL.Daemon");
        var assembly = Path.Combine(payload, "HCSL.Daemon.dll");
        if (!File.Exists(assembly) || !File.Exists(Path.Combine(payload, "HCSL.Daemon.runtimeconfig.json")) ||
            !File.Exists(Path.Combine(payload, "HCSL.Daemon.deps.json")))
            throw new DaemonStartupException("HCSL-DAEMON-MISSING", "The bundled daemon is missing. Rebuild or reinstall the complete application.");
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = payload, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsLinux())
        {
            start.FileName = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
            if (!File.Exists(start.FileName))
                throw new DaemonStartupException("HCSL-DAEMON-DEPENDENCY", "Linux automatic startup requires setsid (util-linux).");
            start.ArgumentList.Add("--fork");
            start.ArgumentList.Add(File.Exists(appHost) ? appHost : DotnetHost());
        }
        else if (OperatingSystem.IsWindows()) start.FileName = File.Exists(appHost) ? appHost : DotnetHost();
        else throw new DaemonStartupException("HCSL-DAEMON-PLATFORM", "Only Windows and Linux are supported.");
        if (!File.Exists(appHost)) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipe);
        using var process = Process.Start(start) ?? throw new DaemonStartupException("HCSL-DAEMON-START", "Cannot launch the local daemon.");
        // The background child replaces its OS standard handles with null devices before accepting IPC.
        // Never kill it on frontend disposal or startup cancellation.
        process.StandardInput.Close();
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { await reader.BaseStream.CopyToAsync(Stream.Null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static string StateDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HCSL", "daemon");
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(state) || !Path.IsPathFullyQualified(state))
            state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "hcsl", "daemon");
    }
}
