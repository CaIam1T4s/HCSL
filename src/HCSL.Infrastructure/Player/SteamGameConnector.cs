using System.Diagnostics;
using HCSL.Application.Abstractions;
using HCSL.Core;

namespace HCSL.Infrastructure.Player;

/// <summary>Requests Steam launch/forwarding. Does not claim the game actually joined.</summary>
public sealed class SteamGameConnector : IGameConnector
{
    public static Uri ConnectUri(PlayerServer server, string password)
    {
        server = PlayerServerRules.Normalize(server);
        PlayerServerRules.Password(password);
        // Verified on Windows with CS2 both stopped and running. rungameid is not
        // interchangeable with run/730 or the Steam server-information connect action.
        if (password.Length == 0)
            return new Uri("steam://rungameid/730//+connect%20" + server.Endpoint);
        // Password launch arguments have not been verified. Preserve the existing
        // dedicated-connect fallback rather than guessing a console argument format.
        return new Uri("steam://connect/" + server.Endpoint + "/" + Uri.EscapeDataString(password));
    }
    public async Task RequestConnectAsync(PlayerServer server, string password, CancellationToken ct = default)
    {
        await DispatchAsync(ConnectUri(server, password), ct);
    }
    internal static async Task DispatchAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // No shell command concatenation; dispatch only the steam protocol.
            if (OperatingSystem.IsLinux())
            {
                var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(uri.AbsoluteUri);
                using var process = Process.Start(start) ?? throw new IOException();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    // Kill only the dispatcher, never the Steam/game process tree.
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new IOException();
                }
                if (process.ExitCode != 0) throw new IOException();
            }
            else if (OperatingSystem.IsWindows())
            { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }) ?? throw new IOException(); }
            else throw new IOException();
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        { throw new IOException("HCSL-CLIENT-LAUNCH: Could not dispatch Steam. Install Steam or use the manual connect command."); }
    }
}
