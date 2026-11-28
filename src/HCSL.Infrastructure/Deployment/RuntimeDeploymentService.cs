using System.Diagnostics;
using HCSL.Infrastructure.Steam;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
using HCSL.Infrastructure.Server;
namespace HCSL.Infrastructure.Deployment;

/// <summary>Offline only. Every operation holds the same cross-process leases as SteamCMD and instance editing.</summary>
public sealed class RuntimeDeploymentService : IRuntimeDeploymentService
{
    internal sealed record InventoryFile(string Path, long Bytes, string Sha256);
    internal sealed record Baseline(string BuildId, string EntrypointSha256, InventoryFile[] Inventory, string[] Configs);
    private sealed record InstallState(int FormatVersion, int AppId, string Status, string? BuildId);
    private sealed record LocalBaselineDocument(int FormatVersion, int AppId, string BuildId, string Source, string OperationId,
        DateTimeOffset GeneratedAt, string EntrypointSha256, InventoryFile[] ConservativeCopyInventory, string[] IndependentConfigPaths);
    internal sealed record Transaction(int FormatVersion, Guid OperationId, Guid InstanceId, Guid DataId, Guid LibraryId);
    private static readonly JsonSerializerOptions LocalBaselineJson = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly Baseline baseline;
    private readonly Action<string>? checkpoint;
    private readonly bool useLocalBaseline;
    public RuntimeDeploymentService() : this(ReadBaseline(), null, true) { }
    internal RuntimeDeploymentService(Baseline baseline, Action<string>? checkpoint = null) : this(baseline, checkpoint, false) { }
    private RuntimeDeploymentService(Baseline baseline, Action<string>? checkpoint, bool useLocalBaseline)
    { this.baseline = baseline; this.checkpoint = checkpoint; this.useLocalBaseline = useLocalBaseline; }
    private static Baseline ReadBaseline()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HCSL.LinuxStartupBaseline")!;
        using var json = JsonDocument.Parse(stream); var root = json.RootElement;
        return new(root.GetProperty("buildId").GetString()!, root.GetProperty("entrypointSha256").GetString()!,
            root.GetProperty("conservativeCopyInventory").EnumerateArray().Select(f => new InventoryFile(f.GetProperty("path").GetString()!, f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!)).ToArray(),
            root.GetProperty("independentConfigPaths").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    private const string WindowsEntrypoint = "game/bin/win64/cs2.exe";

    private static string LocalBaselinePath(WorkspaceLayout layout) => Path.Combine(layout.LibraryRoot, ".hcsl",
        OperatingSystem.IsWindows() ? "windows-startup-baseline.json" : "linux-startup-baseline.json");

    private static Baseline? ReadLocalBaseline(WorkspaceLayout layout, string buildId)
    {
        var path = LocalBaselinePath(layout);
        LocalPaths.RejectLink(path);
        if (!File.Exists(path)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<LocalBaselineDocument>(File.ReadAllText(path), LocalBaselineJson);
            if (document is not { FormatVersion: 1, AppId: 730, Source: "ManagedSteamCmd" }
                || string.IsNullOrWhiteSpace(document.BuildId) || document.BuildId != buildId
                || string.IsNullOrWhiteSpace(document.OperationId)
                || document.ConservativeCopyInventory is not { Length: > 0 }
                || document.IndependentConfigPaths is not { Length: > 0 })
                return null;
            return new(document.BuildId, document.EntrypointSha256, document.ConservativeCopyInventory, document.IndependentConfigPaths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    /// <summary>Refreshes the library-local baseline after a successful managed SteamCMD update.</summary>
    internal static void RefreshLocalBaseline(WorkspaceLayout layout, string buildId, Guid operationId, Baseline? template = null)
    {
        var windows = OperatingSystem.IsWindows();
        if (!windows) Platform();
        else if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DeploymentException("HCSL-DEPLOY-PLATFORM", "The Windows startup baseline requires an x64 process.");
        template ??= windows ? new Baseline(buildId, "", [], ["game/csgo/gameinfo.gi", "game/csgo/steam.inf"]) : ReadBaseline();
        // The recorded build must be the build Steam actually delivered: never bind a baseline to an
        // edited install state or a different manifest. This runs before any hashing so nothing is written.
        var manifestPath = InRoot(layout.ServerRoot, "steamapps/appmanifest_730.acf");
        LocalPaths.RejectLink(manifestPath);
        if (!File.Exists(manifestPath)) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "The Steam manifest is missing; the installation is not managed by HCSL.");
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "The Steam manifest is too large to trust.");
        var manifest = File.ReadAllText(manifestPath);
        if (!Regex.IsMatch(manifest, "\"appid\"\\s*\"730\"", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(manifest, "\"StateFlags\"\\s*\"4\"", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(manifest, "\"buildid\"\\s*\"" + Regex.Escape(buildId) + "\"", RegexOptions.IgnoreCase))
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Steam manifest does not match the installation state; the baseline was not written.");
        var steamInfoPath = InRoot(layout.ServerRoot, "game/csgo/steam.inf");
        LocalPaths.RejectLink(steamInfoPath);
        if (!File.Exists(steamInfoPath) || !Regex.IsMatch(File.ReadAllText(steamInfoPath), @"(?im)^\s*appid\s*=\s*730\s*$"))
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "game/csgo/steam.inf is missing or does not declare App 730.");
        var inventory = new List<InventoryFile>();
        foreach (var item in template.Inventory)
        {
            var path = InRoot(layout.ServerRoot, item.Path);
            LocalPaths.RejectLink(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null)
                throw new DeploymentException("HCSL-DEPLOY-BASELINE", "A baseline native file is missing or linked: " + item.Path);
            inventory.Add(new(item.Path, info.Length, HashFile(path)));
        }
        var entrypointPath = windows ? WindowsEntrypoint : "game/cs2.sh";
        var entrypoint = InRoot(layout.ServerRoot, entrypointPath);
        LocalPaths.RejectLink(entrypoint);
        if (!File.Exists(entrypoint) || new FileInfo(entrypoint).Length == 0) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "The baseline entrypoint is missing.");
        foreach (var config in template.Configs)
        {
            var path = InRoot(layout.ServerRoot, config);
            LocalPaths.RejectLink(path);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new DeploymentException("HCSL-DEPLOY-BASELINE", "A baseline config is missing or empty: " + config);
        }
        var entrypointHash = HashFile(entrypoint);
        if (windows) inventory.Add(new(entrypointPath, new FileInfo(entrypoint).Length, entrypointHash));
        var document = new LocalBaselineDocument(1, 730, buildId, "ManagedSteamCmd", operationId.ToString("N"),
            DateTimeOffset.UtcNow, entrypointHash, [.. inventory], template.Configs);
        var destination = LocalBaselinePath(layout);
        LocalPaths.RejectLink(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, LocalBaselineJson);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal Baseline ActiveBaseline(WorkspaceLayout layout, string buildId) =>
        useLocalBaseline ? ReadLocalBaseline(layout, buildId) ?? (OperatingSystem.IsWindows()
            ? throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Refresh the Windows startup baseline before deploying.")
            : baseline) : baseline;

    /// <summary>Read-only report: which baseline deployment would use for the installed build.</summary>
    public static (string Source, string BuildId, string LocalBuildId) BaselineStatus(WorkspaceLayout layout, string installedBuildId)
    {
        var local = ReadLocalBaseline(layout, installedBuildId);
        var source = local is not null ? "Local" : !OperatingSystem.IsWindows() && ReadBaseline().BuildId == installedBuildId ? "Embedded" : "None";
        return (source, installedBuildId, local?.BuildId ?? "");
    }

    /// <summary>Verifies the installed files and writes the library-local baseline (CLI/maintenance entry point).</summary>
    public static void RebuildLocalBaseline(WorkspaceLayout layout, string buildId, Guid operationId) =>
        RefreshLocalBaseline(layout, buildId, operationId);
    private static void Platform()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DeploymentException("HCSL-DEPLOY-PLATFORM", "Offline deployment requires Linux or Windows x64.");
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
    internal DeploymentPlan Prepare(IWorkspaceLease lease, DeploymentOptions options, CancellationToken ct) =>
        PrepareCore(lease, options, ct, allowInterruptedValidation: false);

    internal DeploymentPlan PrepareForStart(IWorkspaceLease lease, DeploymentOptions options, bool autoDeploy, CancellationToken ct) =>
        PrepareCore(lease, options, ct, allowInterruptedValidation: !autoDeploy);

    private DeploymentPlan PrepareCore(IWorkspaceLease lease, DeploymentOptions options, CancellationToken ct, bool allowInterruptedValidation)
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
        if (d.LayoutVersion != InstanceRules.CurrentLayoutVersion)
            throw new DeploymentException("HCSL-INSTANCE-LAYOUT", "Migrate this instance to the instance-level home/userdata layout before deploying.");
        if (options.ExpectedRevision is { } revision && d.Revision != revision)
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "Instance changed since the plan was reviewed.");
        var instance = InRoot(l.InstancesRoot, Id(id)); var preset = InRoot(instance, "presets/" + Id(p.Id));
        PresetMapPool mapPool;
        try { mapPool = LocalPresetMapPoolStore.ReadForDeployment(InRoot(preset, "maps.json")); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
        string? collection;
        ServerMapGroupPlan? rotation;
        try
        {
            collection = MapPoolRules.CollectionId(mapPool);
            rotation = collection is null ? ServerMapGroupRules.Create(p.Id, p.Map, mapPool) : null;
        }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
        var pluginSet = PluginSet(preset, l.LibraryRoot);
        var framework = FrameworkOf(pluginSet.Packages);
        var authorization = DeploymentFiles.Read<JsonElement>(InRoot(preset, "authorization.json"));
        if (!authorization.TryGetProperty("FormatVersion", out var av) || av.GetInt32() != 1 ||
            !authorization.TryGetProperty("Rules", out var rules) || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() != 0)
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Access rules are not deployable yet; nonempty or unknown manifests are refused.");
        var state = DeploymentFiles.Read<InstallState>(InRoot(l.LibraryRoot, ".hcsl/cs2-install.json"));
        var target = Target(l, id);
        DeploymentReceipt? retained = null;
        if (state is { FormatVersion: 1, AppId: 730, Status: "ValidationInterrupted" } && !string.IsNullOrWhiteSpace(state.BuildId))
        {
            if (allowInterruptedValidation) retained = ValidateExisting(lease, id, target);
            if (retained is null)
                throw new DeploymentException("HCSL-DEPLOY-VALIDATION-INTERRUPTED", "Complete server file validation before deploying or redeploying.");
            if (retained.BuildId != state.BuildId)
                throw new DeploymentException("HCSL-DEPLOY-VALIDATION-INTERRUPTED", "The existing deployment does not match the last verified build.");
        }
        else if (state is not { FormatVersion: 1, AppId: 730, Status: "Ready" } || string.IsNullOrWhiteSpace(state.BuildId))
            throw new DeploymentException("HCSL-DEPLOY-INSTALL-STATE", "Ready installation must have a valid BuildId before deployment.");
        if (pluginSet.Packages.Count(package => package.FrameworkPayload &&
                FrameworkRules.Find(package.Framework)?.Kind == FrameworkKind.ScriptFramework) > 1 && state.BuildId != "26879581")
            throw new DeploymentException("HCSL-DEPLOY-FRAMEWORK-BUILD", "多框架组合仅在 CS2 构建 26879581 上通过验证。");
        var activeBaseline = ActiveBaseline(l, state.BuildId!);
        if (state.BuildId != activeBaseline.BuildId)
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Ready installation must match the verified deployment baseline. Update the baseline after a new game build.");
        if (retained is null)
        {
            var manifest = SmallText(InRoot(l.ServerRoot, "steamapps/appmanifest_730.acf"));
            if (!Regex.IsMatch(manifest, "\"appid\"\\s*\"730\"", RegexOptions.IgnoreCase) ||
                !Regex.IsMatch(manifest, "\"StateFlags\"\\s*\"4\"", RegexOptions.IgnoreCase) ||
                !Regex.IsMatch(manifest, "\"buildid\"\\s*\"" + Regex.Escape(state.BuildId!) + "\"", RegexOptions.IgnoreCase))
                throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Steam manifest and installation state disagree.");
        }
        var contentRoot = retained is null ? l.ServerRoot : target;
        var info = SmallText(InRoot(contentRoot, "game/csgo/steam.inf"));
        if (!Regex.IsMatch(info, @"(?im)^\s*appid\s*=\s*730\s*$")) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Invalid steam.inf.");
        if (collection is null)
        {
            var map = InRoot(contentRoot, "game/csgo/maps/" + p.Map + ".vpk");
            if (!File.Exists(map) || new FileInfo(map).Length == 0)
                throw new DeploymentException("HCSL-DEPLOY-MAP", "Selected map is not present as a built-in map package; Workshop deployment is not implemented.");
        }
        if (rotation is not null)
        {
            foreach (var pooledMap in mapPool.Maps)
            {
                var package = InRoot(contentRoot, "game/csgo/maps/" + pooledMap.Value + ".vpk");
                if (!File.Exists(package) || new FileInfo(package).Length == 0)
                    throw new DeploymentException("HCSL-DEPLOY-ROTATION", "Every standard map in the pool must be installed as a built-in map package.");
            }
            var existingRotation = InRoot(contentRoot, "game/csgo/" + ServerMapGroupRules.FileName);
            if (retained is null && File.Exists(existingRotation) && new FileInfo(existingRotation).Length != 0)
                throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The base installation already has a nonempty gamemodes_server.txt; merge it manually before enabling a managed map group.");
        }
        if (collection is not null)
        {
            var seed = InRoot(contentRoot, "game/csgo/maps/" + ServerMapRules.CollectionSeedMap + ".vpk");
            if (!File.Exists(seed) || new FileInfo(seed).Length == 0)
                throw new DeploymentException("HCSL-DEPLOY-MAP", "The built-in seed map required to start a Workshop collection is missing.");
        }
        VerifyBaseline(contentRoot, activeBaseline, ct);
        if (retained is not null)
        {
            EnsureStopped(target);
            var existingPlan = new DeploymentPlan(id, d.Revision, p.Id, p.Revision, retained.BuildId, target, retained.CopyMode,
                0, 0, 0, 0, retained.PersistentMappings, [], rotation?.Name, rotation?.Hash, collection,
                pluginSet.Hash, framework, FrameworkRules.ReadinessProfileFor(framework));
            if (ServerPreparation.IsStale(retained, existingPlan) || !RotationFileMatches(retained, target))
                throw new DeploymentException("HCSL-DEPLOY-VALIDATION-INTERRUPTED", "The existing deployment needs to be rebuilt after server file validation.");
            return existingPlan;
        }
        var windows = OperatingSystem.IsWindows();
        if (windows && options.CopyMode != DeploymentCopyMode.FullCopy)
            throw new DeploymentException("HCSL-DEPLOY-COPY", "Windows offline deployment requires full copy.");
        var sdk = windows ? null : InRoot(l.SteamCmdRoot, "tool/linux64/steamclient.so");
        if (sdk is not null && (!File.Exists(sdk) || new FileInfo(sdk).Length == 0)) throw new DeploymentException("HCSL-DEPLOY-SDK", "Managed SteamCMD linux64/steamclient.so is missing.");
        var mappings = new List<DeploymentMapping>
        {
            new("game/csgo/cfg", InRoot(preset, "cfg"), SeedDefaults: true),
            new("game/csgo/addons", InRoot(preset, "addons")),
            new("game/csgo/logs", InRoot(instance, "logs")),
            new("game/csgo/recordings", InRoot(instance, "recordings")),
            new("userdata", InRoot(instance, "userdata")),
            new("home", InRoot(instance, "home"))
        };
        foreach (var materialized in FrameworkRules.MaterializedRootsFor(framework))
        {
            var index = mappings.FindIndex(m => string.Equals(m.RuntimePath, "game/" + materialized, StringComparison.Ordinal));
            if (index < 0)
                throw new DeploymentException("HCSL-DEPLOY-PERSISTENT", "A framework materialized root has no base deployment mapping.");
            mappings[index] = mappings[index] with { Kind = DeploymentMappingKind.Materialized };
        }
        foreach (var persistent in FrameworkRules.PersistentPathsFor(framework))
        {
            var root = persistent.Scope == FrameworkPersistentScope.Preset ? preset : instance;
            var persistentTarget = InRoot(root, persistent.PersistentRelativePath);
            if (mappings.Any(m => Overlaps(m.RuntimePath, persistent.RuntimePath) && m.Kind != DeploymentMappingKind.Materialized))
                throw new DeploymentException("HCSL-DEPLOY-PERSISTENT", "A framework persistent path overlaps an already mapped runtime path.");
            mappings.Add(new("game/" + persistent.RuntimePath, persistentTarget, SeedDefaults: persistent.SeedPackageDefaults));
        }
        foreach (var m in mappings) if (Directory.Exists(m.PersistentPath)) DeploymentFiles.Tree(m.PersistentPath, _ => { }, ct);
        // SDK ancestors may not exist yet; check their existing components before any seeding.
        var existingSdk = windows ? null : InRoot(instance, "home/.steam/sdk64/steamclient.so");
        if (existingSdk is not null && File.Exists(existingSdk) && new FileInfo(existingSdk).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-SDK", "Persistent Steam SDK file is empty; inspect it before deployment.");
        foreach (var reserved in new[] { "game/csgo/addons", "game/csgo/logs", "game/csgo/recordings" })
        {
            var source = InRoot(l.ServerRoot, reserved);
            if (File.Exists(source) || (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any()))
                throw new DeploymentException("HCSL-DEPLOY-MAPPING", "Base installation contains content reserved for an instance; import must be handled explicitly.");
        }
        int count = 0; long bytes = 0, seedBytes = sdk is null ? 0 : new FileInfo(sdk).Length;
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
        ValidateExisting(lease, id, target, verifyRotation: false, allowLegacy: true); EnsureStopped(target);
        var notes = new List<string>
        {
            "Offline deployment only; no server is launched.", "No resource links are approved; the entire game tree is independently cloned.",
            "Reflink support is tested during deployment, with no silent full-copy fallback.", "CoW writes can consume additional disk space later.",
            "Missing base CFG files are seeded once; existing persistent CFG files are never overwritten.", "Previous deployments and unknown runtime writes are retained, not deleted."
        };
        if (rotation is not null) notes.Add($"A launcher-owned CS2 map group '{rotation.Name}' is generated; pool order does not prove the engine's actual next map.");
        else if (collection is not null) notes.Add($"Workshop collection #{collection} is hosted natively and supplies its own map order; the preset startup map is ignored.");
        else if (mapPool.Maps.Length != 0)
            notes.Add("Workshop or mixed map pools are not deployed as a native map group; use confirmed live map changes.");
        if (pluginSet.Packages.Length != 0)
            notes.Add($"{pluginSet.Packages.Length} plugin package(s) are deployed to preset addons or framework data; package-owned files are replaced on redeploy.");
        else notes.Add("No plugin packages are enabled.");
        return new(id, d.Revision, p.Id, p.Revision, state.BuildId!, target, options.CopyMode, count, bytes, minimum, seedBytes + 64L * 1024 * 1024, [.. mappings],
            [.. notes], rotation?.Name, rotation?.Hash, collection, pluginSet.Hash, framework, FrameworkRules.ReadinessProfileFor(framework));
    }
    private static ServerMapGroupPlan? Rotation(string presetPath, PresetDefinition preset)
    {
        try { return ServerMapGroupRules.Create(preset.Id, preset.Map, LocalPresetMapPoolStore.ReadForDeployment(InRoot(presetPath, "maps.json"))); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
    }
    private static string? Collection(string presetPath)
    {
        try { return MapPoolRules.CollectionId(LocalPresetMapPoolStore.ReadForDeployment(InRoot(presetPath, "maps.json"))); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
    }
    private static string? FrameworkOf(IEnumerable<LocalPluginStore.Package> packages) =>
        packages.Where(p => p.FrameworkPayload && FrameworkRules.Find(p.Framework)?.Kind == FrameworkKind.ScriptFramework)
            .Select(p => p.Framework).Distinct(StringComparer.Ordinal)
            // The allowed pairs have at most one framework requiring special persistent mappings.
            .OrderByDescending(id => FrameworkRules.PersistentPathsFor(id).Length)
            .ThenBy(id => id, StringComparer.Ordinal).FirstOrDefault();

    private static bool Overlaps(string left, string right) =>
        PluginRules.IsPathUnder(left, right) || PluginRules.IsPathUnder(right, left);

    private static (string? Hash, LocalPluginStore.Package[] Packages) PluginSet(string presetPath, string libraryRoot)
    {
        try
        {
            var manifest = LocalPluginStore.ReadManifestForDeployment(InRoot(presetPath, "plugins.json"));
            var packages = new List<LocalPluginStore.Package>();
            foreach (var plugin in manifest.Plugins.Where(p => p.Enabled))
                packages.Add(LocalPluginStore.LoadPackage(Path.Combine(libraryRoot, "plugin-packages"), plugin.Id, plugin.Version));
            FrameworkRules.ValidateSelections(packages.Select(p => new FrameworkRules.SelectedFramework(p.Id, p.Framework, p.FrameworkPayload, p.Version)));
            using var sha = SHA256.Create();
            foreach (var package in packages)
            {
                var line = Encoding.UTF8.GetBytes(package.Id + "\0" + package.Version + "\0" + package.Hash + "\n");
                sha.TransformBlock(line, 0, line.Length, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            return (packages.Count == 0 ? null : Convert.ToHexString(sha.Hash!), packages.ToArray());
        }
        catch (InstanceException e) when (e.Code.StartsWith("HCSL-PLUGIN-FRAMEWORK-", StringComparison.Ordinal))
        { throw new DeploymentException(e.Code, e.Message); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException) { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", e.Message); }
    }

    /// <summary>Loads the previous framework package even when it is now disabled, so built-in module directories can be distinguished.</summary>
    private static LocalPluginStore.Package[] FrameworkPackages(string presetPath, string libraryRoot, string? framework)
    {
        if (framework is null) return [];
        try
        {
            var manifest = LocalPluginStore.ReadManifestForDeployment(InRoot(presetPath, "plugins.json"));
            return manifest.Plugins.Where(p => string.Equals(p.Id, framework, StringComparison.Ordinal))
                .Select(p => LocalPluginStore.LoadPackage(Path.Combine(libraryRoot, "plugin-packages"), p.Id, p.Version)).ToArray();
        }
        catch (Exception e) when (e is InstanceException or WorkspaceException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
        { throw new DeploymentException("HCSL-DEPLOY-PERSISTENT", "The previous framework package cannot be read; its user modules were not moved."); }
    }

    private sealed record PluginLedger(int FormatVersion, PluginLedgerEntry[] Packages);
    private sealed record PluginLedgerEntry(string Id, string Version, PluginLedgerFile[] Files);
    private sealed record PluginLedgerFile(string Path, string Sha256);
    private static PluginLedger ReadPluginLedger(string path)
    {
        LocalPaths.RejectLink(path);
        if (!File.Exists(path)) return new(1, []);
        try
        {
            var ledger = DeploymentFiles.Read<PluginLedger>(path);
            if (ledger.FormatVersion != 1 || ledger.Packages is null) throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Unknown plugin install ledger.");
            return ledger;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin install ledger cannot be read; no plugin files were changed."); }
    }
    private static string AddonsPath(string addons, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Invalid plugin install path.");
        var path = addons;
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or "..") throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Invalid plugin install path.");
            path = Path.Combine(path, part); LocalPaths.RejectLink(path);
        }
        return path;
    }
    private static void CopyPluginFile(string source, string destination, string sha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && string.Equals(HashFile(destination), sha256, StringComparison.OrdinalIgnoreCase)) return;
        var temp = destination + ".plugin-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temp, false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(source));
            // Hash the copy, not the source: a package edited after validation must not reach the preset.
            if (!string.Equals(HashFile(temp), sha256, StringComparison.OrdinalIgnoreCase))
                throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "A plugin package file changed after it was validated; no persistent plugin file was replaced.");
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void RejectReservedPluginPath(string relative)
    {
        if (PluginRules.IsReservedDeploymentPath(relative))
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin packages may not write preset CFG, logs or recordings.");
        if (PluginRules.IsAddonsRoot(relative))
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "A plugin package file may not replace the addons directory.");
    }
    /// <summary>Probes whether the target filesystem supports reflink copies. Never writes into an existing deployment.</summary>
    public static DeploymentCopyMode ProbeCopyMode(string directory)
    {
        var source = Path.Combine(directory, ".hcsl-reflink-probe-" + Guid.NewGuid().ToString("N"));
        var copy = source + ".copy";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(source, "probe");
            DeploymentFiles.Copy(source, copy, DeploymentCopyMode.Reflink);
            return DeploymentCopyMode.Reflink;
        }
        catch (Exception e) when (e is DeploymentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return DeploymentCopyMode.FullCopy; }
        finally
        {
            try { if (File.Exists(source)) File.Delete(source); } catch (IOException) { }
            try { if (File.Exists(copy)) File.Delete(copy); } catch (IOException) { }
        }
    }
    private sealed record PluginAddonFile(string RelativePath, string SourcePath, string Sha256);
    /// <summary>Persistent addon changes prepared during staging and applied once, with an on-disk backup for rollback.</summary>
    private sealed class PluginAddonPlan(string root, string relativePrefix, string ledgerPath, PluginAddonFile[] writes, PluginAddonFile[] removals, PluginLedger ledger)
    {
        private readonly string backupRoot = Path.Combine(Path.GetDirectoryName(root)!, ".hcsl-plugin-backup-" + Guid.NewGuid().ToString("N"));
        private readonly List<(string Path, bool Existed)> applied = [];
        private bool ledgerExisted, settled;
        private string Destination(string relative) => InRoot(root, relative[relativePrefix.Length..]);
        private string BackupPath(string destination) => Path.Combine(backupRoot, "files", Path.GetRelativePath(root, destination));
        private void Backup(string destination)
        {
            var backup = BackupPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(destination, backup, true);
        }
        internal void Apply()
        {
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(backupRoot);
                if (File.Exists(ledgerPath)) { File.Copy(ledgerPath, Path.Combine(backupRoot, "ledger.json"), true); ledgerExisted = true; }
                foreach (var write in writes)
                {
                    var destination = Destination(write.RelativePath);
                    var existed = File.Exists(destination);
                    if (existed) Backup(destination);
                    applied.Add((destination, existed));
                    CopyPluginFile(write.SourcePath, destination, write.Sha256);
                }
                foreach (var removal in removals)
                {
                    var destination = Destination(removal.RelativePath);
                    if (!File.Exists(destination)) continue;
                    if (!string.Equals(HashFile(destination), removal.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    Backup(destination);
                    File.Delete(destination);
                    PruneEmptyDirectories(Path.GetDirectoryName(destination));
                }
                DeploymentFiles.AtomicJson(ledgerPath, ledger);
            }
            catch (Exception e)
            {
                try { Rollback(); }
                catch (Exception rollback)
                {
                    throw new DeploymentException("HCSL-DEPLOY-PLUGINS",
                        "Plugin addon files could not be written and the backup could not be restored; inspect the preset addons directory (" + rollback.GetType().Name + ").");
                }
                if (e is DeploymentException) throw;
                throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin addon files could not be written; persistent plugin files were restored.");
            }
        }
        internal void Commit()
        {
            if (settled) return;
            settled = true;
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
        }
        internal void Rollback()
        {
            if (settled) return;
            try
            {
                for (var i = applied.Count - 1; i >= 0; i--)
                    if (!applied[i].Existed && File.Exists(applied[i].Path)) File.Delete(applied[i].Path);
                var files = Path.Combine(backupRoot, "files");
                if (Directory.Exists(files))
                    foreach (var backup in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
                    {
                        var destination = Path.Combine(root, Path.GetRelativePath(files, backup));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(backup, destination, true);
                    }
                var ledgerBackup = Path.Combine(backupRoot, "ledger.json");
                if (File.Exists(ledgerBackup)) File.Copy(ledgerBackup, ledgerPath, true);
                else if (!ledgerExisted && File.Exists(ledgerPath)) File.Delete(ledgerPath);
            }
            finally
            {
                settled = true;
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
            }
        }
        private void PruneEmptyDirectories(string? directory)
        {
            for (; directory is not null && !string.Equals(directory, root, LocalPaths.Comparison); directory = Path.GetDirectoryName(directory))
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
                Directory.Delete(directory);
            }
        }
    }
    private sealed class FrameworkDataPlan
    {
        private readonly List<(string Source, string Destination)> moves;
        public FrameworkDataPlan(List<(string Source, string Destination)> moves) => this.moves = moves;
        internal void Apply()
        {
            var applied = new List<(string Source, string Destination)>();
            try
            {
                foreach (var move in moves)
                {
                    if (File.Exists(move.Destination))
                    {
                        if (string.Equals(HashFile(move.Source), HashFile(move.Destination), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(move.Source);
                            continue;
                        }
                        throw new DeploymentException("HCSL-DEPLOY-FRAMEWORK-DATA", "Framework user data already exists at the parking destination.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                    File.Move(move.Source, move.Destination, false);
                    applied.Add(move);
                }
            }
            catch
            {
                for (var i = applied.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (File.Exists(applied[i].Destination) && !File.Exists(applied[i].Source))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(applied[i].Source)!);
                            File.Move(applied[i].Destination, applied[i].Source, false);
                        }
                    }
                    catch (IOException) { }
                }
                throw;
            }
        }
        internal void Rollback()
        {
            for (var i = moves.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (File.Exists(moves[i].Destination) && !File.Exists(moves[i].Source))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(moves[i].Source)!);
                        File.Move(moves[i].Destination, moves[i].Source, false);
                    }
                }
                catch (IOException) { }
            }
        }
        internal void Commit() { }
    }

    private static FrameworkDataPlan PlanFrameworkData(string presetPath, string? previousFramework, string? targetFramework)
    {
        var moves = new List<(string Source, string Destination)>();
        if (previousFramework is not null && !string.Equals(previousFramework, targetFramework, StringComparison.Ordinal))
            AddQuarantine(presetPath, previousFramework, moves);
        if (targetFramework is not null)
            AddRestore(presetPath, targetFramework, moves);
        return new FrameworkDataPlan(moves);
    }

    private static void AddQuarantine(string presetPath, string framework, List<(string Source, string Destination)> moves)
    {
        var ledger = ReadPluginLedger(InRoot(presetPath, ".hcsl-plugin-ledger.json"));
        var packageFiles = new Dictionary<string, string>(StringComparer.FromComparison(LocalPaths.Comparison));
        foreach (var package in ledger.Packages ?? [])
            foreach (var file in package.Files ?? [])
                packageFiles[file.Path] = file.Sha256;
        var persistentPaths = FrameworkRules.PersistentPathsFor(framework);
        foreach (var root in FrameworkRules.PayloadRootsFor(framework))
        {
            var addonRelative = AddonRelative(root);
            if (addonRelative is null) continue;
            var sourceRoot = InRoot(presetPath, "addons/" + addonRelative);
            if (!Directory.Exists(sourceRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).LinkTarget is not null) continue;
                var within = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                var gamePath = root + "/" + within;
                if (packageFiles.TryGetValue(gamePath, out var sha) && string.Equals(HashFile(file), sha, StringComparison.OrdinalIgnoreCase))
                    continue;
                var persistent = persistentPaths.FirstOrDefault(p => p.Scope == FrameworkPersistentScope.Preset && PluginRules.IsPathUnder(gamePath, p.RuntimePath));
                var destination = persistent is not null
                    ? InRoot(presetPath, persistent.PersistentRelativePath + "/" + gamePath[(persistent.RuntimePath.Length + 1)..])
                    : InRoot(presetPath, "framework-data/" + framework + "/payload/" + within);
                moves.Add((file, destination));
            }
        }
    }

    private static void AddRestore(string presetPath, string framework, List<(string Source, string Destination)> moves)
    {
        var parked = InRoot(presetPath, "framework-data/" + framework + "/payload");
        if (!Directory.Exists(parked)) return;
        foreach (var root in FrameworkRules.PayloadRootsFor(framework))
        {
            var addonRelative = AddonRelative(root);
            if (addonRelative is null) continue;
            var destinationRoot = InRoot(presetPath, "addons/" + addonRelative);
            foreach (var file in Directory.EnumerateFiles(parked, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).LinkTarget is not null) continue;
                var within = Path.GetRelativePath(parked, file).Replace(Path.DirectorySeparatorChar, '/');
                var destination = InRoot(destinationRoot, within);
                if (!File.Exists(destination)) moves.Add((file, destination));
            }
        }
    }

    private static string? AddonRelative(string root) =>
        root.StartsWith("csgo/addons/", StringComparison.Ordinal) ? root["csgo/addons/".Length..] : null;
    private sealed record PluginPlan(PluginAddonPlan Addons, PluginAddonPlan FrameworkPlugins, ModuleLinkPlan? ModuleLinks = null)
    {
        internal void Apply()
        {
            Addons.Apply();
            try { FrameworkPlugins.Apply(); ModuleLinks?.Apply(); }
            catch { ModuleLinks?.Rollback(); FrameworkPlugins.Rollback(); Addons.Rollback(); throw; }
        }
        internal void Rollback() { ModuleLinks?.Rollback(); FrameworkPlugins.Rollback(); Addons.Rollback(); }
        internal void Commit() { ModuleLinks?.Commit(); FrameworkPlugins.Commit(); Addons.Commit(); }
    }

    /// <summary>Links each managed module directory to the preset data root while keeping built-in modules real.</summary>
    private sealed class ModuleLinkPlan(string runtimeRoot, string dataRoot)
    {
        private readonly List<string> created = [];
        internal void Apply()
        {
            if (!Directory.Exists(dataRoot)) return;
            LocalPaths.RejectLink(dataRoot);
            foreach (var directory in Directory.EnumerateDirectories(dataRoot))
            {
                LocalPaths.RejectLink(directory);
                var name = Path.GetFileName(directory);
                var destination = InRoot(runtimeRoot, name);
                if (File.Exists(destination) || Directory.Exists(destination) || new FileInfo(destination).LinkTarget is not null || new DirectoryInfo(destination).LinkTarget is not null)
                    throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "A managed module conflicts with a built-in module directory: " + name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.CreateSymbolicLink(destination, directory);
                created.Add(destination);
            }
        }
        internal void Rollback()
        {
            for (var i = created.Count - 1; i >= 0; i--)
            {
                try { if (new DirectoryInfo(created[i]).LinkTarget is not null) Directory.Delete(created[i]); }
                catch (IOException) { }
            }
        }
        internal void Commit() { }
    }

    private static (string PluginRoot, string DataRoot)? ManagedPluginRoute(string? framework)
    {
        var root = FrameworkRules.PluginRootFor(framework);
        var data = FrameworkRules.PluginDataRootFor(framework);
        return root is null || data is null ? null : (root, data);
    }

    private static (string PluginRoot, string DataRoot)? ManagedPluginRouteOfPath(string path)
    {
        foreach (var definition in FrameworkRules.Known)
            if (definition.PluginRoot is { } root && definition.PluginDataRoot is { } data
                && PluginRules.IsPathUnder(path, root) && !path.Equals(root, LocalPaths.Comparison)) return (root, data);
        return null;
    }

    private static string? ManagedPluginFrameworkOfPath(string path)
    {
        foreach (var definition in FrameworkRules.Known)
            if (definition.PluginRoot is { } root && definition.PluginDataRoot is not null
                && PluginRules.IsPathUnder(path, root) && !path.Equals(root, LocalPaths.Comparison)) return definition.Id;
        return null;
    }

    private static bool IsManagedPluginFile(LocalPluginStore.Package package, string path) =>
        !package.FrameworkPayload && ManagedPluginRoute(package.Framework) is { } route
        && PluginRules.IsPathUnder(path, route.PluginRoot) && !path.Equals(route.PluginRoot, LocalPaths.Comparison);

    private static bool IsPersistentPluginRoot(string? framework, string root) =>
        FrameworkRules.PersistentPathsFor(framework).Any(p => p.Scope == FrameworkPersistentScope.Preset
            && string.Equals(p.RuntimePath, root, LocalPaths.Comparison));

    /// <summary>Builds persistent addon/framework-plugin changes and overlays non-addon package files into staging.</summary>
    private static PluginPlan PlanPlugins(string stage, string presetPath, LocalPluginStore.Package[] packages, string[] persistentPrefixes, string? framework)
    {
        try
        {
            var addons = InRoot(presetPath, "addons"); LocalPaths.RejectLink(addons);
            if (!Directory.Exists(addons)) throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Preset addons directory is missing.");
            var ledgerPath = InRoot(presetPath, ".hcsl-plugin-ledger.json");
            var legacyLedger = InRoot(addons, ".hcsl-plugin-ledger.json");
            var old = ReadPluginLedger(File.Exists(ledgerPath) ? ledgerPath : legacyLedger);
            var dataLedgerPath = InRoot(presetPath, ".hcsl-framework-plugin-ledger.json");
            var oldData = ReadPluginLedger(dataLedgerPath);
            var dataFramework = packages.Select(p => p.Framework).FirstOrDefault(f => ManagedPluginRoute(f) is not null)
                ?? (ManagedPluginRoute(framework) is not null ? framework : null)
                ?? (oldData.Packages ?? []).SelectMany(p => p.Files ?? []).Select(f => ManagedPluginFrameworkOfPath(f.Path))
                    .FirstOrDefault(f => f is not null);
            var dataRoute = ManagedPluginRoute(dataFramework)
                ?? (oldData.Packages ?? []).SelectMany(p => p.Files ?? []).Select(f => ManagedPluginRouteOfPath(f.Path)).FirstOrDefault(r => r is not null)
                ?? ("csgo/addons/swiftlys2/plugins", "framework-data/swiftlys2/plugins");
            var dataRoot = InRoot(presetPath, dataRoute.DataRoot);
            var dataPrefix = dataRoute.PluginRoot + "/";
            var moduleLinks = framework is not null && string.Equals(dataFramework, framework, StringComparison.Ordinal)
                && !IsPersistentPluginRoot(framework, dataRoute.PluginRoot)
                ? new ModuleLinkPlan(InRoot(stage, "game/" + dataRoute.PluginRoot), dataRoot) : null;
            var writes = new List<PluginAddonFile>();
            var dataWrites = new List<PluginAddonFile>();
            var installed = new List<PluginLedgerEntry>();
            var dataInstalled = new List<PluginLedgerEntry>();
            var keep = new HashSet<string>(StringComparer.FromComparison(LocalPaths.Comparison));
            var dataKeep = new HashSet<string>(StringComparer.FromComparison(LocalPaths.Comparison));
            var gameInfo = InRoot(stage, "game/csgo/gameinfo.gi");
            var mergeGameInfo = TryMergeGeneratedGameInfo(gameInfo, packages);
            foreach (var package in packages)
            {
                var files = new List<PluginLedgerFile>();
                var dataFiles = new List<PluginLedgerFile>();
                foreach (var file in package.Files)
                {
                    RejectReservedPluginPath(file.RelativePath);
                    if (mergeGameInfo && IsGameInfo(file.RelativePath)) continue;
                    if (string.Equals(package.Framework, dataFramework, StringComparison.Ordinal) && IsManagedPluginFile(package, file.RelativePath))
                    {
                        dataWrites.Add(new(file.RelativePath, file.FullPath, file.Sha256));
                        dataKeep.Add(file.RelativePath);
                        dataFiles.Add(new(file.RelativePath, file.Sha256));
                        continue;
                    }
                    if (persistentPrefixes.Any(prefix => PluginRules.IsPathUnder(file.RelativePath, prefix))) continue;
                    if (PluginRules.IsAddonFile(file.RelativePath))
                    {
                        writes.Add(new(file.RelativePath, file.FullPath, file.Sha256));
                        keep.Add(file.RelativePath);
                        files.Add(new(file.RelativePath, file.Sha256));
                    }
                    else CopyPluginFile(file.FullPath, InRoot(stage, "game/" + file.RelativePath), file.Sha256);
                }
                installed.Add(new(package.Id, package.Version, [.. files]));
                if (dataFiles.Count != 0) dataInstalled.Add(new(package.Id, package.Version, [.. dataFiles]));
            }
            var removals = new List<PluginAddonFile>();
            foreach (var oldPackage in old.Packages ?? [])
                foreach (var file in oldPackage.Files ?? [])
                {
                    if (keep.Contains(file.Path) || !PluginRules.IsAddonFile(file.Path)) continue;
                    string destination;
                    try { destination = AddonsPath(addons, file.Path["csgo/addons/".Length..]); }
                    catch (DeploymentException) { continue; }
                    if (!File.Exists(destination)) continue;
                    if (!string.Equals(HashFile(destination), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    removals.Add(new(file.Path, destination, file.Sha256));
                }
            var dataRemovals = new List<PluginAddonFile>();
            foreach (var oldPackage in oldData.Packages ?? [])
                foreach (var file in oldPackage.Files ?? [])
                {
                    if (dataKeep.Contains(file.Path)) continue;
                    var route = ManagedPluginRouteOfPath(file.Path);
                    if (route is null || !string.Equals(route.Value.DataRoot, dataRoute.DataRoot, StringComparison.Ordinal)) continue;
                    var destination = InRoot(dataRoot, file.Path[(route.Value.PluginRoot.Length + 1)..]);
                    if (!File.Exists(destination)) continue;
                    if (!string.Equals(HashFile(destination), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    dataRemovals.Add(new(file.Path, destination, file.Sha256));
                }
            return new(
                new PluginAddonPlan(addons, "csgo/addons/", ledgerPath, [.. writes], [.. removals], new PluginLedger(1, [.. installed])),
                new PluginAddonPlan(dataRoot, dataPrefix, dataLedgerPath, [.. dataWrites], [.. dataRemovals], new PluginLedger(1, [.. dataInstalled])),
                moduleLinks);
        }
        catch (DeploymentException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException or InvalidDataException or NotSupportedException)
        { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin files could not be prepared; no persistent plugin file was changed."); }
    }

    private static bool IsGameInfo(string path) => path.Equals("csgo/gameinfo.gi", StringComparison.OrdinalIgnoreCase);

    private static bool TryMergeGeneratedGameInfo(string destination, LocalPluginStore.Package[] packages)
    {
        var overlays = packages.SelectMany(package => package.Files.Where(file => IsGameInfo(file.RelativePath))
            .Select(file => (Package: package, File: file))).ToArray();
        if (overlays.Length < 2 || !File.Exists(destination)) return false;
        var baseline = File.ReadAllLines(destination);
        var paths = new List<string>();
        foreach (var (package, file) in overlays)
        {
            var path = package.FrameworkPayload ? package.Framework switch
            {
                "metamod" => "csgo/addons/metamod",
                "swiftlys2" => "csgo/addons/swiftlys2",
                "modsharp" => "sharp",
                _ => null
            } : null;
            if (path is null) throw Unmergeable();
            var actual = File.ReadAllLines(file.FullPath);
            var generated = LocalPluginImporter.BuildGameInfoOverlay(baseline, [path]);
            var legacy = baseline.ToList();
            var anchor = legacy.FindIndex(line => line.TrimStart().StartsWith("Game", StringComparison.Ordinal)
                && line.TrimEnd().EndsWith("csgo", StringComparison.Ordinal));
            if (anchor >= 0 && !legacy.Any(line => line.Contains(path, StringComparison.OrdinalIgnoreCase)))
                legacy.Insert(anchor, legacy[anchor][..(legacy[anchor].Length - legacy[anchor].TrimStart().Length)] + "Game\t" + path);
            if (!actual.SequenceEqual(generated) && !actual.SequenceEqual(legacy)) throw Unmergeable();
            paths.Add(path);
        }
        var merged = LocalPluginImporter.BuildGameInfoOverlay(baseline, paths);
        if (!merged.SequenceEqual(baseline))
        {
            // Replace the staged file, never write through a shared clone or link.
            File.Delete(destination);
            File.WriteAllLines(destination, merged);
        }
        return true;

        static DeploymentException Unmergeable() => new("HCSL-DEPLOY-GAMEINFO",
            "Multiple gameinfo overlays are present and at least one is custom or based on another game build. Reimport or manually reconcile the overlays before deploying.");
    }

    private static void DeleteLegacyPluginLedger(string presetPath)
    {
        try
        {
            var legacy = InRoot(presetPath, "addons/.hcsl-plugin-ledger.json");
            if (File.Exists(legacy) && File.Exists(InRoot(presetPath, ".hcsl-plugin-ledger.json"))) File.Delete(legacy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeploymentException) { }
    }
    private static void ClearRotation(string stage)
    {
        var path = InRoot(stage, "game/csgo/" + ServerMapGroupRules.FileName); LocalPaths.RejectLink(path);
        if (!File.Exists(path)) return;
        if (!File.ReadAllText(path).StartsWith(ServerMapGroupRules.ManagedMarker, StringComparison.Ordinal)) return;
        File.Delete(path);
    }
    private static void WriteRotation(string stage, ServerMapGroupPlan rotation, bool replace = false)
    {
        var path = InRoot(stage, "game/csgo/" + ServerMapGroupRules.FileName); LocalPaths.RejectLink(path);
        if (!replace && File.Exists(path) && new FileInfo(path).Length != 0)
            throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The staged game already has a nonempty gamemodes_server.txt.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(rotation.Content);
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string SmallText(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new DeploymentException("HCSL-DEPLOY-STATE", "Installation metadata is too large.");
        return File.ReadAllText(path);
    }
    internal void VerifyBaseline(string server, Baseline activeBaseline, CancellationToken ct)
    {
        VerifyHash(InRoot(server, OperatingSystem.IsWindows() ? WindowsEntrypoint : "game/cs2.sh"), activeBaseline.EntrypointSha256);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(InRoot(server, "game/cs2.sh")) & UnixFileMode.UserExecute) == 0)
            throw new DeploymentException("HCSL-DEPLOY-FILES", "cs2.sh is not executable.");
        foreach (var f in activeBaseline.Inventory)
        {
            ct.ThrowIfCancellationRequested(); var path = InRoot(server, f.Path);
            if (new FileInfo(path).Length != f.Bytes) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Native file size differs from the verified baseline.");
            VerifyHash(path, f.Sha256);
        }
        foreach (var config in activeBaseline.Configs) if (new FileInfo(InRoot(server, config)).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-FILES", "Required path configuration is missing.");
    }
    internal static bool RotationFileMatches(DeploymentReceipt receipt, string target)
    {
        if (receipt.RotationCollection is not null) return receipt.RotationGroup is null && receipt.RotationHash is null;
        if (receipt.RotationHash is null) return true;
        var rotation = InRoot(target, "game/csgo/" + ServerMapGroupRules.FileName); LocalPaths.RejectLink(rotation);
        return File.Exists(rotation) && HashFile(rotation).Equals(receipt.RotationHash, StringComparison.Ordinal);
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
        if (OperatingSystem.IsWindows())
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("cs2"))
            {
                using (process)
                {
                    string? executable;
                    try { executable = process.MainModule?.FileName; }
                    catch (InvalidOperationException) when (process.HasExited) { continue; }
                    catch (System.ComponentModel.Win32Exception e)
                    { throw new DeploymentException("HCSL-DEPLOY-RUNNING", "Could not verify that a CS2 process is outside the runtime: " + e.Message); }
                    if (executable is null || LocalPaths.Contains(target, executable))
                        throw new DeploymentException("HCSL-DEPLOY-RUNNING", "A CS2 process may be using this runtime; stop it before redeploying.");
                }
            }
            return;
        }
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
    private static bool TryManagedModuleLink(DeploymentReceipt receipt, string instance, string relative, out string expected)
    {
        expected = "";
        var root = FrameworkRules.PluginRootFor(receipt.Framework);
        var data = FrameworkRules.PluginDataRootFor(receipt.Framework);
        if (root is null || data is null) return false;
        var runtimeRoot = "game/" + root;
        var prefix = runtimeRoot + "/";
        if (!relative.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = relative[prefix.Length..];
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) return false;
        expected = InRoot(instance, "presets/" + Id(receipt.PresetId) + "/" + data + "/" + name);
        return true;
    }

    internal static DeploymentReceipt? ValidateExisting(IWorkspaceLease lease, Guid id, string target, bool verifyRotation = true, bool allowLegacy = false)
    {
        LocalPaths.RejectLink(target);
        if (File.Exists(target)) throw new DeploymentException("HCSL-DEPLOY-UNMANAGED", "Runtime path is a file.");
        if (!Directory.Exists(target)) return null;
        var receiptFile = Path.Combine(target, "deployment.json");
        if (!File.Exists(receiptFile)) throw new DeploymentException("HCSL-DEPLOY-UNMANAGED", "Existing runtime has no deployment receipt; it was not modified.");
        var receipt = DeploymentFiles.Read<DeploymentReceipt>(receiptFile);
        if (receipt.Status != "DeployedOffline" || receipt.InstanceId != id || receipt.DataId != lease.Identity.DataId || receipt.LibraryId != lease.Identity.LibraryId || receipt.OperationId == Guid.Empty)
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Runtime ownership or deployment format does not match.");
        if (receipt.FormatVersion == 1)
        {
            if (!allowLegacy) throw new DeploymentException("HCSL-DEPLOY-LAYOUT", "This runtime uses the old persistent layout; migrate and redeploy it before use.");
            return receipt;
        }
        if (receipt.FormatVersion != 2)
            throw new DeploymentException("HCSL-DEPLOY-LAYOUT", "This runtime uses the old persistent layout; migrate and redeploy it before use.");
        if (receipt.PersistentMappings == null || receipt.PersistentMappings.Select(m => m.RuntimePath).Distinct().Count() != receipt.PersistentMappings.Length)
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Incomplete persistent mapping metadata.");
        var required = new[] { "game/csgo/cfg", "game/csgo/addons", "game/csgo/logs", "game/csgo/recordings", "userdata", "home" };
        if (required.Any(path => receipt.PersistentMappings.All(m => m.RuntimePath != path)))
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Persistent mapping metadata is missing a required path.");
        if (verifyRotation && receipt.RotationHash is not null && !RotationFileMatches(receipt, target))
            throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The generated gamemodes_server.txt is missing or modified; redeploy the preset.");
        // Unknown files are retained in previous generations. Unknown links are not followed or silently accepted.
        var instance = InRoot(lease.Layout.InstancesRoot, Id(id));
        foreach (var mapping in receipt.PersistentMappings)
        {
            string allowed;
            if (mapping.RuntimePath is "game/csgo/cfg" or "game/csgo/addons")
                allowed = "presets/" + Id(receipt.PresetId) + "/" + (mapping.RuntimePath == "game/csgo/cfg" ? "cfg" : "addons");
            else if (mapping.RuntimePath is "game/csgo/logs" or "game/csgo/recordings")
                allowed = mapping.RuntimePath[(mapping.RuntimePath.LastIndexOf('/') + 1)..];
            else if (mapping.RuntimePath is "userdata" or "home")
                allowed = mapping.RuntimePath;
            else
            {
                var persistent = FrameworkRules.PersistentPathsFor(receipt.Framework)
                    .FirstOrDefault(p => p.RuntimePath == GameRelative(mapping.RuntimePath));
                if (persistent is null)
                    throw new DeploymentException("HCSL-DEPLOY-PATH", "Unknown persistent mapping.");
                var root = persistent.Scope == FrameworkPersistentScope.Preset
                    ? "presets/" + Id(receipt.PresetId) : "";
                allowed = string.IsNullOrEmpty(root) ? persistent.PersistentRelativePath : root + "/" + persistent.PersistentRelativePath;
            }
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
                    if (m.Kind == DeploymentMappingKind.Materialized)
                    {
                        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
                            throw new DeploymentException("HCSL-DEPLOY-PATH", "Materialized persistent directory was modified.");
                        continue;
                    }
                    var link = new DirectoryInfo(path).LinkTarget;
                    if (link == null || !Path.IsPathFullyQualified(link) || !string.Equals(Path.GetFullPath(link), m.PersistentPath, LocalPaths.Comparison))
                        throw new DeploymentException("HCSL-DEPLOY-PATH", "Persistent runtime link was modified.");
                    continue;
                }
                var moduleLink = new DirectoryInfo(path).LinkTarget;
                if (TryManagedModuleLink(receipt, instance, rel, out var moduleTarget) && moduleLink is not null)
                {
                    if (!Path.IsPathFullyQualified(moduleLink)
                        || !string.Equals(Path.GetFullPath(moduleLink), moduleTarget, LocalPaths.Comparison))
                        throw new DeploymentException("HCSL-DEPLOY-PATH", "Framework module link was modified.");
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
        LocalPluginMigration.Run(Path.Combine(lease.Layout.LibraryRoot, "plugin-packages"));
        var plan = Prepare(lease, options, cancellationToken); var l = lease.Layout;
        if ((TrySyncPlugins(lease, plan, cancellationToken) ?? TryUpdateRotation(lease, plan, cancellationToken)) is { } updated)
        {
            progress?.Report(new("DeployedOffline"));
            return updated;
        }
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
        EmptyDirs(sourceGame); VerifyBaseline(stage, ActiveBaseline(l, plan.BuildId), cancellationToken); checkpoint?.Invoke("Cloned");
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
            if (mapping.Kind == DeploymentMappingKind.Link)
                Directory.CreateSymbolicLink(destination, mapping.PersistentPath);
            else
                Directory.CreateDirectory(destination);
        }
        var home = plan.PersistentMappings.Single(m => m.RuntimePath == "home").PersistentPath;
        if (!OperatingSystem.IsWindows())
        {
            var sdk = InRoot(home, ".steam/sdk64/steamclient.so");
            if (!File.Exists(sdk)) SeedFile(InRoot(l.SteamCmdRoot, "tool/linux64/steamclient.so"), sdk);
        }
        var presetPath = Path.Combine(InRoot(l.InstancesRoot, Id(tx.InstanceId)), "presets", Id(plan.PresetId));
        var currentCollection = Collection(presetPath);
        if (!string.Equals(currentCollection, plan.RotationCollection, StringComparison.Ordinal))
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The map pool changed while deployment was prepared. Redeploy to regenerate the rotation.");
        var currentPlugins = PluginSet(presetPath, l.LibraryRoot);
        if (!string.Equals(currentPlugins.Hash, plan.PluginSetHash, StringComparison.Ordinal))
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The plugin selection changed while deployment was prepared. Redeploy.");
        var persistentPrefixes = plan.PersistentMappings.Where(m => m.SeedDefaults && m.RuntimePath != "game/csgo/cfg")
            .Select(m => GameRelative(m.RuntimePath)).ToArray();
        var previousFramework = ValidateExisting(lease, tx.InstanceId, plan.RuntimePath, verifyRotation: false, allowLegacy: true)?.Framework;
        var frameworkData = PlanFrameworkData(presetPath, previousFramework, plan.Framework);
        try
        {
            frameworkData.Apply();
            MigratePersistentFromRuntime(plan.RuntimePath, plan.PersistentMappings);
            MigrateModuleDataFromRuntime(plan.RuntimePath, presetPath, plan.Framework, currentPlugins.Packages);
            if (previousFramework is not null && !string.Equals(previousFramework, plan.Framework, StringComparison.Ordinal))
                MigrateModuleDataFromRuntime(plan.RuntimePath, presetPath, previousFramework,
                    FrameworkPackages(presetPath, l.LibraryRoot, previousFramework));
            SeedPersistentDefaults(plan.PersistentMappings, currentPlugins.Packages);
            var pluginChanges = PlanPlugins(stage, presetPath, currentPlugins.Packages, persistentPrefixes, plan.Framework);
            if (plan.RotationHash is not null)
            {
                var entry = LocalInstanceStore.Load(InRoot(l.InstancesRoot, Id(tx.InstanceId)), tx.InstanceId);
                var preset = entry.Presets.Single(p => p.Id == plan.PresetId);
                var current = Rotation(presetPath, preset);
                if (current is null || !string.Equals(current.Hash, plan.RotationHash, StringComparison.Ordinal))
                    throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The map pool changed while deployment was prepared. Redeploy to regenerate the map group.");
                WriteRotation(stage, current);
            }
            else if (currentCollection is null)
                ClearRotation(stage);
            var receipt = new DeploymentReceipt(2, tx.OperationId, "DeployedOffline", lease.Identity.DataId, lease.Identity.LibraryId,
                plan.InstanceId, plan.PresetId, plan.InstanceRevision, plan.PresetRevision, plan.BuildId, plan.CopyMode, DateTimeOffset.UtcNow, plan.PersistentMappings,
                plan.RotationGroup, plan.RotationHash, plan.RotationCollection, plan.PluginSetHash, plan.Framework, plan.FrameworkReadiness,
                CapturePluginManifest(currentPlugins.Packages));
            DeploymentFiles.AtomicJson(Path.Combine(stage, "deployment.json"), receipt);
            ValidateExisting(lease, tx.InstanceId, stage); cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke("Prepared");
            EnsureStopped(plan.RuntimePath); ValidateExisting(lease, tx.InstanceId, plan.RuntimePath, verifyRotation: false, allowLegacy: true);
            // Persistent addons are backed up and rolled back if publication fails. A crash between the two
            // renames stays recoverable through the journal and the previous generation.
            try
            {
                pluginChanges.Apply();
                MaterializeMappings(stage, plan.PersistentMappings, options.CopyMode);
                checkpoint?.Invoke("PluginsApplied");
            }
            catch { pluginChanges.Rollback(); throw; }
            try
            {
                // Ignore cancellation inside the short publication window. Recovery handles process termination between renames.
                if (Directory.Exists(plan.RuntimePath)) Directory.Move(plan.RuntimePath, previous);
                checkpoint?.Invoke("PreviousMoved");
                Directory.Move(stage, plan.RuntimePath);
            }
            catch { pluginChanges.Rollback(); throw; }
            pluginChanges.Commit();
            DeleteLegacyPluginLedger(presetPath);
            ValidateExisting(lease, tx.InstanceId, plan.RuntimePath); checkpoint?.Invoke("Published");
            File.Delete(journal); progress?.Report(new("DeployedOffline", done, plan.FileCount));
            frameworkData.Commit();
            return receipt;
        }
        catch
        {
            frameworkData.Rollback();
            throw;
        }
    }
    private static DeployedPluginPackage[]? CapturePluginManifest(LocalPluginStore.Package[] packages)
    {
        var manifest = packages.Select(package => new DeployedPluginPackage(package.Id, package.Version, package.Hash,
            package.FrameworkPayload, package.Framework,
            package.Files.Select(file => new DeployedPluginFile(file.RelativePath, file.Sha256)).ToArray())).ToArray();
        // Leave room for persistent mappings under the receipt reader's 1 MiB limit.
        return JsonSerializer.SerializeToUtf8Bytes(manifest).Length <= 512 * 1024 ? manifest : null;
    }

    private DeploymentReceipt? TrySyncPlugins(IWorkspaceLease lease, DeploymentPlan plan, CancellationToken ct)
    {
        var prior = ValidateExisting(lease, plan.InstanceId, plan.RuntimePath, verifyRotation: false, allowLegacy: true);
        if (prior is not { FormatVersion: 2, PluginManifest: not null } || prior.PluginSetHash == plan.PluginSetHash ||
            prior.Framework != plan.Framework || prior.FrameworkReadiness != plan.FrameworkReadiness || prior.CopyMode != plan.CopyMode ||
            !prior.PersistentMappings.SequenceEqual(plan.PersistentMappings) ||
            ServerPreparation.IsStale(prior with { PluginSetHash = plan.PluginSetHash }, plan) ||
            !RotationFileMatches(prior, plan.RuntimePath)) return null;
        var mapping = plan.PersistentMappings.Single(m => m.RuntimePath == "game/csgo/addons");
        if (mapping.Kind != DeploymentMappingKind.Link) return null;
        var layout = lease.Layout;
        var preset = InRoot(layout.InstancesRoot, Id(plan.InstanceId) + "/presets/" + Id(plan.PresetId));
        var selection = PluginSet(preset, layout.LibraryRoot);
        if (selection.Hash != plan.PluginSetHash)
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "Plugin selection changed during deployment.");
        var manifest = CapturePluginManifest(selection.Packages);
        if (manifest is null) return null;
        var unchanged = manifest.Where(p => prior.PluginManifest.Any(old => old.Id == p.Id && old.Version == p.Version && old.Hash == p.Hash))
            .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var oldChanged = prior.PluginManifest.Where(p => !unchanged.Contains(p.Id)).ToArray();
        var newChanged = manifest.Where(p => !unchanged.Contains(p.Id)).ToArray();
        if (oldChanged.Concat(newChanged).Any(p => p.FrameworkPayload || FrameworkRules.Find(p.Id) is not null || p.Files.Any(f =>
            !PluginRules.IsAddonFile(f.Path) || ManagedPluginRouteOfPath(f.Path) is not null ||
            plan.PersistentMappings.Any(m => m != mapping && Overlaps(GameRelative(m.RuntimePath), f.Path))))) return null;
        var comparer = StringComparer.FromComparison(LocalPaths.Comparison);
        var ledgerPath = InRoot(preset, ".hcsl-plugin-ledger.json");
        var ledger = ReadPluginLedger(ledgerPath);
        var oldFiles = oldChanged.SelectMany(p => p.Files).ToDictionary(f => f.Path, comparer);
        var newFiles = selection.Packages.Where(p => !unchanged.Contains(p.Id)).SelectMany(p => p.Files)
            .ToDictionary(f => f.RelativePath, comparer);
        var keptEntries = ledger.Packages.Where(p => unchanged.Contains(p.Id)).ToArray();
        var keptPaths = keptEntries.SelectMany(p => p.Files).Select(f => f.Path).ToHashSet(comparer);
        var owned = ledger.Packages.Where(p => oldChanged.Any(old => old.Id == p.Id && old.Version == p.Version))
            .SelectMany(p => p.Files).ToDictionary(f => f.Path, comparer);
        EnsureStopped(plan.RuntimePath);
        VerifyBaseline(plan.RuntimePath, ActiveBaseline(layout, plan.BuildId), ct);
        var writes = new List<PluginAddonFile>();
        var removals = new List<PluginAddonFile>();
        const string prefix = "csgo/addons/";
        foreach (var path in oldFiles.Keys.Union(newFiles.Keys, comparer))
        {
            ct.ThrowIfCancellationRequested();
            if (keptPaths.Contains(path))
                throw new DeploymentException("HCSL-DEPLOY-PLUGIN-CONFLICT", $"Plugin path belongs to another installed package: {path}");
            oldFiles.TryGetValue(path, out var old);
            newFiles.TryGetValue(path, out var next);
            if (old is not null && (!owned.TryGetValue(path, out var recorded) || recorded.Sha256 != old.Sha256))
                throw new DeploymentException("HCSL-DEPLOY-PLUGIN-CONFLICT", $"Plugin ownership could not be confirmed: {path}");
            if (old is not null && old.Sha256 == next?.Sha256) continue;
            var destination = AddonsPath(mapping.PersistentPath, path[prefix.Length..]);
            if (Directory.Exists(destination) || (File.Exists(destination) &&
                (old is null || !string.Equals(HashFile(destination), old.Sha256, StringComparison.OrdinalIgnoreCase))))
                throw new DeploymentException("HCSL-DEPLOY-PLUGIN-CONFLICT", $"Plugin path is occupied or modified: {path}");
            if (next is not null) writes.Add(new(next.RelativePath, next.FullPath, next.Sha256));
            else if (old is not null) removals.Add(new(old.Path, destination, old.Sha256));
        }
        var updatedLedger = new PluginLedger(1, [.. keptEntries, .. newChanged.Select(p =>
            new PluginLedgerEntry(p.Id, p.Version, p.Files.Select(f => new PluginLedgerFile(f.Path, f.Sha256)).ToArray()))]);
        var changes = new PluginAddonPlan(mapping.PersistentPath, prefix, ledgerPath, [.. writes], [.. removals], updatedLedger);
        var tx = new Transaction(2, Guid.NewGuid(), plan.InstanceId, lease.Identity.DataId, lease.Identity.LibraryId);
        var journal = Journal(layout, plan.InstanceId);
        var receipt = prior with { OperationId = tx.OperationId, PluginSetHash = plan.PluginSetHash, PluginManifest = manifest };
        ct.ThrowIfCancellationRequested();
        DeploymentFiles.AtomicJson(journal, tx);
        changes.Apply();
        try
        {
            checkpoint?.Invoke("IncrementalPluginsApplied");
            DeploymentFiles.AtomicJson(InRoot(plan.RuntimePath, "deployment.json"), receipt);
        }
        catch { changes.Rollback(); File.Delete(journal); throw; }
        changes.Commit();
        File.Delete(journal);
        return receipt;
    }

    private DeploymentReceipt? TryUpdateRotation(IWorkspaceLease lease, DeploymentPlan plan, CancellationToken ct)
    {
        var target = plan.RuntimePath;
        var prior = ValidateExisting(lease, plan.InstanceId, target, verifyRotation: false, allowLegacy: true);
        if (prior is not { FormatVersion: 2 } ||
            (prior.RotationHash == plan.RotationHash && prior.RotationCollection == plan.RotationCollection) ||
            prior.CopyMode != plan.CopyMode || prior.Framework != plan.Framework || prior.FrameworkReadiness != plan.FrameworkReadiness ||
            !prior.PersistentMappings.SequenceEqual(plan.PersistentMappings) ||
            ServerPreparation.IsStale(prior with { RotationHash = plan.RotationHash, RotationGroup = plan.RotationGroup, RotationCollection = plan.RotationCollection }, plan) ||
            !RotationFileMatches(prior, target)) return null;

        var layout = lease.Layout;
        EnsureStopped(target);
        VerifyBaseline(target, ActiveBaseline(layout, plan.BuildId), ct);
        var instance = InRoot(layout.InstancesRoot, Id(plan.InstanceId));
        var preset = LocalInstanceStore.Load(instance, plan.InstanceId).Presets.Single(p => p.Id == plan.PresetId);
        var presetPath = InRoot(instance, "presets/" + Id(preset.Id));
        var pool = LocalPresetMapPoolStore.ReadForDeployment(InRoot(presetPath, "maps.json"));
        var collection = MapPoolRules.CollectionId(pool);
        var rotation = collection is null ? ServerMapGroupRules.Create(preset.Id, preset.Map, pool) : null;
        if (rotation?.Hash != plan.RotationHash || collection != plan.RotationCollection)
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The map pool changed while deployment was prepared.");
        var maps = collection is not null ? new[] { ServerMapRules.CollectionSeedMap }
            : rotation is null ? new[] { preset.Map } : pool.Maps.Select(map => map.Value).Append(preset.Map);
        foreach (var map in maps.Distinct(StringComparer.Ordinal))
        {
            var package = new FileInfo(InRoot(target, "game/csgo/maps/" + map + ".vpk"));
            if (!package.Exists || package.Length == 0)
                throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The deployed runtime is missing a map required by the new rotation.");
        }
        var path = InRoot(target, "game/csgo/" + ServerMapGroupRules.FileName);
        // Only replace/delete a file whose ownership was verified against the previous receipt.
        if (prior.RotationHash is null && Path.Exists(path)) return null;
        var original = prior.RotationHash is null ? null : SmallText(path);
        ct.ThrowIfCancellationRequested();
        var receipt = prior with { RotationGroup = rotation?.Name, RotationHash = rotation?.Hash, RotationCollection = collection };
        if (rotation is not null) WriteRotation(target, rotation, replace: original is not null);
        else if (original is not null) File.Delete(path);
        try
        {
            checkpoint?.Invoke("RotationWritten");
            DeploymentFiles.AtomicJson(InRoot(target, "deployment.json"), receipt);
        }
        catch
        {
            if (original is not null)
                WriteRotation(target, new(prior.RotationGroup!, preset.Map, original, prior.RotationHash!), replace: true);
            else if (rotation is not null) File.Delete(path);
            throw;
        }
        return receipt;
    }

    private static string GameRelative(string runtimePath) =>
        runtimePath.StartsWith("game/", StringComparison.Ordinal) ? runtimePath[5..] : runtimePath;
    /// <summary>Copies user-owned module directories from an older deployment into the preset data root before the runtime is replaced.</summary>
    private static void MigrateModuleDataFromRuntime(string runtime, string presetPath, string? framework, LocalPluginStore.Package[] packages)
    {
        var route = ManagedPluginRoute(framework);
        if (route is null) return;
        var source = Path.Combine(runtime, "game", route.Value.PluginRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(source) || new DirectoryInfo(source).LinkTarget is not null) return;
        var frameworkPackages = packages.Where(p => p.FrameworkPayload && string.Equals(p.Framework, framework, StringComparison.Ordinal)).ToArray();
        if (frameworkPackages.Length == 0 && Directory.EnumerateDirectories(source).Any(d => new DirectoryInfo(d).LinkTarget is null))
            throw new DeploymentException("HCSL-DEPLOY-PERSISTENT", "The previous framework package is missing; user module directories cannot be separated from built-in modules.");
        var dataRoot = InRoot(presetPath, route.Value.DataRoot);
        var builtIn = frameworkPackages
            .SelectMany(p => p.Files)
            .Where(f => PluginRules.IsPathUnder(f.RelativePath, route.Value.PluginRoot))
            .Select(f => f.RelativePath[(route.Value.PluginRoot.Length + 1)..].Split('/')[0])
            .ToHashSet(StringComparer.FromComparison(LocalPaths.Comparison));
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null) continue;
            var name = Path.GetFileName(directory);
            if (builtIn.Contains(name)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).LinkTarget is not null) continue;
                var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                var destination = InRoot(dataRoot, name + "/" + relative);
                if (File.Exists(destination))
                {
                    if (string.Equals(HashFile(destination), HashFile(file), StringComparison.OrdinalIgnoreCase)) continue;
                    throw new DeploymentException("HCSL-DEPLOY-PERSISTENT", "A user module conflicts with existing framework data: " + name);
                }
                CopyPluginFile(file, destination, HashFile(file));
            }
        }
    }

    private static void MigratePersistentFromRuntime(string runtime, DeploymentMapping[] mappings)
    {
        foreach (var mapping in mappings.Where(m => m.SeedDefaults && m.RuntimePath != "game/csgo/cfg"))
        {
            var source = Path.Combine(runtime, mapping.RuntimePath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(source) || new DirectoryInfo(source).LinkTarget is not null) continue;
            DeploymentFiles.Tree(source, file =>
            {
                var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                var destination = InRoot(mapping.PersistentPath, relative);
                if (!File.Exists(destination)) SeedFile(file, destination);
            }, CancellationToken.None);
        }
    }
    private static void MaterializeMappings(string stage, DeploymentMapping[] mappings, DeploymentCopyMode mode)
    {
        foreach (var mapping in mappings.Where(m => m.Kind == DeploymentMappingKind.Materialized))
        {
            var destination = InRoot(stage, mapping.RuntimePath);
            var skip = mappings.Where(m => m != mapping && m.Kind == DeploymentMappingKind.Link
                    && PluginRules.IsPathUnder(m.RuntimePath, mapping.RuntimePath))
                .Select(m => m.RuntimePath[(mapping.RuntimePath.Length + 1)..]).ToArray();
            MaterializeTree(mapping.PersistentPath, destination, skip, mode);
        }
    }
    private static void MaterializeTree(string source, string destination, string[] skip, DeploymentCopyMode mode)
    {
        if (!Directory.Exists(source)) throw new DeploymentException("HCSL-DEPLOY-PERSISTENT", "Materialized source directory is missing.");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory).Replace(Path.DirectorySeparatorChar, '/');
            if (skip.Any(prefix => PluginRules.IsPathUnder(relative, prefix))) continue;
            Directory.CreateDirectory(InRoot(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            if (skip.Any(prefix => PluginRules.IsPathUnder(relative, prefix))) continue;
            DeploymentFiles.Copy(file, InRoot(destination, relative), mode);
        }
    }
    private static void SeedPersistentDefaults(DeploymentMapping[] mappings, LocalPluginStore.Package[] packages)
    {
        foreach (var mapping in mappings.Where(m => m.SeedDefaults && m.RuntimePath != "game/csgo/cfg"))
        {
            var packagePrefix = GameRelative(mapping.RuntimePath);
            foreach (var file in packages.SelectMany(p => p.Files))
            {
                if (!PluginRules.IsPathUnder(file.RelativePath, packagePrefix)) continue;
                var suffix = file.RelativePath[(packagePrefix.Length + 1)..];
                if (suffix.Length == 0) continue;
                var destination = InRoot(mapping.PersistentPath, suffix);
                if (!File.Exists(destination)) SeedFile(file.FullPath, destination);
            }
        }
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
        if (tx.FormatVersion is not (1 or 2) || tx.OperationId == Guid.Empty || tx.InstanceId != instanceId || tx.DataId != lease.Identity.DataId || tx.LibraryId != lease.Identity.LibraryId)
            throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "Unknown deployment journal; nothing was changed.");
        var target = Target(l, instanceId); var previous = Previous(l, tx); var stage = Stage(l, tx);
        LocalPaths.RejectLink(stage); LocalPaths.RejectLink(previous); EnsureStopped(target);
        var current = ValidateExisting(lease, instanceId, target);
        if (current?.OperationId == tx.OperationId) { File.Delete(journal); return "PublishedDeploymentConfirmed"; }
        if (tx.FormatVersion == 2)
            throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "Plugin sync was interrupted before publication. Inspect the preset plugin backups before clearing the deployment journal.");
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
