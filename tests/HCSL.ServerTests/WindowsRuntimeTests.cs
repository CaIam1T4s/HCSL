using System.Diagnostics;
using HCSL.Infrastructure.Server;

namespace HCSL.ServerTests;

internal static class WindowsRuntimeTests
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Windows Job Object test: Windows required");
            return;
        }

        using var job = WindowsServerJob.Create();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Could not start Windows test child.");
        try
        {
            WindowsServerJob.Attach(job, process);
            if (process.HasExited) throw new Exception("Test child exited before the job was closed.");
            job.Dispose();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Console.WriteLine("PASS windows-runtime.job-close-terminates-process");
    }
}
