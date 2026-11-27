using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HCSL.Contracts;
using HCSL.Frontend;

if (args is ["--bootstrap-worker", var workerPipe, var workerPayload])
{
    await new DaemonBootstrapper(workerPipe, workerPayload).EnsureRunningAsync();
    Console.WriteLine(JsonSerializer.Serialize(await Hello(workerPipe)));
    return 0;
}
if (args is ["--inspect", var inspectPipe])
{
    Console.WriteLine(JsonSerializer.Serialize(await Hello(inspectPipe)));
    return 0;
}
var root = Path.Combine(Path.GetTempPath(), "hcsl-bootstrap-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var oldState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(root, "state"));
var daemons = new HashSet<int>();
var workers = new List<Process>();
var count = 0;
try
{
    var solution = new DirectoryInfo(AppContext.BaseDirectory);
    while (solution is not null && !File.Exists(Path.Combine(solution.FullName, "Hello_Counter_Strike_Launcher.slnx"))) solution = solution.Parent;
    if (solution is null) throw new Exception("Run from a built checkout.");
    var config = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
    var source = Path.Combine(solution.FullName, "src", "HCSL.Daemon", "bin", config, "net10.0");
    var payload = Path.Combine(root, "payload with spaces");
    Directory.CreateDirectory(payload);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        var target = Path.Combine(payload, Path.GetFileName(file));
        File.Copy(file, target);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
    }
    var pipe = "hcsl-auto-" + Guid.NewGuid().ToString("N");
    if (args is ["--only", "native-upgrade", var image])
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This isolated fixture uses Linux XDG paths.");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
        await new DaemonBootstrapper(pipe, payload).EnsureRunningAsync();
        var managed = await Hello(pipe);
        daemons.Add(managed.ProcessId);
        var nativeDir = Directory.CreateDirectory(Path.Combine(root, "native payload with spaces")).FullName;
        var executable = Path.Combine(nativeDir, "HCSL.Daemon");
        File.Copy(image, executable);
        File.SetUnixFileMode(executable, File.GetUnixFileMode(image));
        File.WriteAllText(Path.Combine(nativeDir, DaemonPayload.NativeMarker), "native-aot");
        var bootstrap = new DaemonBootstrapper(pipe, nativeDir);
        await bootstrap.EnsureRunningAsync();
        var native = await Hello(pipe);
        daemons.Add(native.ProcessId);
        Check(native.ProcessId != managed.ProcessId && native.InstanceId != managed.InstanceId &&
            native.DaemonBuild == DaemonPayload.BundledHash(nativeDir), "native daemon did not replace managed daemon with matching identity");
        await bootstrap.EnsureRunningAsync();
        Check((await Hello(pipe)).InstanceId == native.InstanceId, "current native daemon was replaced unnecessarily");
        Console.WriteLine("PASS managed-to-native bootstrap replacement and native reuse");
        return 0;
    }
    await Run("two frontend processes automatically start one reusable detached daemon", async () =>
    {
        var a = StartWorker(pipe, payload);
        var b = StartWorker(pipe, payload);
        await Task.WhenAll(a.WaitForExitAsync(), b.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(25));
        Check(a.ExitCode == 0 && b.ExitCode == 0, "bootstrap worker failed: " + await a.StandardError.ReadToEndAsync() + await b.StandardError.ReadToEndAsync());
        var first = JsonSerializer.Deserialize<LocalResponse>(await a.StandardOutput.ReadToEndAsync())!;
        var second = JsonSerializer.Deserialize<LocalResponse>(await b.StandardOutput.ReadToEndAsync())!;
        daemons.Add(first.ProcessId);
        Check(first.InstanceId == second.InstanceId && first.ProcessId == second.ProcessId && first.ProcessId > 1, "duplicate daemon ownership");
        await Task.Delay(250);
        var current = await Hello(pipe);
        Check(current.InstanceId == first.InstanceId && current.Snapshot?.Stage == ProbeStage.Idle && current.Snapshot.Id is null,
            "daemon died with frontend or started a tool operation");
    });
    await Run("existing daemon is reused even when bundled payload is unavailable", async () =>
    {
        var before = await Hello(pipe);
        await new DaemonBootstrapper(pipe, Path.Combine(root, "missing")).EnsureRunningAsync();
        Check((await Hello(pipe)).InstanceId == before.InstanceId, "existing daemon was replaced");
    });
    await Run("framework-dependent daemon can start without an apphost", async () =>
    {
        var directory = Path.Combine(root, "DLL only payload");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(payload))
            if (Path.GetFileName(file) is not ("HCSL.Daemon" or "HCSL.Daemon.exe"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var name = "hcsl-dll-" + Guid.NewGuid().ToString("N");
        await new DaemonBootstrapper(name, directory).EnsureRunningAsync();
        var hello = await Hello(name);
        daemons.Add(hello.ProcessId);
        Check(hello.Success && hello.ProcessId > 1, "DLL-only daemon failed");
    });
    await Run("controller connects through bootstrap and closing it leaves daemon alive", async () =>
    {
        var gate = new object();
        var model = new SteamCmdController(new DaemonClient(pipe), action => { lock (gate) action(); }, new DaemonBootstrapper(pipe, payload));
        await model.ConnectAsync();
        Check(model.Connected && model.CanStart && model.LastSnapshot?.Stage == ProbeStage.Idle, "automatic controller connection failed");
        model.Dispose();
        Check((await Hello(pipe)).Success, "controller disposal stopped daemon");
    });
    await Run("missing payload is actionable and reconnect remains available", async () =>
    {
        var unused = "hcsl-missing-" + Guid.NewGuid().ToString("N");
        var bootstrapper = new DaemonBootstrapper(unused, Path.Combine(root, "not installed"));
        try { await bootstrapper.EnsureRunningAsync(); throw new Exception("missing payload accepted"); }
        catch (DaemonStartupException ex) when (ex.Code == "HCSL-DAEMON-MISSING") { }
        var gate = new object();
        using var model = new SteamCmdController(new DaemonClient(unused), action => { lock (gate) action(); }, bootstrapper);
        await model.ConnectAsync();
        Check(!model.Connected && model.CanConnect && model.Detail.Contains("HCSL-DAEMON-MISSING"), "startup failure hidden from UI");
    });
    await Run("incompatible live endpoint is reported without spawning a replacement", async () =>
    {
        var incompatible = "hcsl-incompatible-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(incompatible, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serve = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await LocalProtocol.ReadAsync<LocalRequest>(server, timeout.Token);
            await LocalProtocol.WriteAsync(server, new LocalResponse(999, request.Id, true), timeout.Token);
        });
        try
        {
            await new DaemonBootstrapper(incompatible, Path.Combine(root, "missing")).EnsureRunningAsync();
            throw new Exception("incompatible daemon accepted");
        }
        catch (DaemonStartupException ex) when (ex.Code == "HCSL-DAEMON-PROTOCOL") { }
        await serve;
    });
    await Run("pre-cancelled startup does not launch or report a missing installation", async () =>
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            await new DaemonBootstrapper("hcsl-cancel-" + Guid.NewGuid().ToString("N"), Path.Combine(root, "missing")).EnsureRunningAsync(cancel.Token);
            throw new Exception("cancel ignored");
        }
        catch (OperationCanceledException) { }
    });
    Console.WriteLine($"PASS: {count} daemon bootstrap scenarios ({RuntimeInformation.OSDescription})");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    foreach (var worker in workers)
    {
        if (!worker.HasExited) { worker.Kill(true); worker.WaitForExit(10000); }
        worker.Dispose();
    }
    foreach (var pid in daemons)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (OperatingSystem.IsLinux()) Kill(pid, 15);
            else process.Kill(true);
            process.WaitForExit(10000);
        }
        catch (ArgumentException) { }
    }
    Directory.Delete(root, true);
    if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("XDG_STATE_HOME", oldState);
}
Process StartWorker(string name, string payload)
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var value in new[] { "--bootstrap-worker", name, payload }) start.ArgumentList.Add(value);
    var process = Process.Start(start)!; workers.Add(process); return process;
}
async Task Run(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS: " + name); }
static async Task<LocalResponse> Hello(string pipe) => await new DaemonClient(pipe).SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello));
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
[DllImport("libc", EntryPoint = "kill", SetLastError = true)] static extern int Kill(int pid, int signal);
