using System.Diagnostics;
using System.Text.Json;
namespace HCSL.Infrastructure.Server;

public sealed record ServerGateCommand(int OwnerPid, long OwnerStartTicks, string PidNamespace, string Executable, string[] Arguments, string WorkingDirectory, long MemoryMaxBytes);
/// <summary>Runs as the main process of a systemd service. Parent EOF/loss ends the gate, so systemd drains its cgroup.</summary>
public static class ServerProcessGate
{
    private static bool OwnerAlive(ServerGateCommand command)
    {
        try
        {
            using var owner = Process.GetProcessById(command.OwnerPid);
            var valid = !owner.HasExited && LinuxProcessIdentity.StartTicks(command.OwnerPid) == command.OwnerStartTicks && new FileInfo("/proc/self/ns/pid").LinkTarget == command.PidNamespace;
            if (!valid) Console.Error.WriteLine("HCSL_SERVER_GATE_OWNER_MISMATCH");
            return valid;
        }
        catch { return false; }
    }
    private static bool LimitsMatch(ServerGateCommand command)
    {
        try
        {
            var group = File.ReadLines("/proc/self/cgroup").Single(l => l.StartsWith("0::", StringComparison.Ordinal))[3..];
            if (!group.StartsWith('/') || group.Split('/').Any(p => p == "..")) return false;
            var path = "/sys/fs/cgroup" + group;
            return long.TryParse(File.ReadAllText(Path.Combine(path, "memory.max")).Trim(), out var max) && max == command.MemoryMaxBytes &&
                File.ReadAllText(Path.Combine(path, "memory.swap.max")).Trim() == "0" && File.ReadAllText(Path.Combine(path, "pids.max")).Trim() == "128";
        }
        catch { return false; }
    }
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return 125;
        Console.WriteLine("HCSL_SERVER_GATE_READY"); Console.Out.Flush();
        using var control = new StreamReader(Console.OpenStandardInput());
        var first = await control.ReadLineAsync();
        if (first == null || first.Length > 16384) { Console.Error.WriteLine("HCSL_SERVER_GATE_INPUT_MISSING"); return 125; }
        var command = JsonSerializer.Deserialize<ServerGateCommand>(first);
        if (command == null || !Path.IsPathFullyQualified(command.Executable) || !OwnerAlive(command)) return 125;
        if (!LimitsMatch(command)) { Console.Error.WriteLine("HCSL_SERVER_GATE_LIMITS_MISMATCH"); return 125; }
        static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
        var start = new ProcessStartInfo("/usr/bin/script") { WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--quiet", "--return", "--flush", "--command", "exec " + Quote(command.Executable) + " " + string.Join(" ", command.Arguments.Select(Quote)), "/dev/null" }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        Console.WriteLine("HCSL_SERVER_CHILD_STARTED"); Console.Out.Flush();
        var stdout = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var stderr = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        var exited = child.WaitForExitAsync(); var input = control.ReadLineAsync();
        while (!exited.IsCompleted)
        {
            var winner = await Task.WhenAny(exited, input, Task.Delay(500));
            if (winner == exited) break;
            if (winner == input || !OwnerAlive(command))
            {
                try { await child.StandardInput.WriteLineAsync("quit"); await child.StandardInput.FlushAsync(); } catch (IOException) { }
                try { await exited.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { return 124; }
                break;
            }
        }
        // A script descendant may retain output pipes after its immediate parent exits. Do not wait forever.
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        return child.HasExited ? child.ExitCode : 124;
    }
}
