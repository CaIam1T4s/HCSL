using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Frontend.Dev;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;

var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

try
{
    var invocation = DevCommandLine.Parse(args);
    if (invocation.Command == "help") { Console.WriteLine(DevCommandLine.Usage); return 0; }
    return await RunAsync(invocation, json, cancel.Token);
}
catch (DevUsageException e)
{
    Console.Error.WriteLine("hcsl: " + e.Message);
    Console.Error.WriteLine("Run: hcsl help");
    return 2;
}
catch (DeploymentException e) { Console.Error.WriteLine(DevHints.With(e.Code + ": " + e.Message, e.Code)); return 2; }
catch (InstanceException e) { Console.Error.WriteLine(DevHints.With(e.Code + ": " + e.Message, e.Code)); return 2; }
catch (ServerException e) { Console.Error.WriteLine(DevHints.With(e.Code + ": " + e.Message, e.Code)); return 2; }
catch (WorkspaceException e) { Console.Error.WriteLine(DevHints.With(e.Code + ": " + e.Message, e.Code)); return e.Code == "HCSL-LOCK" ? 3 : 2; }
catch (IOException e) { Console.Error.WriteLine(DevHints.With(e.Message, ExtractCode(e.Message))); return 2; }
catch (OperationCanceledException) { Console.Error.WriteLine("hcsl: cancelled."); return 130; }

static async Task<int> RunAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct) => invocation.Command switch
{
    "status" => await StatusAsync(invocation, json, ct),
    "instances" => Instances(invocation, json),
    "plan" => Plan(invocation, json),
    "deploy" => Deploy(invocation, json, ct),
    "recover" => Recover(invocation),
    "start" => await StartAsync(invocation, json, ct),
    "stop" => await StopAsync(invocation, json, ct),
    "exec" => await ExecAsync(invocation, json, ct),
    "logs" => await LogsAsync(invocation, json, ct),
    "doctor" => Doctor(invocation, json),
    "plugins" => Plugins(invocation, json),
    "import" => Import(invocation, json),
    "gc" => Gc(invocation),
    "test" => await TestAsync(invocation, json, ct),
    _ => throw new DevUsageException("Unknown command. Run: hcsl help")
};

static WorkspaceRequest Workspace(DevInvocation invocation)
{
    var defaults = LocalPaths.Defaults(invocation.Get("--settings"));
    return new(invocation.Get("--settings") ?? defaults.SettingsRoot,
               invocation.Get("--data") ?? defaults.DataRoot,
               invocation.Get("--library") ?? defaults.LibraryRoot);
}

static Guid ParseId(string value, string option)
{
    if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
        throw new DevUsageException($"{option} must be a nonempty D-format UUID. Example: hcsl deploy --instance 565653a2-28fe-4fce-8bb6-6e231d55ac61");
    return id;
}

static (InstanceEntry Entry, PresetDefinition Preset) Target(WorkspaceRequest workspace, DevInvocation invocation)
{
    var catalog = new LocalInstanceStore().Execute(workspace, new(InstanceAction.List));
    var entry = DevTargets.Resolve(catalog.Instances, invocation.Get("--instance"));
    if (entry.Definition is null) throw new DevUsageException($"Instance {entry.Id:D} has no readable definition: {entry.Error}");
    return (entry, DevTargets.ResolvePreset(entry, invocation.Get("--preset")));
}

static int Instances(DevInvocation invocation, JsonSerializerOptions json)
{
    var workspace = Workspace(invocation);
    var catalog = new LocalInstanceStore().Execute(workspace, new(InstanceAction.List));
    if (invocation.Has("--json")) { Console.WriteLine(JsonSerializer.Serialize(catalog, json)); return 0; }
    if (catalog.Instances.Length == 0) { Console.WriteLine("no instances; create one in the GUI/TUI"); return 0; }
    foreach (var entry in catalog.Instances)
    {
        var definition = entry.Definition;
        var preset = entry.Presets.FirstOrDefault(p => p.Id == definition?.SelectedPresetId);
        var runtime = Path.Combine(workspace.LibraryRoot, "runtime", entry.Id.ToString("D"));
        var deployed = File.Exists(Path.Combine(runtime, "deployment.json")) ? "deployed" : "not deployed";
        Console.WriteLine((definition?.Name ?? "?") + "  " + entry.Id.ToString("D"));
        Console.WriteLine("  preset     " + (preset?.Name ?? "—") + "  port " + (definition?.GamePort.ToString() ?? "—"));
        Console.WriteLine("  runtime    " + runtime + "  (" + deployed + ")");
        if (entry.Definition is null) Console.WriteLine("  error      " + entry.Error);
    }
    return 0;
}

static DeploymentOptions Deployment(WorkspaceRequest workspace, DevInvocation invocation, Guid instanceId)
{
    var mode = invocation.Has("--full-copy") ? DeploymentCopyMode.FullCopy
        : invocation.Has("--reflink") ? DeploymentCopyMode.Reflink
        : RuntimeDeploymentService.ProbeCopyMode(Path.Combine(workspace.LibraryRoot, "runtime"));
    return new DeploymentOptions(instanceId, mode);
}

static (InstanceEntry Entry, PresetDefinition Preset, DeploymentPlan Plan) Prepare(DevInvocation invocation, WorkspaceRequest workspace)
{
    var (entry, preset) = Target(workspace, invocation);
    var options = Deployment(workspace, invocation, entry.Id);
    var plan = new RuntimeDeploymentService().Plan(workspace, options, CancellationToken.None);
    return (entry, preset, plan);
}

static void PrintPlan(DevInvocation invocation, WorkspaceRequest workspace, InstanceEntry entry, PresetDefinition preset, DeploymentPlan plan)
{
    if (invocation.Has("--json")) { Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } })); return; }
    Console.WriteLine(DevFormat.Plan(plan, entry, preset));
    var plugins = new LocalPluginStore().Execute(workspace, new(PluginAction.List, entry.Id, preset.Id));
    Console.WriteLine(DevFormat.ExplainPlugins(plugins));
}

static int Plan(DevInvocation invocation, JsonSerializerOptions json)
{
    var workspace = Workspace(invocation);
    var (entry, preset, plan) = Prepare(invocation, workspace);
    if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(plan, json));
    else PrintPlan(invocation, workspace, entry, preset, plan);
    return 0;
}

static int Deploy(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    var workspace = Workspace(invocation);
    var (entry, preset) = Target(workspace, invocation);
    var options = Deployment(workspace, invocation, entry.Id);
    var service = new RuntimeDeploymentService();
    if (invocation.Has("--recover")) Console.WriteLine("recovered  " + service.Recover(workspace, entry.Id));
    var plan = service.Plan(workspace, options, ct);
    if (!invocation.Has("--json")) PrintPlan(invocation, workspace, entry, preset, plan);
    if (invocation.Has("--dry-run")) { if (!invocation.Has("--json")) Console.WriteLine("dry-run    nothing was written"); return 0; }
    if (invocation.Has("--explain") && !invocation.Has("--json"))
        Console.WriteLine("deploying  persistent addons are backed up first; a failed publication restores them");
    var receipt = service.Deploy(workspace, options, new Progress(), ct);
    if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(receipt, json));
    else Console.WriteLine("deployed   " + receipt.Status + " · " + receipt.CopyMode + " · preset rev " + receipt.PresetRevision);
    return 0;
}

static int Recover(DevInvocation invocation)
{
    var workspace = Workspace(invocation);
    var (entry, _) = Target(workspace, invocation);
    Console.WriteLine(new RuntimeDeploymentService().Recover(workspace, entry.Id));
    return 0;
}

static async Task<(DaemonClient Daemon, ServerClient Client)> ConnectAsync(DevInvocation invocation, CancellationToken ct)
{
    string? pipe = null;
    var daemon = new DaemonClient(pipe);
    try
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromMilliseconds(500));
        await daemon.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Status), probe.Token);
    }
    catch (Exception e) when (e is IOException or OperationCanceledException)
    {
        await new DaemonBootstrapper(pipe).EnsureRunningAsync(ct);
    }
    return (daemon, new ServerClient(daemon));
}

static WorkspacePaths Paths(WorkspaceRequest workspace) => new(workspace.SettingsRoot, workspace.DataRoot, workspace.LibraryRoot);

static async Task<int> StatusAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    var workspace = Workspace(invocation);
    var (_, client) = await ConnectAsync(invocation, ct);
    var snapshot = await client.SendAsync(new(ServerAction.Status), Paths(workspace), ct);
    if (invocation.Has("--json")) { Console.WriteLine(JsonSerializer.Serialize(snapshot, json)); return 0; }
    InstanceEntry? entry = null; PresetDefinition? preset = null;
    try
    {
        var catalog = new LocalInstanceStore().Execute(workspace, new(InstanceAction.List));
        var requestedId = invocation.Get("--instance") is { } text ? ParseId(text, "--instance") : snapshot.InstanceId;
        entry = requestedId is { } id ? catalog.Instances.FirstOrDefault(i => i.Id == id) : null;
        if (entry?.Definition is not null) preset = entry.Presets.FirstOrDefault(p => p.Id == entry.Definition.SelectedPresetId);
    }
    catch (Exception e) when (e is InstanceException or WorkspaceException or DevUsageException) { }
    Console.WriteLine(DevFormat.Status(snapshot, entry, preset));
    return 0;
}

static async Task<int> StartAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    var workspace = Workspace(invocation);
    var (entry, _) = Target(workspace, invocation);
    var (daemon, client) = await ConnectAsync(invocation, ct);
    {
        var snapshot = await client.SendAsync(new(ServerAction.Start, entry.Id, StartTimeoutSeconds: 120,
            AutoDeploy: true, AllowLowMemory: invocation.Has("--allow-low-memory")), Paths(workspace), ct);
        if (invocation.Has("--wait"))
        {
            var expected = snapshot.RunId;
            while (snapshot.Active && snapshot.State is not (ServerState.Running or ServerState.Failed or ServerState.RecoveryRequired))
            {
                if (!invocation.Has("--json")) Console.Error.WriteLine("starting   " + snapshot.State + " · " + snapshot.Code);
                await Task.Delay(500, ct);
                snapshot = await client.SendAsync(new(ServerAction.Status), Paths(workspace), ct);
                if (snapshot.RunId != expected) throw new IOException("HCSL-SERVER-RUN-CHANGED: the run changed while waiting; query status.");
            }
        }
        if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(snapshot, json));
        else Console.WriteLine("started    " + snapshot.State + " · " + snapshot.Code + " · run " + (snapshot.RunId?.ToString("D") ?? "—"));
        return snapshot.State is ServerState.Failed or ServerState.RecoveryRequired ? 2 : 0;
    }
}

static async Task<int> StopAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    var workspace = Workspace(invocation);
    var (daemon, client) = await ConnectAsync(invocation, ct);
    {
        var status = await client.SendAsync(new(ServerAction.Status), Paths(workspace), ct);
        var run = invocation.Get("--run") is { } text ? ParseId(text, "--run") : status.RunId
            ?? throw new DevUsageException("No current run to stop. Use: hcsl status");
        var snapshot = await client.SendAsync(new(ServerAction.Stop, RunId: run), Paths(workspace), ct);
        if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(snapshot, json));
        else Console.WriteLine("stopped    " + snapshot.State + " · " + snapshot.Code);
        return 0;
    }
}

static async Task<int> ExecAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    var workspace = Workspace(invocation);
    var text = string.Join(' ', invocation.Positionals);
    var (daemon, client) = await ConnectAsync(invocation, ct);
    {
        var status = await client.SendAsync(new(ServerAction.Status), Paths(workspace), ct);
        var run = invocation.Get("--run") is { } raw ? ParseId(raw, "--run") : status.RunId
            ?? throw new ServerException("HCSL-CONSOLE-STATE", "No current run.");
        var receipt = await client.SendConsoleAsync(new(run, Guid.NewGuid(), text, invocation.Has("--confirm-write")), ct);
        if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(receipt, json));
        else Console.WriteLine(receipt.Delivery + " · " + (receipt.Code ?? receipt.CommandId.ToString("D")));
        if (receipt.Delivery != ConsoleDelivery.Written)
            Console.Error.WriteLine(DevHints.With("console not written (" + receipt.Code + ")", receipt.Code));
        return receipt.Delivery == ConsoleDelivery.Written ? 0 : 2;
    }
}

static async Task<int> LogsAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    var workspace = Workspace(invocation);
    var (daemon, client) = await ConnectAsync(invocation, ct);
    {
        var after = long.TryParse(invocation.Get("--after"), out var parsed) ? parsed : 0;
        while (true)
        {
            var snapshot = await client.SendAsync(new(ServerAction.Logs, AfterSequence: after), Paths(workspace), ct);
            if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(snapshot, json));
            else foreach (var line in snapshot.Lines) Console.WriteLine(line.Text);
            after = snapshot.NextSequence;
            if (!invocation.Has("--follow")) return 0;
            await Task.Delay(500, ct);
        }
    }
}

static int Plugins(DevInvocation invocation, JsonSerializerOptions json)
{
    var workspace = Workspace(invocation);
    var (entry, preset) = Target(workspace, invocation);
    var result = new LocalPluginStore().Execute(workspace, new(PluginAction.List, entry.Id, preset.Id));
    Console.WriteLine(invocation.Has("--json") ? JsonSerializer.Serialize(result, json) : DevFormat.Plugins(result));
    return 0;
}

static int Doctor(DevInvocation invocation, JsonSerializerOptions json)
{
    var workspace = Workspace(invocation);
    var problems = new List<string>();
    var lines = new List<string>();
    void Ok(string text) => lines.Add("ok         " + text);
    void Warn(string text) { lines.Add("warn       " + text); problems.Add(text); }
    Ok("settings   " + workspace.SettingsRoot);
    Ok("data       " + workspace.DataRoot);
    Ok("library    " + workspace.LibraryRoot);
    if (!Directory.Exists(workspace.LibraryRoot)) { Warn("library root does not exist; run the GUI/daemon once to initialize it"); return Report(); }
    var catalog = new LocalInstanceStore().Execute(workspace, new(InstanceAction.List));
    Ok("instances  " + catalog.Instances.Length);
    var (entry, preset) = Target(workspace, invocation);
    var addons = Path.Combine(workspace.DataRoot, "instances", entry.Id.ToString("D"), "presets", preset.Id.ToString("D"), "addons");
    try
    {
        Directory.CreateDirectory(addons);
        var probe = Path.Combine(addons, ".hcsl-doctor-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "probe"); File.Delete(probe);
        Ok("addons     writable");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warn("addons is not writable (" + e.GetType().Name + "); fix with: chmod -R u+w " + addons); }
    var plugins = new LocalPluginStore().Execute(workspace, new(PluginAction.List, entry.Id, preset.Id));
    Ok("plugins    " + plugins.Available.Length + " available, " + plugins.Selected.Count(s => s.Enabled) + " enabled");
    foreach (var rejected in plugins.Rejected ?? []) Warn("package rejected: " + rejected.Id + " " + rejected.Version + " (" + rejected.Code + ")");
    foreach (var missing in plugins.Selected.Where(s => s.Enabled && !s.Available)) Warn("enabled package missing: " + missing.Id + " " + missing.Version);
    try { var plan = new RuntimeDeploymentService().Plan(workspace, new DeploymentOptions(entry.Id, RuntimeDeploymentService.ProbeCopyMode(Path.Combine(workspace.LibraryRoot, "runtime")))); Ok("plan       valid (" + plan.CopyMode + ", " + plan.FileCount + " files)"); }
    catch (DeploymentException e) { Warn("plan failed: " + e.Code + " " + e.Message); }
    catch (WorkspaceException e) when (e.Code == "HCSL-LOCK") { Warn("workspace is busy (GUI/TUI/daemon); plan/deploy checks were skipped"); }
    var journal = Path.Combine(workspace.LibraryRoot, ".hcsl", "deployment-" + entry.Id.ToString("D") + ".json");
    if (File.Exists(journal)) Warn("interrupted deployment journal present; run: hcsl recover");
    var runtimeRoot = Path.Combine(workspace.LibraryRoot, "runtime");
    var previous = Directory.Exists(runtimeRoot) ? Directory.GetDirectories(runtimeRoot, ".previous-" + entry.Id + "-*").Length : 0;
    var staging = Directory.Exists(runtimeRoot) ? Directory.GetDirectories(runtimeRoot, ".staging-" + entry.Id + "-*").Length : 0;
    if (previous + staging > 0) Warn("stale deployments: " + previous + " previous, " + staging + " staging; run: hcsl gc --yes");
    var runtime = Path.Combine(runtimeRoot, entry.Id.ToString("D"));
    if (Directory.Exists(runtime))
    {
        var gameinfo = Path.Combine(runtime, "game", "csgo", "gameinfo.gi");
        var wantsMetamod = plugins.Selected.Any(s => s.Enabled && s.Id.Equals("metamod", StringComparison.OrdinalIgnoreCase));
        if (wantsMetamod && File.Exists(gameinfo) && !File.ReadAllText(gameinfo).Contains("csgo/addons/metamod", StringComparison.Ordinal))
            Warn("metamod is enabled but runtime gameinfo.gi does not reference csgo/addons/metamod; run: hcsl deploy");
        Ok("runtime    " + runtime);
    }
    else Warn("no runtime deployment; run: hcsl deploy");
    return Report();
    int Report()
    {
        if (invocation.Has("--json")) { Console.WriteLine(JsonSerializer.Serialize(new { ok = problems.Count == 0, problems, checks = lines }, json)); return problems.Count == 0 ? 0 : 2; }
        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine(problems.Count == 0 ? "result     no problems found" : "result     " + problems.Count + " problem(s) listed above");
        return problems.Count == 0 ? 0 : 2;
    }
}

static int Import(DevInvocation invocation, JsonSerializerOptions json)
{
    var path = invocation.Positionals.FirstOrDefault()
        ?? throw new DevUsageException("import needs a folder or archive. Example: hcsl import ~/Downloads/sm.tar.gz --probe");
    if (!Directory.Exists(path) && !File.Exists(path))
        throw new DevUsageException("Source does not exist: " + path);
    var workspace = Workspace(invocation);
    var importer = new LocalPluginImporter();
    var preview = importer.Probe(workspace, new(path));
    if (invocation.Has("--probe"))
    {
        if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(preview, json));
        else
        {
            Console.WriteLine("kind       " + preview.Kind);
            Console.WriteLine("id         " + preview.Id);
            Console.WriteLine("name       " + preview.Name);
            Console.WriteLine("version    " + preview.Version);
            Console.WriteLine("files      " + preview.Files + " · " + DevFormat.Bytes(preview.Bytes));
            if (preview.MetaPlugin is not null) Console.WriteLine("meta       " + preview.MetaPlugin);
            foreach (var warning in preview.Warnings) Console.WriteLine("warn       " + warning);
        }
        return 0;
    }
    var instanceId = Guid.Empty; var presetId = Guid.Empty; long revision = 0;
    if (invocation.Has("--enable"))
    {
        var (entry, preset) = Target(workspace, invocation);
        instanceId = entry.Id; presetId = preset.Id; revision = entry.Definition!.Revision;
    }
    var archive = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? path : null;
    var result = importer.Execute(workspace, new(instanceId, presetId, archive is null ? path : "",
        invocation.Get("--id") ?? preview.Id, invocation.Get("--name") ?? preview.Name,
        invocation.Get("--version") ?? preview.Version, invocation.Get("--meta") ?? preview.MetaPlugin,
        invocation.Has("--enable"), invocation.Has("--force"), revision, archive));
    if (invocation.Has("--json")) Console.WriteLine(JsonSerializer.Serialize(result, json));
    else
    {
        Console.WriteLine("imported   " + result.Id + " " + result.Version + " → " + result.PackagePath);
        Console.WriteLine("size       " + result.Files + " files · " + DevFormat.Bytes(result.Bytes));
        Console.WriteLine("enabled    " + (result.Enabled ? "yes" : "no"));
        if (!result.CurrentPlatformSupported) Console.WriteLine("warn       no binary for this platform");
        foreach (var warning in result.Warnings) Console.WriteLine("warn       " + warning);
    }
    return 0;
}

static int Gc(DevInvocation invocation)
{
    var workspace = Workspace(invocation);
    var (entry, _) = Target(workspace, invocation);
    var runtimeRoot = Path.Combine(workspace.LibraryRoot, "runtime");
    if (!Directory.Exists(runtimeRoot)) { Console.WriteLine("gc         nothing to collect"); return 0; }
    var targets = Directory.GetDirectories(runtimeRoot, ".previous-" + entry.Id + "-*")
        .Concat(Directory.GetDirectories(runtimeRoot, ".staging-" + entry.Id + "-*")).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    if (targets.Length == 0) { Console.WriteLine("gc         nothing to collect"); return 0; }
    foreach (var target in targets) Console.WriteLine("candidate  " + target);
    if (!invocation.Has("--yes"))
    {
        Console.WriteLine("gc         dry run: pass --yes to delete " + targets.Length + " directory(ies)");
        return 0;
    }
    foreach (var target in targets) Directory.Delete(target, true);
    Console.WriteLine("gc         removed " + targets.Length + " directory(ies)");
    return 0;
}

static string? ExtractCode(string message)
{
    var index = message.IndexOf("HCSL-", StringComparison.Ordinal);
    if (index < 0) return null;
    var end = message.IndexOfAny([':', ' ', ')'], index);
    return end < 0 ? message[index..] : message[index..end];
}


static async Task<int> TestAsync(DevInvocation invocation, JsonSerializerOptions json, CancellationToken ct)
{
    if (invocation.Positionals.Length == 0)
        throw new DevUsageException("test needs a target. Example: hcsl test mms ~/Downloads/mmsource-2.0.0-linux.tar.gz");
    var target = invocation.Positionals[0];
    if (target == "plugins") return await RunSuitesAsync(ct);
    if (target == "mms")
    {
        var source = invocation.Positionals.ElementAtOrDefault(1)
            ?? throw new DevUsageException("test mms needs a release directory or .tar.gz. Example: hcsl test mms ~/Downloads/mmsource-2.0.0-linux.tar.gz");
        return await ImportMmsAsync(invocation, source);
    }
    throw new DevUsageException($"Unknown test target '{target}'. Use: mms <dir|tar.gz> or plugins.");
}

static async Task<int> RunSuitesAsync(CancellationToken ct)
{
    var root = RepositoryRoot() ?? throw new IOException("HCSL-REPO: run this from inside the repository (Hello_Counter_Strike_Launcher.slnx not found).");
    var suites = new[]
    {
        new[] { "run", "--project", Path.Combine(root, "tests/HCSL.ServerTests"), "--", "--only", "plugins" },
        new[] { "run", "--project", Path.Combine(root, "tests/HCSL.DeploymentTests") }
    };
    foreach (var arguments in suites)
    {
        Console.WriteLine("run        dotnet " + string.Join(' ', arguments));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("HCSL-REPO: cannot start dotnet.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) return process.ExitCode;
    }
    return 0;
}

static string? RepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hello_Counter_Strike_Launcher.slnx")))
        directory = directory.Parent;
    return directory?.FullName;
}

static async Task<int> ImportMmsAsync(DevInvocation invocation, string source)
{
    var workspace = Workspace(invocation);
    var version = invocation.Get("--version") ?? "2.0.0";
    PluginRules.Version(version);
    var temp = Path.Combine(Path.GetTempPath(), "hcsl-mms-" + Guid.NewGuid().ToString("N"));
    try
    {
        string root;
        if (Directory.Exists(source)) root = Path.GetFullPath(source);
        else if (File.Exists(source) && (source.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)))
        {
            Directory.CreateDirectory(temp);
            await using var file = File.OpenRead(source);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, temp, overwriteFiles: true);
            root = temp;
        }
        else throw new DevUsageException("test mms expects an extracted directory or a .tar.gz archive. Example: hcsl test mms ~/Downloads/mmsource-2.0.0-linux.tar.gz");

        var metamod = FindMetamod(root) ?? throw new DevUsageException("No addons/metamod directory with a bin folder was found. Check the archive layout.");
        var cs2Binary = Directory.EnumerateFiles(metamod, "metamod.2.*.so", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Replace('\\', '/').Contains("/linuxsteamrt64/", StringComparison.Ordinal));
        if (cs2Binary is null)
            Console.Error.WriteLine("warning    no linuxsteamrt64/metamod.2.*.so found; this may be a CS:GO-era package and will not load in CS2");

        var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "metamod", version);
        if (Directory.Exists(package) && !invocation.Has("--force"))
            throw new DevUsageException($"Package already exists: {package}. Pass --force to replace it, or --version <other>.");
        if (Directory.Exists(package)) Directory.Delete(package, true);
        var files = Path.Combine(package, "files", "csgo", "addons");
        Directory.CreateDirectory(files);
        CopyDirectory(metamod, Path.Combine(files, "metamod"));

        var baseGameInfo = Path.Combine(workspace.LibraryRoot, "server", "game", "csgo", "gameinfo.gi");
        if (!File.Exists(baseGameInfo)) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Base gameinfo.gi is missing; install CS2 before importing Metamod.");
        var overlay = Path.Combine(package, "files", "csgo", "gameinfo.gi");
        var lines = File.ReadAllLines(baseGameInfo).ToList();
        if (!lines.Any(line => line.Contains("csgo/addons/metamod", StringComparison.OrdinalIgnoreCase)))
        {
            var index = lines.FindIndex(line => line.TrimStart().StartsWith("Game", StringComparison.Ordinal) && line.TrimEnd().EndsWith("csgo", StringComparison.Ordinal));
            if (index < 0) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Could not find a 'Game csgo' SearchPath line in gameinfo.gi.");
            var indent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
            lines.Insert(index, indent + "Game\tcsgo/addons/metamod");
        }
        File.WriteAllLines(overlay, lines);
        File.WriteAllText(Path.Combine(package, "plugin.json"),
            "{\"FormatVersion\":1,\"Id\":\"metamod\",\"Name\":\"Metamod:Source\",\"Version\":\"" + version + "\"}");

        var count = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Count();
        var bytes = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        if (count > PluginRules.MaxPackageFiles || bytes > PluginRules.MaxPackageBytes)
            throw new DevUsageException($"Package exceeds limits ({count} files, {DevFormat.Bytes(bytes)}); trim legacy engines under addons/metamod/bin/.");

        Console.WriteLine("package    " + package);
        Console.WriteLine("version    " + version + " · " + count + " files · " + DevFormat.Bytes(bytes));
        Console.WriteLine("gameinfo   overlay " + (cs2Binary is null ? "CHECK (no cs2 binary)" : "csgo/addons/metamod present"));
        if (invocation.Has("--enable"))
        {
            var (entry, preset) = Target(workspace, invocation);
            var store = new LocalPluginStore();
            var current = store.Execute(workspace, new(PluginAction.List, entry.Id, preset.Id));
            var selection = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).Where(s => s.Id != "metamod").Append(new("metamod", version, true)).ToArray();
            var saved = store.Execute(workspace, new(PluginAction.Save, entry.Id, preset.Id, entry.Definition!.Revision, current.Hash, selection));
            Console.WriteLine("selected   metamod " + version + " in preset " + preset.Name + " (revision " + saved.Revision + ")");
        }
        Console.WriteLine("next       hcsl doctor && hcsl deploy --explain");
        return 0;
    }
    finally
    {
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { }
    }
}

static string? FindMetamod(string root)
{
    if (Path.GetFileName(root).Equals("metamod", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(root, "bin"))) return root;
    foreach (var directory in Directory.EnumerateDirectories(root, "metamod", SearchOption.AllDirectories))
        if (Path.GetFileName(Path.GetDirectoryName(directory) ?? "").Equals("addons", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(directory, "bin"))) return directory;
    return null;
}

static void CopyDirectory(string source, string destination)
{
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
}

sealed class Progress : IProgress<DeploymentEvent>
{
    public void Report(DeploymentEvent e) => Console.Error.WriteLine("progress   " + e.Stage + " " + e.FilesCompleted + "/" + e.TotalFiles);
}
