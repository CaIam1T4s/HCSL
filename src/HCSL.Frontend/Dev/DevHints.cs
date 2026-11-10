namespace HCSL.Frontend.Dev;

/// <summary>Maps stable HCSL codes to the next concrete action. Keep one hint per code.</summary>
public static class DevHints
{
    public static string? For(string? code) => code switch
    {
        null or "" => null,
        "HCSL-DEPLOY-RECOVERY" => "An earlier deploy stopped before publication: run `hcsl recover`, then `hcsl deploy`.",
        "HCSL-DEPLOY-RUNNING" => "Stop the server first: `hcsl stop` (or stop it in the UI), then deploy.",
        "HCSL-DEPLOY-PLUGINS" => "Check plugin package paths and that the preset addons directory is writable: `hcsl doctor`.",
        "HCSL-DEPLOY-STALE" => "Redeploy: `hcsl deploy`.",
        "HCSL-DEPLOY-CONFLICT" => "The instance/preset changed while preparing: rerun the command.",
        "HCSL-DEPLOY-BASELINE" => "The shared CS2 installation does not match the verified build: update/reinstall it.",
        "HCSL-DEPLOY-MAP" => "The selected map (or the Workshop seed map) is missing from the shared installation.",
        "HCSL-DEPLOY-ROTATION" => "The map pool and the base gamemodes_server.txt disagree: fix the pool or merge the file.",
        "HCSL-DEPLOY-SPACE" => "Free disk space on the library/data roots (reflink still needs metadata space).",
        "HCSL-DEPLOY-UNMANAGED" => "The runtime directory was not created by HCSL: move it away before deploying.",
        "HCSL-DEPLOY-MAPPING" => "Share one of the mapped directories is not empty: inspect the preset before deploying.",
        "HCSL-DEPLOY-STEAMCMD" => "Finish or recover the SteamCMD session first.",
        "HCSL-DEPLOY-FILES" or "HCSL-DEPLOY-VERIFY" => "The shared installation is incomplete or changed: validate/reinstall it.",
        "HCSL-SERVER-STALE" => "The deployment is stale: `hcsl deploy` (or confirm redeploy-and-start in the UI).",
        "HCSL-SERVER-DEPLOYMENT" => "No current deployment: `hcsl deploy` first.",
        "HCSL-SERVER-BUSY" => "A server task is already running: `hcsl status`.",
        "HCSL-SERVER-RECOVERY" => "A server run is active or unrecovered: stop it with `hcsl stop`; if no run is active, recover the previous session in the GUI/TUI (or `HCSL.ServerCheck --recover --instance <UUID>`).",
        "HCSL-SERVER-MEMORY" or "HCSL-SERVER-LOW-MEMORY" => "Free memory, or pass --allow-low-memory to start.",
        "HCSL-CONSOLE-STATE" => "The server is not Starting/LoadingMap/Running. Start it (`hcsl start --wait`) and retry.",
        "HCSL-CONSOLE-STALE" => "The run changed: `hcsl status` and retry against the new RunId.",
        "HCSL-CONSOLE-INPUT" => "The command was rejected as empty/unsafe: use one single line.",
        "HCSL-CONSOLE-UNAVAILABLE" => "This daemon has no server runtime; start the server from the GUI/TUI.",
        "HCSL-LOCK" => "Another HCSL process holds the workspace lease: close it (GUI/TUI/SteamCMD) and retry.",
        "HCSL-PLUGIN-CONFLICT" => "The plugin manifest changed externally: reload the list and retry.",
        "HCSL-PLUGIN-PACKAGE" => "A selected package is missing/invalid: `hcsl plugins` and `hcsl doctor`.",
        "HCSL-PLUGIN-LIMIT" => "The selection or a package exceeds a documented limit (32 entries, 4096 files, 512 MiB).",
        "HCSL-INSTANCE-STALE" => "The instance changed: refresh the catalog (`hcsl status`) and retry.",
        "HCSL-IPC-VERSION" => "GUI/CLI and daemon versions differ: restart the daemon/session.",
        _ => null
    };

    public static string With(string message, string? code) =>
        For(code) is { } hint ? message + Environment.NewLine + "next: " + hint : message;
}
