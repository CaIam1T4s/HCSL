using System.Diagnostics;
using System.Text.Json;

namespace HCSL.Infrastructure.Steam;

/// <summary>Windows-only blocked worker, released after its parent assigns the job object.</summary>
public static class SteamCmdProcessGate
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows()) return 125;
        Console.WriteLine("HCSL_GATE:" + Environment.ProcessId);
        Console.Out.Flush();
        var line = Console.ReadLine();
        if (line is null || line.Length > 16384) return 125;
        var command = JsonSerializer.Deserialize(line, SteamCmdJsonContext.Default.SteamCmdGateCommand);
        if (command is null || !Path.IsPathFullyQualified(command.Executable)) return 125;
        var start = new ProcessStartInfo(command.Executable) { UseShellExecute = false };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        child.WaitForExit();
        return child.ExitCode;
    }
}
