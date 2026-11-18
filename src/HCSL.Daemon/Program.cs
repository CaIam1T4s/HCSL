using System.Reflection;
using System.Runtime.InteropServices;
using HCSL.Contracts;
using HCSL.Daemon.Ipc;
using HCSL.Daemon.Lifecycle;
using HCSL.Daemon.Workers;
using HCSL.Infrastructure.Steam;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;

if (args is ["--server-gate"]) return await ServerProcessGate.RunAsync();
if (args is ["--process-gate"]) return SteamCmdProcessGate.Run();
if (args is ["--help"])
{
    Console.WriteLine("HCSL local daemon. [--pipe <name>] [--background] [--idle-seconds 0..3600]\nCurrent-user-only local IPC; no TCP listener. Ctrl+C stops tasks and exits. Idle exit defaults to 60 seconds; 0 disables it.");
    return 0;
}
try
{
    string? requestedPipe = null;
    bool background = false;
    int idleSeconds = 60;
    bool idleSpecified = false;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--background" && !background) { background = true; continue; }
        if (args[i] == "--pipe" && requestedPipe is null && i + 1 < args.Length) { requestedPipe = args[++i]; continue; }
        if (args[i] == "--idle-seconds" && !idleSpecified && i + 1 < args.Length &&
            int.TryParse(args[++i], out idleSeconds) && idleSeconds is >= 0 and <= 3600)
        { idleSpecified = true; continue; }
        throw new ArgumentException("Use --help.");
    }
    if (background) BackgroundConsole.Detach();
    var pipe = LocalProtocol.PipeName(requestedPipe);
    HCSL.Application.Diagnostics.DiagnosticsLog.Configure(Path.Combine(LocalPaths.Defaults().DataRoot, "logs"), "daemon.log");
    HCSL.Application.Diagnostics.DiagnosticsLog.Write("daemon", "start pid=" + Environment.ProcessId + " pipe=" + pipe + " idle-seconds=" + idleSeconds + " background=" + background);
    var state = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HCSL", "daemon")
        : Path.Combine(AbsoluteStateHome(), "hcsl", "daemon");
    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(state);
    else Directory.CreateDirectory(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var lockPath = Path.Combine(state, pipe + ".lock");
    if (new FileInfo(lockPath).LinkTarget is not null) throw new IOException("Invalid daemon lock.");
    using var singleton = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var stop = new CancellationTokenSource();
    if (!background || !OperatingSystem.IsWindows())
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var term = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); }) : null;
    await using var operations = new SteamCmdOperations(new SteamCmdService(Assembly.GetExecutingAssembly().Location));
    var sessionMarker = Path.Combine(LocalPaths.Defaults().LibraryRoot, ".hcsl", "server-session.json");
    await using var servers = new ServerOperations(new SystemdServerRuntime(Assembly.GetExecutingAssembly().Location), sessionMarker,
        Path.Combine(LocalPaths.Defaults().DataRoot, "instances"));
    Console.WriteLine("HCSL daemon ready: " + pipe);
    await new LocalDaemonServer(pipe, operations, TimeSpan.FromSeconds(idleSeconds), servers).RunAsync(stop.Token);
    HCSL.Application.Diagnostics.DiagnosticsLog.Write("daemon", "exit reason=" + (stop.IsCancellationRequested ? "cancelled" : "completed"));
    return 0;
}
catch (HCSL.Core.ServerException ex)
{
    Console.Error.WriteLine(ex.Code + ": Server cleanup was not confirmed; session records were preserved.");
    return 3;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine("HCSL-DAEMON-START: Cannot start local daemon (invalid arguments, directory permissions or another owner).");
    return 2;
}
static string AbsoluteStateHome()
{
    var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
    return !string.IsNullOrWhiteSpace(state) && Path.IsPathFullyQualified(state) ? state
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
}
public static class DaemonEntry { }
