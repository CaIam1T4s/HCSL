using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Steam;

var root = Path.Combine(Path.GetTempPath(), "hcsl-steam-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var oldStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(root, "user-state"));
var solution = new DirectoryInfo(AppContext.BaseDirectory);
while (solution is not null && !File.Exists(Path.Combine(solution.FullName, "Hello_Counter_Strike_Launcher.slnx"))) solution = solution.Parent;
if (solution is null) throw new Exception("Run integration tests from a built source checkout.");
var configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
var fakeDirectory = Path.Combine(solution.FullName, "build", "HCSL.FakeSteamCmd", "bin", configuration, "net10.0");
var fakeName = "HCSL.FakeSteamCmd" + (OperatingSystem.IsWindows() ? ".exe" : "");
var cli = typeof(SteamCmdCheckEntry).Assembly.Location;
var service = new SteamCmdService(cli, baselineRefresh: (_, _, _) => { });
var processes = new List<Process>();
var groups = new List<int>();
var count = 0;
try
{
    await Run("command echo cannot mark a successful operation as failed", () =>
    {
        const string echo = "\x1b[0m\"@ShutdownOnFailedCommand\" = \"1\"\r\n";
        for (int split = 0; split <= echo.Length; split++)
        {
            var parser = Parser();
            parser.Accept(echo.AsSpan(0, split), false);
            parser.Accept(echo.AsSpan(split), false);
            parser.Finish(false);
            Check(!parser.Failed && !parser.Authenticated, "config echo changed result at split " + split);
        }
        return Task.CompletedTask;
    });
    await Run("real ANSI login success survives every read boundary", () =>
    {
        const string real = "\x1b[0m\"@ShutdownOnFailedCommand\" = \"1\"\r\n" +
            "Connecting anonymously to Steam Public...\x1b[0mOK\r\n" +
            "\x1b[0mWaiting for user info...\x1b[0mOK\r\n";
        for (int split = 0; split <= real.Length; split++)
        {
            var parser = Parser();
            parser.Accept(real.AsSpan(0, split), false);
            parser.Accept(real.AsSpan(split), false);
            parser.Finish(false);
            Check(parser.Authenticated && !parser.Failed, "real output misclassified at split " + split);
        }
        var singleCharacters = Parser();
        foreach (char value in real) singleCharacters.Accept(new[] { value }, false);
        singleCharacters.Finish(false);
        Check(singleCharacters.Authenticated && !singleCharacters.Failed, "character-sized ANSI chunks failed");
        return Task.CompletedTask;
    });
    await Run("explicit login failures remain failures after ANSI normalization", () =>
    {
        foreach (var message in new[] { "ERROR! Login FAILED (No Connection)", "Connecting anonymously...FAILED (No Connection)",
                     "Login Failure: Invalid Password", "FAILED" })
        {
            var parser = Parser();
            foreach (char value in "\x1b[31m" + message + "\x1b[0m\r\n") parser.Accept(new[] { value }, false);
            parser.Finish(false);
            Check(parser.Failed, "explicit failure was ignored: " + message);
        }
        var warning = Parser();
        warning.Accept("Warning: failed to load optional data\nWaiting for user info...OK\n", false);
        Check(warning.Authenticated && !warning.Failed, "nonfatal warning became a login failure");
        return Task.CompletedTask;
    });
    await Run("stdout/stderr escape state is independent and real stderr errors are retained", () =>
    {
        var parser = Parser();
        parser.Accept("Waiting for user info...\x1b[", false);
        parser.Accept("\x1b[33mWarning: failed to load optional data\x1b[0m\n", true);
        parser.Accept("0mOK\n", false);
        Check(parser.Authenticated && !parser.Failed, "stderr corrupted stdout ANSI decoding");
        parser.Accept("\x1b[31mERROR! Login FAILED (No Connection)\x1b[0m\n", true);
        Check(parser.Failed, "real stderr failure was lost");
        return Task.CompletedTask;
    });
    await Run("terminal title/control payloads do not become login results", () =>
    {
        var parser = Parser();
        const string text = "\x1b]0;ERROR! Login FAILED (title)\a\x1bPfailed: control data\x1b\\" +
            "Waiting for user info...\x1b[0mOK\r\n";
        foreach (char value in text) parser.Accept(new[] { value }, false);
        parser.Finish(false);
        Check(parser.Authenticated && !parser.Failed, "terminal control payload affected result");
        return Task.CompletedTask;
    });
    await Run("anonymous self-update/login and clean exit", async () =>
    {
        var fixture = Fixture("anonymous");
        var events = new Events();
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), events);
        Check(result.Stage == SteamCmdStage.Succeeded, result.Code);
        foreach (var stage in new[] { SteamCmdStage.SelfUpdating, SteamCmdStage.Ready, SteamCmdStage.Authenticated, SteamCmdStage.Succeeded })
            Check(events.Values.Any(e => e.Stage == stage), "missing " + stage);
        Check(!File.Exists(SteamCmdJournal.PathFor(Path.GetDirectoryName(fixture.Executable)!)), "journal not cleared");
    });
    await Run("split no-newline password/Guard prompts and no sensitive output", async () =>
    {
        var fixture = Fixture("account", "account");
        var events = new Events();
        var input = new Input();
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable, AccountName: "test_account"), events, input);
        Check(result.Stage == SteamCmdStage.Succeeded, result.Code);
        Check(input.Challenges.Count == 2 && input.Challenges.Select(c => c.ChallengeId).Distinct().Count() == 2, "challenge duplication");
        Check(input.Challenges[0].Kind == SteamCmdChallengeKind.Password && input.Challenges[1].Kind == SteamCmdChallengeKind.GuardCode, "challenge ordering");
        var serialized = JsonSerializer.Serialize(events.Values);
        Check(!serialized.Contains("test-password-secret") && !serialized.Contains("12345-secret"), "secret leaked into events");
        Check(events.Values.Any(e => e.Stage == SteamCmdStage.WaitingForMobileApproval), "mobile approval not detected");
    });
    await Run("interactive prompt without provider stops safely", async () =>
    {
        var fixture = Fixture("no-input", "account");
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable));
        Check(result.Code == "HCSL-STEAMCMD-INTERACTION", result.Code);
        Check(!File.Exists(SteamCmdJournal.PathFor(Path.GetDirectoryName(fixture.Executable)!)), "failed input left a journal");
    });
    await Run("newline injection in secret input rejected", async () =>
    {
        var fixture = Fixture("invalid-input", "account");
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), interaction: new Input("bad\nquit"));
        Check(result.Code == "HCSL-STEAMCMD-INTERACTION", result.Code);
    });
    await Run("login failure/nonzero exit/unknown success are distinct from success", async () =>
    {
        foreach (var scenario in new[] { "failed", "exit-failure", "unconfirmed" })
        {
            var fixture = Fixture(scenario, scenario);
            var events = new Events();
            var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), events);
            Check(result.Stage == SteamCmdStage.Failed, scenario + " incorrectly succeeded");
            Check(!JsonSerializer.Serialize(events.Values).Contains("private-token-should-not-leak"), "failure output leaked");
        }
    });
    await Run("large unterminated output is bounded", async () =>
    {
        var fixture = Fixture("flood", "flood");
        var events = new Events();
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), events);
        Check(result.Stage == SteamCmdStage.Succeeded && events.Values.Count < 20, result.Code);
    });
    await Run("unknown prompt times out without inventing a challenge", async () =>
    {
        var fixture = Fixture("unknown", "unknown-prompt");
        var input = new Input();
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable, Timeout: TimeSpan.FromSeconds(1)), interaction: input);
        Check(result.Stage == SteamCmdStage.TimedOut && input.Challenges.Count == 0, result.Code);
    });
    await Run("timeout stops child tree and allows retry", async () =>
    {
        var fixture = Fixture("timeout", "hang");
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable, Timeout: TimeSpan.FromSeconds(1)));
        Check(result.Stage == SteamCmdStage.TimedOut, result.Code);
        CheckChildStopped(fixture.Executable);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "scenario.txt"), "success");
        Check((await service.ProbeAsync(new(fixture.Workspace, fixture.Executable))).Stage == SteamCmdStage.Succeeded, "retry failed");
    });
    await Run("external tool directory is locked across independent workspaces; cancellation drains children", async () =>
    {
        var fixture = Fixture("cancel", "hang");
        using var cancel = new CancellationTokenSource();
        var task = service.ProbeAsync(new(fixture.Workspace, fixture.Executable), cancellationToken: cancel.Token);
        try
        {
            await WaitForFile(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "child.pid"));
            var other = Workspace("other-owner");
            await ExpectAsync("HCSL-STEAMCMD-BUSY", () => service.ProbeAsync(new(other, fixture.Executable)));
            var differentTool = Fixture("different-tool");
            await ExpectAsync("HCSL-STEAMCMD-BUSY", () => service.ProbeAsync(new(differentTool.Workspace, differentTool.Executable)));
        }
        finally { cancel.Cancel(); }
        var result = await task;
        Check(result.Stage == SteamCmdStage.Cancelled, result.Code);
        CheckChildStopped(fixture.Executable);
    });
    await Run("children remaining after normal launcher exit are stopped", async () =>
    {
        var fixture = Fixture("child-exit", "child-exit");
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable));
        Check(result.Stage == SteamCmdStage.Succeeded, result.Code);
        CheckChildStopped(fixture.Executable);
    });
    await Run("official bootstrap download/extraction through injected HTTP handler", async () =>
    {
        var package = CreatePackage();
        var handler = new PackageHandler(package);
        using var client = new HttpClient(handler);
        var installer = new SteamCmdService(cli, client);
        var workspace = Workspace("bootstrap");
        var result = await installer.ProbeAsync(new(workspace, InstallIfMissing: true));
        Check(result.Stage == SteamCmdStage.Succeeded, result.Code);
        Check(handler.Requests == 1, "unexpected requests");
        Check(File.Exists(Path.Combine(workspace.DataRoot, "steamcmd", "tool", OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh")), "launcher not installed");
    });
    await Run("missing tool and bootstrap HTTP failure reported without launching", async () =>
    {
        Check((await service.ProbeAsync(new(Workspace("missing")))).Code == "HCSL-STEAMCMD-MISSING", "missing tool not classified");
        using var client = new HttpClient(new PackageHandler([], fail: true));
        var result = await new SteamCmdService(cli, client).ProbeAsync(new(Workspace("download-failure"), InstallIfMissing: true));
        Check(result.Code == "HCSL-STEAMCMD-DOWNLOAD", result.Code);
    });
    if (OperatingSystem.IsLinux())
    {
        await Run("archive traversal and symbolic links rejected", async () =>
        {
            foreach (var link in new[] { false, true })
            {
                var archive = Path.Combine(root, link ? "link.tar.gz" : "escape.tar.gz");
                await using (var file = File.Create(archive))
                await using (var gzip = new GZipStream(file, CompressionMode.Compress))
                using (var writer = new TarWriter(gzip))
                {
                    var entry = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, link ? "link" : "../escape");
                    if (link) entry.LinkName = "/tmp";
                    else entry.DataStream = new MemoryStream([1]);
                    writer.WriteEntry(entry);
                }
                var destination = Path.Combine(root, "extract-" + link);
                Directory.CreateDirectory(destination);
                await ExpectAsync("HCSL-STEAMCMD-ARCHIVE", () => SteamCmdTool.ExtractAsync(archive, destination, default));
                Check(!File.Exists(Path.Combine(root, "escape")), "archive escaped");
            }
        });
    }
    await Run("observer and input-provider failures cannot bypass process cleanup", async () =>
    {
        var fixture = Fixture("observer");
        Check((await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), new ThrowingEvents())).Stage == SteamCmdStage.Succeeded,
            "observer interrupted ownership");
        fixture = Fixture("throwing-input", "account");
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), interaction: new ThrowingInput());
        Check(result.Stage == SteamCmdStage.Failed, result.Code);
        Check(!File.Exists(SteamCmdJournal.PathFor(Path.GetDirectoryName(fixture.Executable)!)), "provider failure left live ownership");
    });
    await Run("authenticator wording and cancellation of an unresponsive input provider", async () =>
    {
        var fixture = Fixture("authenticator", "account-long");
        Check((await service.ProbeAsync(new(fixture.Workspace, fixture.Executable), interaction: new Input())).Stage == SteamCmdStage.Succeeded,
            "authenticator app prompt was not recognized");
        fixture = Fixture("ignored-input-cancel", "account");
        var result = await service.ProbeAsync(new(fixture.Workspace, fixture.Executable, Timeout: TimeSpan.FromSeconds(1)), interaction: new UnresponsiveInput());
        Check(result.Stage == SteamCmdStage.TimedOut, result.Code);
        Check(!File.Exists(SteamCmdJournal.PathFor(Path.GetDirectoryName(fixture.Executable)!)), "unresponsive input retained journal");
    });
    await Run("incomplete, corrupt and foreign-namespace journals fail closed", async () =>
    {
        var fixture = Fixture("invalid-journal");
        var directory = Path.GetDirectoryName(fixture.Executable)!;
        var path = SteamCmdJournal.PathFor(directory);
        File.WriteAllText(path + ".tmp", "partial");
        await ExpectAsync("HCSL-STEAMCMD-RECOVERY", () => service.ProbeAsync(new(fixture.Workspace, fixture.Executable)));
        Expect("HCSL-STEAMCMD-RECOVERY", () => service.Recover(fixture.Workspace, fixture.Executable));
        Check(File.ReadAllText(path + ".tmp") == "partial", "partial journal removed");
        File.Delete(path + ".tmp");
        File.WriteAllText(path, "{");
        Expect("HCSL-STEAMCMD-RECOVERY", () => service.Recover(fixture.Workspace, fixture.Executable));
        Check(File.ReadAllText(path) == "{", "corrupt journal removed");
        if (OperatingSystem.IsLinux())
        {
            File.Delete(path);
            (SteamCmdJournal.New() with { PidNamespace = "different namespace" }).Save(directory);
            Expect("HCSL-STEAMCMD-RECOVERY", () => service.Recover(fixture.Workspace, fixture.Executable));
            Check(File.Exists(path), "foreign journal removed");
        }
    });
    await Run("host crash fails closed and explicit recovery refuses active children", async () =>
    {
        var fixture = Fixture("crash", "hang");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var item in new[] { "--settings", fixture.Workspace.SettingsRoot, "--data", fixture.Workspace.DataRoot,
                     "--library", fixture.Workspace.LibraryRoot, "--executable", fixture.Executable }) start.ArgumentList.Add(item);
        var host = Process.Start(start)!;
        processes.Add(host);
        var directory = Path.GetDirectoryName(fixture.Executable)!;
        await WaitForFile(Path.Combine(directory, "child.pid"));
        using var journal = JsonDocument.Parse(File.ReadAllText(SteamCmdJournal.PathFor(directory)));
        int? group = OperatingSystem.IsLinux() ? journal.RootElement.GetProperty("ProcessGroup").GetInt32() : null;
        if (group is { } pg) groups.Add(pg);
        host.Kill(); // Deliberately kill only the owner, not its child process tree.
        Check(host.WaitForExit(10000), "host crash timeout");
        await ExpectAsync("HCSL-STEAMCMD-RECOVERY", () => service.ProbeAsync(new(fixture.Workspace, fixture.Executable)));
        var otherTool = Fixture("crash-other-tool");
        await ExpectAsync("HCSL-STEAMCMD-RECOVERY", () => service.ProbeAsync(new(otherTool.Workspace, otherTool.Executable)));
        if (group is { } live)
        {
            Expect("HCSL-STEAMCMD-BUSY", () => service.Recover(fixture.Workspace, fixture.Executable));
            SteamCmdJournal.SignalGroup(live, 9);
            for (int i = 0; i < 200 && SteamCmdJournal.GroupExists(live); i++) await Task.Delay(50);
            Check(!SteamCmdJournal.GroupExists(live), "orphan group still active");
        }
        else await Task.Delay(500); // Windows Job Object kills on the owner's handle closure.
        service.Recover(fixture.Workspace, fixture.Executable);
        CheckChildStopped(fixture.Executable);
        File.WriteAllText(Path.Combine(directory, "scenario.txt"), "success");
        Check((await service.ProbeAsync(new(fixture.Workspace, fixture.Executable))).Stage == SteamCmdStage.Succeeded, "recovered retry failed");
    });
    await Run("CS2 install output and progress survive arbitrary stream boundaries", () =>
    {
        const string text = "\x1b[0m Update state [0x61] downloading, progress: 12.50 (125 / 1000)\r\n" +
            "Update state (0x5) validating, progress: 75.00 (750 / 1000)\nSuccess! App '730' fully installed.\n";
        for (int split = 0; split <= text.Length; split++)
        {
            var values = new List<(SteamCmdStage Stage, double? Percent)>();
            var parser = new SteamCmdOutput(Guid.NewGuid(), (_, _) => { }, Channel.CreateBounded<SteamCmdChallenge>(4).Writer,
                (stage, _, percent) => values.Add((stage, percent)));
            parser.Accept(text.AsSpan(0, split), false); parser.Accept(text.AsSpan(split), false); parser.Finish(false);
            Check(parser.AppInstalled && !parser.Failed, "installation success lost at split " + split);
            Check(values.Contains((SteamCmdStage.DownloadingContent, 12.5)) && values.Contains((SteamCmdStage.ValidatingContent, 75)), "progress lost");
            Check(values.All(v => v.Percent is null or 12.5 or 75), "partial numeric token became a percentage");
        }
        var wrong = Parser(); wrong.Accept("Success! App '740' fully installed.\n", false);
        Check(!wrong.AppInstalled, "wrong application accepted");
        return Task.CompletedTask;
    });
    await Run("CS2 installation uses fixed commands and persists verified ready state", async () =>
    {
        var fixture = Fixture("install path with spaces");
        var events = new Events();
        var result = await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)), events);
        Check(result.Code == "HCSL-CS2-OK" && result.ExitCode == 0, result.Code);
        var metadata = Path.Combine(fixture.Workspace.LibraryRoot, ".hcsl", "cs2-install.json");
        using var state = JsonDocument.Parse(File.ReadAllText(metadata));
        Check(state.RootElement.GetProperty("Status").GetString() == "Ready" && state.RootElement.GetProperty("BuildId").GetString() == "123456", "ready state missing");
        var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "install-args.json")))!;
        Check(args.SequenceEqual(new[] { "+@ShutdownOnFailedCommand", "1", "+force_install_dir", Path.Combine(fixture.Workspace.LibraryRoot, "server"), "+login", "anonymous", "+app_update", "730", "-beta", "public", "+quit" }), "unexpected commands");
        Check(events.Values.Any(e => e.Stage == SteamCmdStage.DownloadingContent && e.Percent == 12.5), "download progress missing");
        result = await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable), SteamCmdInstallMode.Validate), events);
        Check(result.Code == "HCSL-CS2-OK" && events.Values.Any(e => e.Stage == SteamCmdStage.ValidatingContent && e.Percent == 75), "validation failed");
        args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "install-args.json")))!;
        Check(args.Contains("validate"), "validation command missing");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "scenario.txt"), "install-up-to-date");
        Check((await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)))).Code == "HCSL-CS2-OK", "already up to date was not accepted");
    });
    await Run("login or exit alone cannot mark CS2 installed", async () =>
    {
        foreach (var scenario in new[] { "install-no-success", "install-wrong-app", "install-no-files", "install-fail", "install-exit-failure" })
        {
            var fixture = Fixture(scenario, scenario);
            var result = await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)));
            Check(result.Stage == SteamCmdStage.Failed, "false success: " + scenario);
            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Workspace.LibraryRoot, ".hcsl", "cs2-install.json")));
            Check(state.RootElement.GetProperty("Status").GetString() == "Incomplete", "failed installation became ready");
        }
    });
    await Run("cancelled installation retains partial data and retries without deleting it", async () =>
    {
        var fixture = Fixture("install-cancel", "install-hang");
        using var cancel = new CancellationTokenSource();
        var task = service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)), cancellationToken: cancel.Token);
        await WaitForFile(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "install.pid"));
        var retained = Path.Combine(fixture.Workspace.LibraryRoot, "server", "user-retained.test");
        File.WriteAllText(retained, "do not delete");
        cancel.Cancel();
        Check((await task).Stage == SteamCmdStage.Cancelled, "installation not cancelled");
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Workspace.LibraryRoot, ".hcsl", "cs2-install.json"))))
            Check(state.RootElement.GetProperty("Status").GetString() == "Incomplete", "cancelled installation became ready");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "scenario.txt"), "success");
        Check((await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)))).Code == "HCSL-CS2-OK", "retry failed");
        Check(File.ReadAllText(retained) == "do not delete", "retry deleted partial data");
    });
    await Run("installation timeout is independent of login timeout", async () =>
    {
        var fixture = Fixture("install-timeout", "install-hang");
        var result = await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable, Timeout: TimeSpan.FromSeconds(1))));
        Check(result.Stage == SteamCmdStage.TimedOut, "download timeout not enforced");
        await ExpectAsync("HCSL-STEAMCMD-TIMEOUT", () => service.ProbeAsync(new(fixture.Workspace, fixture.Executable, Timeout: TimeSpan.FromHours(2))));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Executable)!, "scenario.txt"), "success");
        Check((await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable, Timeout: TimeSpan.FromHours(2))))).Stage == SteamCmdStage.Succeeded, "long installation timeout rejected");
    });
    await Run("unmanaged content and damaged installation metadata are preserved", async () =>
    {
        var fixture = Fixture("install-unmanaged");
        using (var lease = new HCSL.Infrastructure.Storage.LocalWorkspaceManager().Acquire(fixture.Workspace))
            File.WriteAllText(Path.Combine(lease.Layout.ServerRoot, "keep.txt"), "user content");
        Check((await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)))).Code == "HCSL-CS2-UNMANAGED", "unmanaged content taken over");
        Check(File.ReadAllText(Path.Combine(fixture.Workspace.LibraryRoot, "server", "keep.txt")) == "user content", "unmanaged data changed");
        var metadata = Path.Combine(fixture.Workspace.LibraryRoot, ".hcsl", "cs2-install.json");
        File.WriteAllText(metadata, "broken");
        Check((await service.InstallAsync(new(new(fixture.Workspace, fixture.Executable)))).Code == "HCSL-CS2-STATE", "damaged metadata accepted");
        Check(File.ReadAllText(metadata) == "broken", "damaged metadata overwritten");
    });
    Console.WriteLine($"PASS: {count} SteamCMD integration scenarios ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    foreach (var process in processes)
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); }
        process.Dispose();
    }
    if (OperatingSystem.IsLinux()) foreach (var group in groups)
        if (SteamCmdJournal.GroupExists(group)) SteamCmdJournal.SignalGroup(group, 9);
    Directory.Delete(root, recursive: true);
    if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("XDG_STATE_HOME", oldStateHome);
}

WorkspaceRequest Workspace(string name)
{
    var parent = Path.Combine(root, name);
    return new(Path.Combine(parent, "config"), Path.Combine(parent, "data"), Path.Combine(parent, "library"));
}
(WorkspaceRequest Workspace, string Executable) Fixture(string name, string scenario = "success")
{
    var directory = Path.Combine(root, name + "-tool");
    Directory.CreateDirectory(directory);
    foreach (var file in Directory.EnumerateFiles(fakeDirectory))
    {
        var target = Path.Combine(directory, Path.GetFileName(file));
        File.Copy(file, target);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
    }
    File.WriteAllText(Path.Combine(directory, "scenario.txt"), scenario);
    return (Workspace(name), Path.Combine(directory, fakeName));
}
byte[] CreatePackage()
{
    using var buffer = new MemoryStream();
    var name = OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh";
    if (OperatingSystem.IsWindows())
    {
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Directory.EnumerateFiles(fakeDirectory))
                zip.CreateEntryFromFile(file, Path.GetFileName(file) == fakeName ? name : Path.GetFileName(file));
    }
    else
    {
        using var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true);
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(fakeDirectory))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(file) == fakeName ? name : Path.GetFileName(file))
                { Mode = File.GetUnixFileMode(file) };
                using var content = File.OpenRead(file);
                entry.DataStream = content;
                writer.WriteEntry(entry);
            }
        }
        gzip.Close();
    }
    return buffer.ToArray();
}
async Task Run(string name, Func<Task> test)
{
    await test();
    count++;
    Console.WriteLine("PASS: " + name);
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task ExpectAsync(string code, Func<Task> action)
{
    try { await action(); }
    catch (SteamCmdException ex) when (ex.Code == code) { return; }
    throw new Exception("Expected " + code);
}
static void Expect(string code, Action action)
{
    try { action(); }
    catch (SteamCmdException ex) when (ex.Code == code) { return; }
    throw new Exception("Expected " + code);
}
static async Task WaitForFile(string path)
{
    for (int i = 0; i < 200; i++)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0) return;
        await Task.Delay(50);
    }
    throw new Exception("Process fixture did not start: " + path);
}
static void CheckChildStopped(string executable)
{
    var path = Path.Combine(Path.GetDirectoryName(executable)!, "child.pid");
    Check(File.Exists(path), "child never started");
    var pid = int.Parse(File.ReadAllText(path));
    if (OperatingSystem.IsLinux())
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            Check(stat[(stat.LastIndexOf(')') + 2)] is 'Z' or 'X', "child remains live");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    else
    {
        try { using var process = Process.GetProcessById(pid); Check(process.HasExited, "child remains live"); }
        catch (ArgumentException) { }
    }
}
static SteamCmdOutput Parser() => new(Guid.NewGuid(), (_, _) => { }, Channel.CreateBounded<SteamCmdChallenge>(4).Writer);
sealed class Events : IProgress<SteamCmdEvent>
{
    private readonly object sync = new();
    internal List<SteamCmdEvent> Values { get; } = [];
    public void Report(SteamCmdEvent value) { lock (sync) Values.Add(value); }
}
sealed class Input(string? fixedValue = null) : ISteamCmdInteraction
{
    internal List<SteamCmdChallenge> Challenges { get; } = [];
    public ValueTask<string> RespondAsync(SteamCmdChallenge challenge, CancellationToken cancellationToken)
    {
        Challenges.Add(challenge);
        return ValueTask.FromResult(fixedValue ?? (challenge.Kind == SteamCmdChallengeKind.Password ? "test-password-secret" : "12345-secret"));
    }
}
sealed class PackageHandler(byte[] package, bool fail = false) : HttpMessageHandler
{
    internal int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (fail) throw new HttpRequestException("simulated unavailable network");
        if (request.RequestUri?.Host != "steamcdn-a.akamaihd.net") throw new Exception("untrusted download endpoint");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
    }
}

sealed class ThrowingEvents : IProgress<SteamCmdEvent>
{
    public void Report(SteamCmdEvent value) => throw new Exception("observer failed");
}
sealed class ThrowingInput : ISteamCmdInteraction
{
    public ValueTask<string> RespondAsync(SteamCmdChallenge challenge, CancellationToken cancellationToken)
        => throw new Exception("provider failed");
}
sealed class UnresponsiveInput : ISteamCmdInteraction
{
    public ValueTask<string> RespondAsync(SteamCmdChallenge challenge, CancellationToken cancellationToken)
        => new(new TaskCompletionSource<string>().Task);
}
