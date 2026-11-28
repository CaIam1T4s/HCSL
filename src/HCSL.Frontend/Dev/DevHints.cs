namespace HCSL.Frontend.Dev;

/// <summary>Maps stable HCSL codes to the next concrete action. Keep one hint per code.</summary>
public static class DevHints
{
    public static string? For(string? code) => code switch
    {
        null or "" => null,
        "HCSL-DEPLOY-RECOVERY" => "An earlier deploy stopped before publication: run `hcsl deploy --recover` (or `hcsl recover`, then deploy).",
        "HCSL-DEPLOY-RUNNING" => "Stop the server first: `hcsl stop` (or stop it in the UI), then deploy.",
        "HCSL-DEPLOY-PLUGINS" => "Check plugin package paths and that the preset addons directory is writable: `hcsl doctor`.",
        "HCSL-DEPLOY-STALE" => "Redeploy: `hcsl deploy`.",
        "HCSL-DEPLOY-CONFLICT" => "The instance/preset changed while preparing: rerun the command.",
        "HCSL-DEPLOY-INSTALL-STATE" => "Validate the CS2 server files before deploying to restore a valid installation record.",
        "HCSL-DEPLOY-BASELINE" => "The shared CS2 installation changed after an update: run `hcsl baseline refresh --yes` to re-verify and record the local baseline, then deploy again.",
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
        "HCSL-PLUGIN-IN-USE" => "A preset still references this package version: remove it from those presets first.",
        "HCSL-PLUGIN-LIMIT" => "The selection or a package exceeds a documented limit (32 entries, 4096 files, 512 MiB).",
        "HCSL-INSTANCE-STALE" => "The instance changed: refresh the catalog (`hcsl status`) and retry.",
        "HCSL-FRAMEWORK-NET" => "Network/API failure: check connectivity/proxy, or import the release manually with `hcsl import`.",
        "HCSL-FRAMEWORK-RELEASE" or "HCSL-FRAMEWORK-ASSET" => "No matching official asset for this platform; download it manually and use `hcsl import`.",
        "HCSL-FRAMEWORK-HASH" => "The release has no usable SHA-256 digest or the download changed; do not install it automatically.",
        "HCSL-FRAMEWORK-BUSY" => "Stop the server/SteamCMD first: `hcsl stop`.",
        "HCSL-PLAYERS-STATE" => "The server must be Starting/LoadingMap/Running: `hcsl start --wait`.",
        "HCSL-PLAYERS-AUTH" => "RCON rejected the per-run password: redeploy/restart so this run re-applies it.",
        "HCSL-PLAYERS-TRANSPORT" => "No RCON endpoint accepted the connection: check the firewall/port and that the server is still running.",
        "HCSL-PLAYERS-PARSE" or "HCSL-PLAYERS-TIMEOUT" => "The server replied with an unexpected format: read `hcsl logs` and report the build.",
        "HCSL-KICK-STATE" => "The run changed or the server is not running: `hcsl status`, then retry.",
        "HCSL-KICK-TARGET" => "That userid/SteamID is not connected any more: `hcsl players` and retry.",
        "HCSL-KICK-REASON" => "Use one line of at most 128 visible characters.",
        "HCSL-FRAMEWORK-ID" => "Only the built-in catalog can be installed: `hcsl frameworks`.",
        "HCSL-PLUGIN-FRAMEWORK-CONFLICT" => "Enable only one scripting framework per preset (CounterStrikeSharp or Source2Mod, not both).",
        "HCSL-PLUGIN-FRAMEWORK-MISSING" => "Also enable the framework package and metamod in the same preset: `hcsl plugins`.",
        "HCSL-IPC-VERSION" => "GUI/CLI and daemon versions differ: restart the daemon/session.",
        _ => null
    };

    public static string With(string message, string? code) =>
        For(code) is { } hint ? message + Environment.NewLine + "next: " + hint : message;
}
