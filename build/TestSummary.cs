using System.Diagnostics;

if (args is not ["full" or "windows"])
{
    Console.Error.WriteLine("Usage: dotnet run --file build/TestSummary.cs -- full|windows");
    return 2;
}

var root = Path.GetFullPath(Directory.GetCurrentDirectory());
var projects = Path.Combine(root, "tests");
var logs = Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp
    ? Directory.CreateDirectory(Path.Combine(runnerTemp, "hcsl-test-results-" + Guid.NewGuid().ToString("N")))
    : Directory.CreateTempSubdirectory("hcsl-test-results-");
Console.WriteLine($"Logs: {logs.FullName}");
var runs = new List<(string Project, string? Section)>();
if (args[0] == "windows")
{
    runs.Add(("HCSL.ServerTests", "windows-runtime"));
    runs.Add(("HCSL.ServerTests", "frameworks"));
}
else
{
    foreach (var project in Directory.EnumerateDirectories(projects).Order(StringComparer.Ordinal))
    {
        var name = Path.GetFileName(project);
        if (!File.Exists(Path.Combine(project, name + ".csproj"))) continue;
        // These lists mirror the default sections, excluding opt-in diagnostics.
        string[] sections = name switch
        {
            "HCSL.HostTests" => ["input", "background", "sidebar", "instances", "shadow", "theme", "logs", "roster", "maps",
                "forms", "presets", "admins", "overview", "pools", "plugins", "frameworks", "baseline", "errors", "cfg", "start"],
            "HCSL.ServerTests" => ["maps", "network", "instance", "cfg", "plugins", "players", "framework-catalog", "frameworks",
                "dev", "console", "controller", "daemon"],
            _ => []
        };
        if (sections.Length == 0) runs.Add((name, null));
        else runs.AddRange(sections.Select(section => (name, (string?)section)));
    }
}

if (runs.Count == 0) return 2;
var results = new List<(string Name, int Exit, TimeSpan Duration, string Log, bool Skipped)>();
foreach (var (project, section) in runs)
{
    var name = section is null ? project : $"{project}.{section}";
    var log = Path.Combine(logs.FullName, name + ".log");
    var clock = Stopwatch.StartNew();
    Console.WriteLine($"=== {name} ===");
    var exit = 1;
    try
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var arg in new[] { "run", "--project", Path.Combine(projects, project, project + ".csproj"), "--no-build", "--" })
            start.ArgumentList.Add(arg);
        if (section is not null) { start.ArgumentList.Add("--only"); start.ArgumentList.Add(section); }
        await using var file = File.CreateText(log);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start test process.");
        child.StandardInput.Close();
        var stdout = LogAsync(child.StandardOutput);
        var stderr = LogAsync(child.StandardError);
        await Task.WhenAll(stdout, stderr, child.WaitForExitAsync());
        exit = child.ExitCode;

        async Task LogAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (file) file.WriteLine(line);
            }
        }
    }
    catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"{name}: {e.Message}");
    }
    var skipped = exit == 0 && section == "windows-runtime" && !OperatingSystem.IsWindows();
    results.Add((name, exit, clock.Elapsed, log, skipped));
    if (exit != 0 && File.Exists(log))
        foreach (var line in File.ReadLines(log).TakeLast(12)) Console.Error.WriteLine(line);
}

Console.WriteLine("=== Summary ===");
foreach (var result in results)
    Console.WriteLine($"{(result.Skipped ? "SKIP" : result.Exit == 0 ? "PASS" : "FAIL")}  {result.Name}  exit={result.Exit}  {result.Duration.TotalSeconds:F1}s  {result.Log}");
var failures = results.Count(result => result.Exit != 0);
var skips = results.Count(result => result.Skipped);
Console.WriteLine($"{results.Count - failures - skips} passed, {failures} failed, {skips} skipped");
return failures == 0 ? 0 : 1;
