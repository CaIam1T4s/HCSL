using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Steam;

/// <summary>Installation state is separate from Steam's resumable content. Never delete partial downloads.</summary>
internal sealed class Cs2Installation(WorkspaceLayout layout, Action<WorkspaceLayout, string, Guid>? refreshBaseline = null)
{
    private sealed record State(int FormatVersion, int AppId, string Status, string? BuildId = null);
    private string Metadata => Path.Combine(layout.LibraryRoot, ".hcsl", "cs2-install.json");
    private bool begun, completed;

    internal void Begin()
    {
        if (layout.ServerRoot.Any(char.IsControl) || layout.ServerRoot.Contains('"'))
            throw new SteamCmdException("HCSL-CS2-PATH", "Installation path contains unsupported characters.");
        LocalPaths.RejectLink(layout.ServerRoot);
        LocalPaths.RejectLink(Metadata);
        if (File.Exists(Metadata))
        {
            State? state;
            try { state = JsonSerializer.Deserialize<State>(File.ReadAllText(Metadata)); }
            catch (JsonException) { throw new SteamCmdException("HCSL-CS2-STATE", "Installation metadata needs inspection."); }
            if (state is not { FormatVersion: 1, AppId: 730 } || state.Status is not ("Installing" or "Incomplete" or "Ready"))
                throw new SteamCmdException("HCSL-CS2-STATE", "Unknown installation metadata; nothing was overwritten.");
        }
        else if (Directory.EnumerateFileSystemEntries(layout.ServerRoot).Any())
            throw new SteamCmdException("HCSL-CS2-UNMANAGED", "The nonempty server directory is not an HCSL installation; import is not implemented.");
        Write(new(1, 730, "Installing"));
        begun = true;
    }

    /// <summary>Marks the install Ready, then tries to refresh the local baseline.</summary>
    /// <returns>A warning when the baseline could not be written; the installation itself is still Ready.</returns>
    internal string? Complete(Guid operationId, Action? checkingBaseline = null)
    {
        var binary = OperatingSystem.IsWindows() ? "game/bin/win64/cs2.exe" : "game/bin/linuxsteamrt64/cs2";
        RequireFile(binary);
        var info = File.ReadAllText(RequireFile("game/csgo/steam.inf"));
        var manifest = File.ReadAllText(RequireFile("steamapps/appmanifest_730.acf"));
        var build = Regex.Match(manifest, "\"buildid\"\\s*\"([0-9]+)\"", RegexOptions.IgnoreCase);
        if (!Regex.IsMatch(info, @"(?im)^\s*appid\s*=\s*730\s*$") ||
            !Regex.IsMatch(manifest, "\"appid\"\\s*\"730\"", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(manifest, "\"StateFlags\"\\s*\"4\"", RegexOptions.IgnoreCase) ||
            !build.Success || !long.TryParse(build.Groups[1].Value, out var buildId) || buildId <= 0)
            throw new SteamCmdException("HCSL-CS2-FILES", "CS2 installation metadata is missing or incomplete.");
        Write(new(1, 730, "Ready", build.Groups[1].Value));
        completed = true;
        checkingBaseline?.Invoke();
        try { (refreshBaseline ?? ((l, b, o) => RuntimeDeploymentService.RefreshLocalBaseline(l, b, o)))(layout, build.Groups[1].Value, operationId); }
        catch (Exception e) when (e is DeploymentException or IOException or UnauthorizedAccessException)
        { return "HCSL-CS2-BASELINE-PENDING: CS2 is Ready but the startup baseline was not refreshed; run 'hcsl baseline refresh --yes' before deploying (" + e.Message + ")"; }
        return null;
    }

    private string RequireFile(string relative)
    {
        var path = layout.ServerRoot;
        foreach (var part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            LocalPaths.RejectLink(path);
        }
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > (relative.EndsWith("cs2") || relative.EndsWith(".exe") ? long.MaxValue : 1024 * 1024))
            throw new SteamCmdException("HCSL-CS2-FILES", "Required CS2 files are missing or invalid.");
        return path;
    }

    internal void Finish()
    {
        if (!begun || completed) return;
        // If this write itself fails, Installing remains a conservative non-ready state.
        try { Write(new(1, 730, "Incomplete")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceException) { }
    }

    private void Write(State value)
    {
        LocalPaths.RejectLink(Metadata);
        var temporary = Metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = LocalPaths.CreateFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Metadata, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
