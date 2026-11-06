using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Storage;

// No test-framework packages: these executable integration checks run offline with the SDK.
var root = Path.Combine(Path.GetTempPath(), "hcsl-foundation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var manager = new LocalWorkspaceManager();
var passed = 0;
var workers = new List<Process>();
try
{
    Run("stable identities, layout and settings", () =>
    {
        var request = Request("stable");
        WorkspaceIdentity identity;
        using (var lease = manager.Acquire(request))
        {
            identity = lease.Identity;
            Check(Directory.Exists(lease.Layout.ServerRoot), "server missing");
            Check(Directory.Exists(Path.Combine(request.DataRoot, "operations")), "operations directory missing");
            Check(Directory.Exists(Path.Combine(request.LibraryRoot, "plugin-packages")), "plugin package directory missing");
            Check(Directory.Exists(lease.Layout.SteamCmdRoot), "steamcmd missing");
            Check(lease.Capacity.DataAvailableBytes > 0, "capacity missing");
            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(request.DataRoot);
                Check((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)) == 0,
                    "new data root is not private");
            }
        }
        var settings = File.ReadAllBytes(Path.Combine(request.SettingsRoot, "settings.json"));
        using var again = manager.Acquire(request);
        Check(identity == again.Identity, "identities changed");
        Check(settings.SequenceEqual(File.ReadAllBytes(Path.Combine(request.SettingsRoot, "settings.json"))),
            "unchanged settings were rewritten");
    });
    Run("unowned nonempty roots are untouched", () =>
    {
        foreach (var kind in new[] { "settings", "data", "library" })
        {
            var request = Request("unowned-" + kind);
            var target = kind == "settings" ? request.SettingsRoot : kind == "data" ? request.DataRoot : request.LibraryRoot;
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "important.txt"), "keep");
            var before = Snapshot(Path.GetDirectoryName(target)!);
            Expect("HCSL-UNOWNED", () => manager.Acquire(request));
            Check(before == Snapshot(Path.GetDirectoryName(target)!), "unowned directory was changed");
        }
    });
    Run("overlap, traversal, relative paths and files rejected", () =>
    {
        var request = Request("paths");
        Expect("HCSL-PATH", () => manager.Acquire(request with { LibraryRoot = Path.Combine(request.DataRoot, "library") }));
        Expect("HCSL-PATH", () => manager.Acquire(request with { DataRoot = "relative" }));
        Expect("HCSL-PATH", () => manager.Acquire(request with { DataRoot = Path.Combine(root, "x", "..", "y") }));
        var file = Path.Combine(root, "file");
        File.WriteAllText(file, "keep");
        Expect("HCSL-PATH", () => manager.Acquire(request with { DataRoot = file }));
        Check(!Directory.Exists(request.SettingsRoot), "invalid paths created directories");
    });
    Run("unknown marker version and corrupted JSON refused", () =>
    {
        var request = Request("format");
        using (manager.Acquire(request)) { }
        var marker = Path.Combine(request.LibraryRoot, "library.json");
        var node = JsonNode.Parse(File.ReadAllText(marker))!;
        node["FormatVersion"] = 99;
        File.WriteAllText(marker, node.ToJsonString());
        var before = Snapshot(Path.GetDirectoryName(request.DataRoot)!);
        Expect("HCSL-FORMAT", () => manager.Acquire(request));
        Check(before == Snapshot(Path.GetDirectoryName(request.DataRoot)!), "unknown version changed data");
        File.WriteAllText(marker, "{");
        Expect("HCSL-FORMAT", () => manager.Acquire(request));
    });
    Run("migration is refused and old settings preserved", () =>
    {
        var request = Request("migration");
        using (manager.Acquire(request)) { }
        var path = Path.Combine(request.SettingsRoot, "settings.json");
        var before = File.ReadAllBytes(path);
        var other = request with { LibraryRoot = Path.Combine(root, "migration-new-library") };
        Expect("HCSL-MIGRATION", () => manager.Acquire(other));
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "settings changed on failure");
        Check(!File.Exists(Path.Combine(other.LibraryRoot, "library.json")), "failed migration claimed new library");
    });
    Run("interrupted child-directory creation can be retried", () =>
    {
        var request = Request("retry");
        WorkspaceIdentity identity;
        using (var lease = manager.Acquire(request)) identity = lease.Identity;
        Directory.Delete(Path.Combine(request.LibraryRoot, "server"));
        using var again = manager.Acquire(request);
        Check(again.Identity == identity && Directory.Exists(again.Layout.ServerRoot), "initialization retry failed");
    });
    Run("managed writable directory links refused", () =>
    {
        var request = Request("child-link");
        using (manager.Acquire(request)) { }
        var external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        var target = Path.Combine(request.LibraryRoot, "server");
        Directory.Delete(target);
        Directory.CreateSymbolicLink(target, external);
        Expect("HCSL-PATH", () => manager.Acquire(request));
        Check(!Directory.EnumerateFileSystemEntries(external).Any(), "external directory modified");
        Directory.Delete(target);
    });
    Run("cross-process conflict, independent workspaces, normal release and crash recovery", () =>
    {
        var a = Request("process-a");
        var owner = Start(a, hold: true);
        Ready(owner);
        var before = Snapshot(Path.GetDirectoryName(a.DataRoot)!);
        CheckExit(a, 3);
        Check(before == Snapshot(Path.GetDirectoryName(a.DataRoot)!), "contender modified active workspace");
        var b = Request("process-b") with { LibraryRoot = a.LibraryRoot };
        CheckExit(b, 3);
        Check(before == Snapshot(Path.GetDirectoryName(a.DataRoot)!), "other data root modified active library");
        Check(!File.Exists(Path.Combine(b.DataRoot, "workspace.json")), "contender published identity");
        {
            var independent = Start(Request("independent"), hold: true);
            Ready(independent);
            independent.StandardInput.WriteLine();
            independent.StandardInput.Flush();
            WaitExit(independent, 0);
        }
        owner.StandardInput.WriteLine();
        owner.StandardInput.Flush();
        WaitExit(owner, 0);
        {
            var crashed = Start(a, hold: true);
            Ready(crashed);
            crashed.Kill(entireProcessTree: true);
            Check(crashed.WaitForExit(10000), "crash timeout");
        }
        CheckExit(a, 0);
    });
    Run("root aliases including a linked ancestor cannot bypass locks", () =>
    {
        var request = Request("alias");
        var owner = Start(request, hold: true);
        Ready(owner);
        var link = Path.Combine(root, "alias-parent-link");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(request.DataRoot)!);
        try
        {
            var alternate = request with
            {
                DataRoot = Path.Combine(link, "data"),
                SettingsRoot = Path.Combine(link, "settings"),
                LibraryRoot = Path.Combine(link, "library")
            };
            CheckExit(alternate, 3);
            owner.StandardInput.WriteLine();
            owner.StandardInput.Flush();
            WaitExit(owner, 0);
            CheckExit(alternate, 0);
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(true); owner.WaitForExit(); }
            Directory.Delete(link);
        }
    });
    Run("simultaneous first initialization elects one owner and preserves identity", () =>
    {
        var request = Request("bootstrap-race");
        var first = Start(request, hold: true);
        var second = Start(request, hold: true);
        var lines = Task.WhenAll(first.StandardOutput.ReadLineAsync(), second.StandardOutput.ReadLineAsync())
            .WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        Check(lines.Count(line => line is not null) == 1, "expected exactly one initialized owner");
        var winner = lines[0] is not null ? first : second;
        var loser = lines[0] is not null ? second : first;
        using var status = JsonDocument.Parse(lines[0] ?? lines[1]!);
        var dataId = status.RootElement.GetProperty("Identity").GetProperty("DataId").GetGuid();
        WaitExit(loser, 3);
        winner.StandardInput.WriteLine();
        winner.StandardInput.Flush();
        WaitExit(winner, 0);
        using var recovered = manager.Acquire(request);
        Check(recovered.Identity.DataId == dataId, "bootstrap changed the elected identity");
    });
    Run("spaces, Unicode and nested directory aliases", () =>
    {
        var request = Request("路径 with spaces");
        using (manager.Acquire(request)) { }
        var parentLink = Path.Combine(root, "intermediate-link");
        var nestedLink = Path.Combine(root, "nested-link");
        Directory.CreateSymbolicLink(parentLink, Path.GetDirectoryName(request.DataRoot)!);
        Directory.CreateSymbolicLink(nestedLink, Path.Combine(parentLink, "data"));
        try
        {
            using var lease = manager.Acquire(request with { DataRoot = nestedLink });
            Check(lease.Layout.DataRoot == request.DataRoot, "nested alias not canonicalized");
            Expect("HCSL-PATH", () => manager.Acquire(Request("alias-overlap") with
            {
                DataRoot = nestedLink, LibraryRoot = Path.Combine(parentLink, "data", "nested-library")
            }));
        }
        finally { Directory.Delete(nestedLink); Directory.Delete(parentLink); }
    });
    Run("invalid settings and reserved file failure preserve original bytes", () =>
    {
        var request = Request("invalid-settings");
        using (manager.Acquire(request)) { }
        var settings = Path.Combine(request.SettingsRoot, "settings.json");
        File.WriteAllText(settings, "{broken");
        var before = Snapshot(Path.GetDirectoryName(request.DataRoot)!);
        Expect("HCSL-FORMAT", () => manager.Acquire(request));
        Check(before == Snapshot(Path.GetDirectoryName(request.DataRoot)!), "failed read changed workspace");
        var reserved = Request("reserved-file");
        using (manager.Acquire(reserved)) { }
        Directory.Delete(Path.Combine(reserved.LibraryRoot, "server"));
        File.WriteAllText(Path.Combine(reserved.LibraryRoot, "server"), "do not delete");
        before = Snapshot(Path.GetDirectoryName(reserved.DataRoot)!);
        Expect("HCSL-PATH", () => manager.Acquire(reserved));
        Check(before == Snapshot(Path.GetDirectoryName(reserved.DataRoot)!), "reserved file was changed");
    });
    if (OperatingSystem.IsLinux()) Run("XDG absolute overrides and relative-variable fallback", () =>
    {
        var oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "xdg-config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "xdg-data"));
            var defaults = LocalPaths.Defaults();
            Check(defaults.SettingsRoot == Path.Combine(root, "xdg-config", "hcsl"), "XDG config ignored");
            Check(defaults.DataRoot == Path.Combine(root, "xdg-data", "hcsl"), "XDG data ignored");
            Check(defaults.LibraryRoot == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HCSL-Library"), "large library follows XDG data");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "relative");
            Check(Path.IsPathFullyQualified(LocalPaths.Defaults().DataRoot), "relative XDG accepted");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldData);
        }
    });
    Run("saved workspace paths survive new defaults without rewriting or migration", () =>
    {
        var request = Request("legacy-layout");
        using (manager.Acquire(request)) { }
        var before = Snapshot(Path.GetDirectoryName(request.DataRoot)!);
        var resolved = LocalPaths.Defaults(request.SettingsRoot);
        Check(resolved == request, "saved data/library replaced by new defaults");
        Check(before == Snapshot(Path.GetDirectoryName(request.DataRoot)!), "default resolution wrote to workspace");
        using var lease = manager.Acquire(resolved);
        Check(lease.Layout.LibraryRoot == request.LibraryRoot, "legacy library changed");
    });
    Run("corrupt or missing saved identity never creates a replacement workspace", () =>
    {
        var request = Request("corrupt-defaults");
        using (manager.Acquire(request)) { }
        var settings = Path.Combine(request.SettingsRoot, "settings.json");
        var original = File.ReadAllBytes(settings);
        File.WriteAllText(settings, "{broken");
        try { LocalPaths.Defaults(request.SettingsRoot); throw new Exception("corrupt settings accepted"); }
        catch (WorkspaceException ex) when (ex.Code == "HCSL-FORMAT") { }
        Check(File.ReadAllText(settings) == "{broken", "corrupt settings overwritten");
        File.WriteAllBytes(settings, original);
        File.Delete(Path.Combine(request.LibraryRoot, "library.json"));
        var before = Snapshot(Path.GetDirectoryName(request.DataRoot)!);
        try { LocalPaths.Defaults(request.SettingsRoot); throw new Exception("missing library identity accepted"); }
        catch (WorkspaceException ex) when (ex.Code == "HCSL-MIGRATION") { }
        Check(before == Snapshot(Path.GetDirectoryName(request.DataRoot)!), "missing identity recreated");
    });
    Run("settings-only CLI reopens its saved custom data and library", () =>
    {
        var request = Request("settings-only");
        using (manager.Acquire(request)) { }
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(WorkspaceCheckEntry).Assembly.Location);
        info.ArgumentList.Add("--settings"); info.ArgumentList.Add(request.SettingsRoot);
        var child = Process.Start(info)!; workers.Add(child);
        var output = child.StandardOutput.ReadToEnd();
        WaitExit(child, 0);
        using var json = JsonDocument.Parse(output);
        Check(json.RootElement.GetProperty("Layout").GetProperty("LibraryRoot").GetString() == request.LibraryRoot, "CLI lost saved library");
    });
    Console.WriteLine($"PASS: {passed} integration scenarios on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    foreach (var process in workers)
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); }
        process.Dispose();
    }
    Directory.Delete(root, recursive: true);
}

WorkspaceRequest Request(string name)
{
    var path = Path.Combine(root, name);
    return new(Path.Combine(path, "settings"), Path.Combine(path, "data"), Path.Combine(path, "library"));
}
void Run(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine("PASS: " + name);
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Expect(string code, Func<IWorkspaceLease> action)
{
    try { using var unexpected = action(); }
    catch (WorkspaceException ex) when (ex.Code == code) { return; }
    throw new Exception("Expected " + code);
}
static string Snapshot(string path) => !Directory.Exists(path) ? "missing" : string.Join("\n",
    Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != ".hcsl-management.lock").Order()
        .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
Process Start(WorkspaceRequest request, bool hold = false)
{
    var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        RedirectStandardInput = true
    };
    info.ArgumentList.Add(typeof(WorkspaceCheckEntry).Assembly.Location);
    foreach (var value in new[] { "--settings", request.SettingsRoot, "--data", request.DataRoot, "--library", request.LibraryRoot })
        info.ArgumentList.Add(value);
    if (hold) info.ArgumentList.Add("--hold");
    var process = Process.Start(info)!;
    workers.Add(process);
    return process;
}
static void Ready(Process process)
{
    var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    if (line is null || JsonDocument.Parse(line).RootElement.GetProperty("Status").GetString() != "Acquired")
        throw new Exception("Worker failed to acquire: " + process.StandardError.ReadToEnd());
}
static void WaitExit(Process process, int expected)
{
    if (!process.WaitForExit(15000))
    {
        process.Kill(true);
        process.WaitForExit();
        throw new Exception("Worker timed out");
    }
    Check(process.ExitCode == expected, $"Expected exit {expected}, got {process.ExitCode}: " + process.StandardError.ReadToEnd());
}
void CheckExit(WorkspaceRequest request, int expected)
{
    var process = Start(request);
    WaitExit(process, expected);
}
