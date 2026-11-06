using System.ComponentModel;
using System.Diagnostics;
using HCSL.Contracts;

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
            if (await IsReadyAsync(ct)) return;
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
                    if (await IsReadyAsync(ct)) return;
                    await Task.Delay(100, ct);
                }
            }
            using (ownership)
            {
                // Another frontend may have completed startup while this one waited for the lock.
                if (await IsReadyAsync(ct)) return;
                ct.ThrowIfCancellationRequested();
                Launch();
                while (!await IsReadyAsync(ct)) await Task.Delay(100, ct);
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
    private async Task<bool> IsReadyAsync(CancellationToken ct)
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
            return true;
        }
        catch (DaemonProtocolException ex)
        {
            throw new DaemonStartupException("HCSL-DAEMON-PROTOCOL", "The running daemon uses an incompatible protocol. Restart it before reconnecting.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (IOException) { return false; }
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
