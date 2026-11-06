using System.Diagnostics;
using HCSL.Infrastructure.Steam;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Deployment;

/// <summary>Offline only. Every operation holds the same cross-process leases as SteamCMD and instance editing.</summary>
public sealed class RuntimeDeploymentService : IRuntimeDeploymentService
{
    internal sealed record InventoryFile(string Path, long Bytes, string Sha256);
    internal sealed record Baseline(string BuildId, string EntrypointSha256, InventoryFile[] Inventory, string[] Configs);
    private sealed record InstallState(int FormatVersion, int AppId, string Status, string? BuildId);
    internal sealed record Transaction(int FormatVersion, Guid OperationId, Guid InstanceId, Guid DataId, Guid LibraryId);
    private readonly Baseline baseline;
    private readonly Action<string>? checkpoint;
    public RuntimeDeploymentService() : this(ReadBaseline()) { }
    internal RuntimeDeploymentService(Baseline baseline, Action<string>? checkpoint = null) { this.baseline = baseline; this.checkpoint = checkpoint; }
    private static Baseline ReadBaseline()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HCSL.LinuxStartupBaseline")!;
        using var json = JsonDocument.Parse(stream); var root = json.RootElement;
        return new(root.GetProperty("buildId").GetString()!, root.GetProperty("entrypointSha256").GetString()!,
            root.GetProperty("conservativeCopyInventory").EnumerateArray().Select(f => new InventoryFile(f.GetProperty("path").GetString()!, f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!)).ToArray(),
            root.GetProperty("independentConfigPaths").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }
    private static void Platform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DeploymentException("HCSL-DEPLOY-PLATFORM", "Only the verified Linux x64 offline deployment baseline is supported.");
    }
    private static string Id(Guid id) => id == Guid.Empty ? throw new DeploymentException("HCSL-DEPLOY-ID", "Instance ID is required.") : id.ToString("D");
    private static string Journal(WorkspaceLayout l, Guid id) => Path.Combine(l.LibraryRoot, ".hcsl", "deployment-" + Id(id) + ".json");
    private static string Target(WorkspaceLayout l, Guid id) => Path.Combine(l.RuntimeRoot, Id(id));
    private static string Stage(WorkspaceLayout l, Transaction t) => Path.Combine(l.RuntimeRoot, ".staging-" + Id(t.InstanceId) + "-" + t.OperationId.ToString("N"));
    private static string Previous(WorkspaceLayout l, Transaction t) => Path.Combine(l.RuntimeRoot, ".previous-" + Id(t.InstanceId) + "-" + t.OperationId.ToString("N"));
    private static string InRoot(string root, string relative)
    {
        var path = root;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new DeploymentException("HCSL-DEPLOY-PATH", "Invalid relative path.");
        foreach (var part in relative.Split('/'))
        {
            if (part is ".." or "." or "" || part.Contains('\\')) throw new DeploymentException("HCSL-DEPLOY-PATH", "Invalid path component.");
            path = Path.Combine(path, part); LocalPaths.RejectLink(path);
        }
        return path;
    }
    public DeploymentPlan Plan(WorkspaceRequest workspace, DeploymentOptions options, CancellationToken cancellationToken = default)
    {
        Platform(); using var lease = new LocalWorkspaceManager().Acquire(workspace);
        return Prepare(lease, options, cancellationToken);
    }
    internal DeploymentPlan Prepare(IWorkspaceLease lease, DeploymentOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.CopyMode)) throw new DeploymentException("HCSL-DEPLOY-MODE", "Unknown copy policy.");
        var l = lease.Layout; var id = options.InstanceId; Id(id);
        if (File.Exists(Journal(l, id))) throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "An interrupted deployment must be recovered first.");
        foreach (var directory in new[] { Path.Combine(l.SteamCmdRoot, "tool"), SteamCmdService.UserCoordinationDirectory() })
        {
            var steamJournal = SteamCmdJournal.PathFor(directory);
            LocalPaths.RejectLink(steamJournal); LocalPaths.RejectLink(steamJournal + ".tmp");
            if (File.Exists(steamJournal) || File.Exists(steamJournal + ".tmp"))
                throw new DeploymentException("HCSL-DEPLOY-STEAMCMD", "An unfinished SteamCMD session must be recovered before deployment.");
        }
        var entry = LocalInstanceStore.Load(InRoot(l.InstancesRoot, Id(id)), id);
        var d = entry.Definition!; var p = entry.Presets.Single(p => p.Id == d.SelectedPresetId);
        if (options.ExpectedRevision is { } revision && d.Revision != revision)
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "Instance changed since the plan was reviewed.");
        var instance = InRoot(l.InstancesRoot, Id(id)); var preset = InRoot(instance, "presets/" + Id(p.Id));
        var plugins = DeploymentFiles.Read<JsonElement>(InRoot(preset, "plugins.json"));
        var authorization = DeploymentFiles.Read<JsonElement>(InRoot(preset, "authorization.json"));
        if (!plugins.TryGetProperty("FormatVersion", out var pv) || pv.GetInt32() != 1 ||
            !plugins.TryGetProperty("Plugins", out var packages) || packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() != 0 ||
            !authorization.TryGetProperty("FormatVersion", out var av) || av.GetInt32() != 1 ||
            !authorization.TryGetProperty("Rules", out var rules) || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() != 0)
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin packages and access rules are not deployable yet; nonempty or unknown manifests are refused.");
        var state = DeploymentFiles.Read<InstallState>(InRoot(l.LibraryRoot, ".hcsl/cs2-install.json"));
        if (state is not { FormatVersion: 1, AppId: 730, Status: "Ready" } || state.BuildId != baseline.BuildId)
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Ready installation must match the verified deployment baseline. Update the baseline after a new game build.");
        var manifest = SmallText(InRoot(l.ServerRoot, "steamapps/appmanifest_730.acf"));
        if (!Regex.IsMatch(manifest, "\"appid\"\\s*\"730\"", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(manifest, "\"StateFlags\"\\s*\"4\"", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(manifest, "\"buildid\"\\s*\"" + Regex.Escape(state.BuildId!) + "\"", RegexOptions.IgnoreCase))
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Steam manifest and installation state disagree.");
        var info = SmallText(InRoot(l.ServerRoot, "game/csgo/steam.inf"));
        if (!Regex.IsMatch(info, @"(?im)^\s*appid\s*=\s*730\s*$")) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Invalid steam.inf.");
        var map = InRoot(l.ServerRoot, "game/csgo/maps/" + p.Map + ".vpk");
        if (!File.Exists(map) || new FileInfo(map).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-MAP", "Selected map is not present as a built-in map package; Workshop deployment is not implemented.");
        VerifyBaseline(l.ServerRoot, ct);
        var sdk = InRoot(l.SteamCmdRoot, "tool/linux64/steamclient.so");
        if (!File.Exists(sdk) || new FileInfo(sdk).Length == 0) throw new DeploymentException("HCSL-DEPLOY-SDK", "Managed SteamCMD linux64/steamclient.so is missing.");
        var mappings = new[]
        {
            new DeploymentMapping("game/csgo/cfg", InRoot(preset, "cfg")),
            new DeploymentMapping("game/csgo/addons", InRoot(preset, "addons")),
            new DeploymentMapping("game/csgo/logs", InRoot(instance, "logs")),
            new DeploymentMapping("game/csgo/recordings", InRoot(instance, "recordings")),
            new DeploymentMapping("userdata", InRoot(preset, "userdata")),
            new DeploymentMapping("home", InRoot(preset, "home"))
        };
        foreach (var m in mappings) if (Directory.Exists(m.PersistentPath)) DeploymentFiles.Tree(m.PersistentPath, _ => { }, ct);
        // SDK ancestors may not exist yet; check their existing components before any seeding.
        var existingSdk = InRoot(preset, "home/.steam/sdk64/steamclient.so");
        if (File.Exists(existingSdk) && new FileInfo(existingSdk).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-SDK", "Persistent Steam SDK file is empty; inspect it before deployment.");
        foreach (var reserved in new[] { "game/csgo/addons", "game/csgo/logs", "game/csgo/recordings" })
        {
            var source = InRoot(l.ServerRoot, reserved);
            if (File.Exists(source) || (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any()))
                throw new DeploymentException("HCSL-DEPLOY-MAPPING", "Base installation contains content reserved for an instance; import must be handled explicitly.");
        }
        int count = 0; long bytes = 0, seedBytes = new FileInfo(sdk).Length;
        DeploymentFiles.Tree(InRoot(l.ServerRoot, "game"), f =>
        {
            count++; bytes = checked(bytes + new FileInfo(f).Length);
            var relative = Path.GetRelativePath(l.ServerRoot, f).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("game/csgo/cfg/", StringComparison.Ordinal)) seedBytes = checked(seedBytes + new FileInfo(f).Length);
        }, ct);
        if (count == 0) throw new DeploymentException("HCSL-DEPLOY-FILES", "Empty game installation.");
        var minimum = checked(256L * 1024 * 1024 + count * 4096L + (options.CopyMode == DeploymentCopyMode.FullCopy ? bytes : 0));
        if (lease.Capacity.LibraryAvailableBytes < minimum || lease.Capacity.DataAvailableBytes < seedBytes + 64L * 1024 * 1024 ||
            (Path.GetPathRoot(l.LibraryRoot) == Path.GetPathRoot(l.DataRoot) && lease.Capacity.LibraryAvailableBytes < minimum + seedBytes + 64L * 1024 * 1024))
            throw new DeploymentException("HCSL-DEPLOY-SPACE", "Insufficient free space for the selected deployment policy and persistent seeds.");
        var target = Target(l, id); ValidateExisting(lease, id, target); EnsureStopped(target);
        return new(id, d.Revision, p.Id, p.Revision, state.BuildId!, target, options.CopyMode, count, bytes, minimum, seedBytes + 64L * 1024 * 1024, mappings,
            ["Offline deployment only; no server is launched.", "No resource links are approved; the entire game tree is independently cloned.",
             "Reflink support is tested during deployment, with no silent full-copy fallback.", "CoW writes can consume additional disk space later.",
             "Missing base CFG files are seeded once; existing persistent CFG files are never overwritten.", "Previous deployments and unknown runtime writes are retained, not deleted."]);
    }
    private static string SmallText(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new DeploymentException("HCSL-DEPLOY-STATE", "Installation metadata is too large.");
        return File.ReadAllText(path);
    }
    internal void VerifyBaseline(string server, CancellationToken ct)
    {
        VerifyHash(InRoot(server, "game/cs2.sh"), baseline.EntrypointSha256);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(InRoot(server, "game/cs2.sh")) & UnixFileMode.UserExecute) == 0)
            throw new DeploymentException("HCSL-DEPLOY-FILES", "cs2.sh is not executable.");
        foreach (var f in baseline.Inventory)
        {
            ct.ThrowIfCancellationRequested(); var path = InRoot(server, f.Path);
            if (new FileInfo(path).Length != f.Bytes) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Native file size differs from the verified baseline.");
            VerifyHash(path, f.Sha256);
        }
        foreach (var config in baseline.Configs) if (new FileInfo(InRoot(server, config)).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-FILES", "Required path configuration is missing.");
    }
    private static void VerifyHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "A native file or entrypoint differs from the verified baseline.");
    }
    private static void EnsureStopped(string target)
    {
        LocalPaths.RejectLink(target);
        if (File.Exists(Path.Combine(target, ".hcsl-running.json"))) throw new DeploymentException("HCSL-DEPLOY-RUNNING", "Runtime is marked as in use.");
        if (!Directory.Exists(target)) return;
        foreach (var process in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(process), out var pid) || pid == Environment.ProcessId) continue;
            foreach (var name in new[] { "cwd", "exe" })
            {
                try
                {
                    var link = File.ResolveLinkTarget(Path.Combine(process, name), true)?.FullName;
                    if (link != null && LocalPaths.Contains(target, link)) throw new DeploymentException("HCSL-DEPLOY-RUNNING", "A process is using this runtime; stop it before redeploying.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    internal static DeploymentReceipt? ValidateExisting(IWorkspaceLease lease, Guid id, string target)
    {
        LocalPaths.RejectLink(target);
        if (File.Exists(target)) throw new DeploymentException("HCSL-DEPLOY-UNMANAGED", "Runtime path is a file.");
        if (!Directory.Exists(target)) return null;
        var receiptFile = Path.Combine(target, "deployment.json");
        if (!File.Exists(receiptFile)) throw new DeploymentException("HCSL-DEPLOY-UNMANAGED", "Existing runtime has no deployment receipt; it was not modified.");
        var receipt = DeploymentFiles.Read<DeploymentReceipt>(receiptFile);
        if (receipt.FormatVersion != 1 || receipt.Status != "DeployedOffline" || receipt.InstanceId != id || receipt.DataId != lease.Identity.DataId || receipt.LibraryId != lease.Identity.LibraryId || receipt.OperationId == Guid.Empty)
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Runtime ownership or deployment format does not match.");
        if (receipt.PersistentMappings == null || receipt.PersistentMappings.Length != 6 || receipt.PersistentMappings.Select(m => m.RuntimePath).Distinct().Count() != 6)
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Incomplete persistent mapping metadata.");
        // Unknown files are retained in previous generations. Unknown links are not followed or silently accepted.
        var instance = InRoot(lease.Layout.InstancesRoot, Id(id));
        foreach (var mapping in receipt.PersistentMappings)
        {
            var allowed = mapping.RuntimePath switch
            {
                "game/csgo/cfg" => "presets/" + Id(receipt.PresetId) + "/cfg",
                "game/csgo/addons" => "presets/" + Id(receipt.PresetId) + "/addons",
                "game/csgo/logs" => "logs", "game/csgo/recordings" => "recordings",
                "userdata" => "presets/" + Id(receipt.PresetId) + "/userdata",
                "home" => "presets/" + Id(receipt.PresetId) + "/home",
                _ => throw new DeploymentException("HCSL-DEPLOY-PATH", "Unknown persistent mapping.")
            };
            if (!string.Equals(InRoot(instance, allowed), mapping.PersistentPath, LocalPaths.Comparison))
                throw new DeploymentException("HCSL-DEPLOY-PATH", "Persistent mapping is outside its instance.");
        }
        void Walk(string dir)
        {
            LocalPaths.RejectLink(dir);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var rel = Path.GetRelativePath(target, path).Replace(Path.DirectorySeparatorChar, '/');
                var m = receipt.PersistentMappings.SingleOrDefault(m => m.RuntimePath == rel);
                if (m != null)
                {
                    var link = new DirectoryInfo(path).LinkTarget;
                    if (link == null || !Path.IsPathFullyQualified(link) || !string.Equals(Path.GetFullPath(link), m.PersistentPath, LocalPaths.Comparison))
                        throw new DeploymentException("HCSL-DEPLOY-PATH", "Persistent runtime link was modified.");
                    continue;
                }
                LocalPaths.RejectLink(path); if (Directory.Exists(path)) Walk(path);
            }
        }
        Walk(target); return receipt;
    }
    public DeploymentReceipt Deploy(WorkspaceRequest workspace, DeploymentOptions options, IProgress<DeploymentEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        Platform(); using var lease = new LocalWorkspaceManager().Acquire(workspace);
        return Deploy(lease, options, progress, cancellationToken);
    }
    /// <summary>
    /// Deploys under a lease the caller already holds. Starting a server takes the lease for the whole
    /// session, so it must deploy through this overload: acquiring a second lease in the same process
    /// fails with HCSL-LOCK.
    /// </summary>
    internal DeploymentReceipt Deploy(IWorkspaceLease lease, DeploymentOptions options, IProgress<DeploymentEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        Platform();
        var plan = Prepare(lease, options, cancellationToken); var l = lease.Layout;
        var tx = new Transaction(1, Guid.NewGuid(), options.InstanceId, lease.Identity.DataId, lease.Identity.LibraryId);
        var journal = Journal(l, tx.InstanceId); var stage = Stage(l, tx); var previous = Previous(l, tx);
        // Once this is durable, all partial files and renames have a known recovery owner.
        DeploymentFiles.AtomicJson(journal, tx); checkpoint?.Invoke("Journaled");
        Directory.CreateDirectory(stage);
        progress?.Report(new("Cloning", 0, plan.FileCount)); int done = 0;
        var sourceGame = InRoot(l.ServerRoot, "game");
        DeploymentFiles.Tree(sourceGame, file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(l.ServerRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            DeploymentFiles.Copy(file, InRoot(stage, relative), options.CopyMode);
            done++; if (done % 64 == 0 || done == plan.FileCount) progress?.Report(new("Cloning", done, plan.FileCount));
        }, cancellationToken);
        // Preserve empty directories too, since engine and plugin search paths may depend on them.
        void EmptyDirs(string source)
        {
            foreach (var dir in Directory.EnumerateDirectories(source))
            { cancellationToken.ThrowIfCancellationRequested(); LocalPaths.RejectLink(dir); Directory.CreateDirectory(InRoot(stage, Path.GetRelativePath(l.ServerRoot, dir).Replace(Path.DirectorySeparatorChar, '/'))); EmptyDirs(dir); }
        }
        EmptyDirs(sourceGame); VerifyBaseline(stage, cancellationToken); checkpoint?.Invoke("Cloned");
        progress?.Report(new("PersistentData", done, plan.FileCount));
        foreach (var mapping in plan.PersistentMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = InRoot(stage, mapping.RuntimePath);
            Directory.CreateDirectory(mapping.PersistentPath);
            if (Directory.Exists(destination))
            {
                if (mapping.RuntimePath == "game/csgo/cfg") Seed(destination, mapping.PersistentPath, cancellationToken);
                else if (Directory.EnumerateFileSystemEntries(destination).Any())
                    throw new DeploymentException("HCSL-DEPLOY-MAPPING", "Base installation contains data in a directory reserved for an instance; no data was discarded.");
                Directory.Delete(destination, true); // This is exclusively our newly cloned staging tree, never an existing deployment.
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.CreateSymbolicLink(destination, mapping.PersistentPath);
        }
        var home = plan.PersistentMappings.Single(m => m.RuntimePath == "home").PersistentPath;
        var sdk = InRoot(home, ".steam/sdk64/steamclient.so");
        if (!File.Exists(sdk)) SeedFile(InRoot(l.SteamCmdRoot, "tool/linux64/steamclient.so"), sdk);
        var receipt = new DeploymentReceipt(1, tx.OperationId, "DeployedOffline", lease.Identity.DataId, lease.Identity.LibraryId,
            plan.InstanceId, plan.PresetId, plan.InstanceRevision, plan.PresetRevision, plan.BuildId, plan.CopyMode, DateTimeOffset.UtcNow, plan.PersistentMappings);
        DeploymentFiles.AtomicJson(Path.Combine(stage, "deployment.json"), receipt);
        ValidateExisting(lease, tx.InstanceId, stage); cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("Prepared");
        EnsureStopped(plan.RuntimePath); ValidateExisting(lease, tx.InstanceId, plan.RuntimePath);
        // Ignore cancellation inside the short publication window. Recovery handles process termination between renames.
        if (Directory.Exists(plan.RuntimePath)) Directory.Move(plan.RuntimePath, previous);
        checkpoint?.Invoke("PreviousMoved");
        Directory.Move(stage, plan.RuntimePath); checkpoint?.Invoke("Published");
        File.Delete(journal); progress?.Report(new("DeployedOffline", done, plan.FileCount)); return receipt;
    }
    private static void Seed(string source, string target, CancellationToken ct)
    {
        DeploymentFiles.Tree(source, file =>
        {
            ct.ThrowIfCancellationRequested(); var dest = InRoot(target, Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'));
            if (!File.Exists(dest)) SeedFile(file, dest);
        }, ct);
    }
    private static void SeedFile(string source, string destination)
    {
        LocalPaths.RejectLink(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".seed-" + Guid.NewGuid().ToString("N");
        try { DeploymentFiles.Copy(source, temp, DeploymentCopyMode.FullCopy); File.Move(temp, destination, false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public string Recover(WorkspaceRequest workspace, Guid instanceId)
    {
        Platform(); using var lease = new LocalWorkspaceManager().Acquire(workspace); var l = lease.Layout;
        var journal = Journal(l, instanceId); LocalPaths.RejectLink(journal);
        if (!File.Exists(journal)) return "NoPendingDeployment";
        var tx = DeploymentFiles.Read<Transaction>(journal);
        if (tx.FormatVersion != 1 || tx.OperationId == Guid.Empty || tx.InstanceId != instanceId || tx.DataId != lease.Identity.DataId || tx.LibraryId != lease.Identity.LibraryId)
            throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "Unknown deployment journal; nothing was changed.");
        var target = Target(l, instanceId); var previous = Previous(l, tx); var stage = Stage(l, tx);
        LocalPaths.RejectLink(stage); LocalPaths.RejectLink(previous); EnsureStopped(target);
        var current = ValidateExisting(lease, instanceId, target);
        if (current?.OperationId == tx.OperationId) { File.Delete(journal); return "PublishedDeploymentConfirmed"; }
        if (!Directory.Exists(target) && Directory.Exists(previous))
        {
            ValidateExisting(lease, instanceId, previous); EnsureStopped(previous); Directory.Move(previous, target);
        }
        else if (Directory.Exists(target) && Directory.Exists(previous))
            throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "Both previous and unexpected current deployments exist; inspect before continuing.");
        // Partial staging trees remain intact for diagnosis. They are never promoted or recursively deleted by recovery.
        File.Delete(journal); return "PreviousDeploymentPreserved_StagingRetained";
    }
}
