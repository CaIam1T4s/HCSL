using System.Diagnostics;

// Test-only process fixture. Never referenced by production services or selected by the real-tool CLI.
if (args is ["--sleep-child"])
{
    await Task.Delay(TimeSpan.FromMinutes(5));
    return 0;
}
var scenario = File.Exists("scenario.txt") ? File.ReadAllText("scenario.txt").Trim() : "success";
if (scenario == "silent") { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
Console.Write("Checking for available up");
Console.Out.Flush();
await Task.Delay(20);
Console.WriteLine("dates...");
Console.WriteLine("Steam Console Client (test fixture)");
Console.WriteLine("\x1b[0m\"@ShutdownOnFailedCommand\" = \"1\"");
Console.WriteLine("Connecting anonymously to Steam Public...OK");
Console.Out.Flush();
if (scenario is "hang" or "child-exit")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add("--sleep-child");
    using var child = Process.Start(start)!;
    File.WriteAllText("child.pid", child.Id.ToString());
    if (scenario == "hang") await Task.Delay(TimeSpan.FromMinutes(5));
}
if (scenario.StartsWith("account"))
{
    Console.Error.Write("Pass");
    Console.Error.Flush();
    await Task.Delay(30);
    Console.Error.Write("word:");
    Console.Error.Flush();
    var password = await Console.In.ReadLineAsync();
    Console.WriteLine("auth echo: " + password); // Deliberately hostile output to check that secrets cannot leak.
    Console.WriteLine("Confirm this login on your phone");
    Console.Write(scenario == "account-long" ? "Please enter the current code from your authenticator" : "Steam Guard co");
    Console.Out.Flush();
    await Task.Delay(30);
    Console.Write(scenario == "account-long" ? " app:" : "de:");
    Console.Out.Flush();
    var code = await Console.In.ReadLineAsync();
    Console.WriteLine("auth echo: " + code);
    if (password != "test-password-secret" || code != "12345-secret") return 9;
}
if (scenario == "unknown-prompt")
{
    Console.Write("Unknown future challenge> ");
    Console.Out.Flush();
    await Task.Delay(TimeSpan.FromMinutes(5));
}
if (scenario == "unconfirmed") return 0;
if (scenario == "failed") { Console.WriteLine("ERROR! Login FAILED (private-token-should-not-leak)"); return 0; }
if (scenario == "flood") Console.WriteLine(new string('x', 100000));
Console.WriteLine("Waiting for client config...OK");
Console.Write("Waiting for user info...\x1b[");
Console.Out.Flush();
await Task.Delay(20);
Console.WriteLine("0mOK");
Console.Out.Flush();
if (Array.IndexOf(args, "+app_update") is var appIndex && appIndex >= 0)
{
    File.WriteAllText("install-args.json", System.Text.Json.JsonSerializer.Serialize(args));
    int pathIndex = Array.IndexOf(args, "+force_install_dir");
    if (pathIndex < 0 || pathIndex > Array.IndexOf(args, "+login") || args[appIndex + 1] != "730") return 20;
    var destination = args[pathIndex + 1];
    Directory.CreateDirectory(destination);
    File.WriteAllText(Path.Combine(destination, "partial-download.test"), "retained");
    Console.Write("\x1b[0m Update state (0x61) downloading, progress: 12.");
    Console.Out.Flush();
    await Task.Delay(30);
    Console.WriteLine("50 (125 / 1000)");
    if (scenario == "install-hang")
    {
        File.WriteAllText("install.pid", Environment.ProcessId.ToString());
        await Task.Delay(TimeSpan.FromMinutes(5));
    }
    if (scenario == "install-fail") { Console.WriteLine("ERROR! App '730' state is 0x6 after update job."); return 0; }
    Console.WriteLine(args.Contains("validate")
        ? " Update state (0x5) validating, progress: 75.00 (750 / 1000)"
        : " Update state (0x81) committing, progress: 100.00 (1000 / 1000)");
    if (scenario != "install-no-files")
    {
        var binary = Path.Combine(destination, "game", "bin", OperatingSystem.IsWindows() ? "win64" : "linuxsteamrt64", OperatingSystem.IsWindows() ? "cs2.exe" : "cs2");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "test fixture; not an executable server");
        var info = Path.Combine(destination, "game", "csgo", "steam.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(info)!);
        File.WriteAllText(info, "appID=730\n");
        Directory.CreateDirectory(Path.Combine(destination, "steamapps"));
        File.WriteAllText(Path.Combine(destination, "steamapps", "appmanifest_730.acf"), "\"AppState\" { \"appid\" \"730\" \"StateFlags\" \"4\" \"buildid\" \"123456\" }");
    }
    if (scenario != "install-no-success") Console.WriteLine(scenario == "install-wrong-app"
        ? "Success! App '740' fully installed." : scenario == "install-up-to-date"
                ? "Success! App '730' already up to date." : "\x1b[0mSuccess! App '730' fully installed.");
}
return scenario is "exit-failure" or "install-exit-failure" ? 7 : 0;

public static class FakeSteamCmdEntry { }
