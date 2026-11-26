using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HCSL.Application.Abstractions;

namespace HCSL.Infrastructure.Storage;

/// <summary>Single-user local workspace ownership. Does not start or supervise child processes.</summary>
public sealed partial class LocalWorkspaceManager : IWorkspaceManager
{
    private const string LockName = ".hcsl-management.lock";
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Marker))]
    [JsonSerializable(typeof(Settings))]
    private sealed partial class WorkspaceJsonContext : JsonSerializerContext;

    private static JsonTypeInfo<T> JsonType<T>() =>
        WorkspaceJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"Unsupported workspace file type: {typeof(T)}.");
    private sealed record Marker(int FormatVersion, string Product, string Kind, Guid Id);
    private sealed record Settings(int FormatVersion, string DataRoot, string LibraryRoot, Guid DataId, Guid LibraryId);

    // Read-only default resolution. Acquire still rechecks paths/identities under the management leases.
    internal static WorkspaceRequest ConfiguredOrDefault(WorkspaceRequest defaults)
    {
        var config = LocalPaths.Canonicalize(defaults.SettingsRoot);
        var path = Path.Combine(config, "settings.json");
        try
        {
            LocalPaths.RejectLink(path);
            if (!File.Exists(path)) return defaults with { SettingsRoot = config };
            var saved = ReadJson<Settings>(path);
            if (saved.FormatVersion != 1 || saved.DataId == Guid.Empty || saved.LibraryId == Guid.Empty)
                throw new WorkspaceException("HCSL-FORMAT", "Unknown or invalid saved workspace settings.");
            var data = LocalPaths.Canonicalize(saved.DataRoot);
            var library = LocalPaths.Canonicalize(saved.LibraryRoot);
            LocalPaths.ValidateRoots(config, data, library);
            foreach (var marker in new[] { Path.Combine(config, "workspace.json"),
                Path.Combine(data, "workspace.json"), Path.Combine(library, "library.json") })
            {
                LocalPaths.RejectLink(marker);
                if (!File.Exists(marker)) throw new WorkspaceException("HCSL-MIGRATION", "A configured workspace is missing; select or restore it instead of creating a replacement.");
            }
            ReadMarker(config, "settings", "workspace.json");
            if (ReadMarker(data, "data", "workspace.json").Id != saved.DataId ||
                ReadMarker(library, "library", "library.json").Id != saved.LibraryId)
                throw new WorkspaceException("HCSL-MIGRATION", "Configured workspace identities differ; original settings were preserved.");
            return new(config, data, library);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException("HCSL-STORAGE", "Cannot read saved workspace settings.", ex);
        }
    }

    public IWorkspaceLease Acquire(WorkspaceRequest request) => Acquire(request, false);
    internal IWorkspaceLease AcquireForServerRecovery(WorkspaceRequest request) => Acquire(request, true);
    private IWorkspaceLease Acquire(WorkspaceRequest request, bool serverRecovery)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new WorkspaceException("HCSL-PLATFORM", "Only Windows and Linux are supported.");
        var locks = new List<FileStream>();
        try
        {
            var config = LocalPaths.Canonicalize(request.SettingsRoot);
            var data = LocalPaths.Canonicalize(request.DataRoot);
            var library = LocalPaths.Canonicalize(request.LibraryRoot);
            LocalPaths.ValidateRoots(config, data, library);
            var roots = new[] { (Path: config, Kind: "settings", Marker: "workspace.json"),
                (Path: data, Kind: "data", Marker: "workspace.json"),
                (Path: library, Kind: "library", Marker: "library.json") };
            // Read-only preflight: do not drop a lock file into an arbitrary existing directory.
            foreach (var root in roots) Preflight(root.Path, root.Kind, root.Marker);
            // Deterministic order; fail immediately rather than waiting while holding other locks.
            foreach (var root in roots.OrderBy(r => r.Path, OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                LocalPaths.CreatePrivateDirectory(root.Path);
                var lockPath = Path.Combine(root.Path, LockName);
                LocalPaths.RejectLink(lockPath);
                try
                {
                    locks.Add(LocalPaths.CreateFile(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex)
                {
                    throw new WorkspaceException("HCSL-LOCK", "Cannot acquire management lock (busy or I/O failure): " + root.Path, ex);
                }
            }
            // Another process could have initialized between preflight and acquiring the locks.
            foreach (var root in roots) Preflight(root.Path, root.Kind, root.Marker, hasLease: true);
            var markers = roots.Select(r => ReadMarker(r.Path, r.Kind, r.Marker)).ToArray();
            var settingsPath = Path.Combine(config, "settings.json");
            LocalPaths.RejectLink(settingsPath);
            if (File.Exists(settingsPath))
            {
                var saved = ReadJson<Settings>(settingsPath);
                if (saved.FormatVersion != 1 || !string.Equals(saved.DataRoot, data, LocalPaths.Comparison) ||
                    !string.Equals(saved.LibraryRoot, library, LocalPaths.Comparison) ||
                    saved.DataId != markers[1].Id || saved.LibraryId != markers[2].Id)
                    throw new WorkspaceException("HCSL-MIGRATION", "Stored paths/identities differ; migration is not implemented. Original settings were preserved.");
            }
            var children = new[] { Path.Combine(data, "steamcmd"), Path.Combine(data, "instances"),
                Path.Combine(library, "server"), Path.Combine(library, "runtime"),
                Path.Combine(library, ".hcsl"), Path.Combine(library, ".hcsl", "locks"),
                Path.Combine(data, "operations"), Path.Combine(library, "plugin-packages") };
            foreach (var child in children)
            {
                LocalPaths.RejectLink(child);
                if (File.Exists(child)) throw new WorkspaceException("HCSL-PATH", "Expected a directory: " + child);
            }
            var capacity = new WorkspaceCapacity(Probe(data), Probe(library));
            Probe(config);
            // Publish ownership before child creation, making interrupted initialization retryable.
            for (int i = 0; i < roots.Length; i++)
            {
                var path = Path.Combine(roots[i].Path, roots[i].Marker);
                if (!File.Exists(path)) AtomicWrite(path, markers[i]);
            }
            foreach (var child in children) LocalPaths.CreatePrivateDirectory(child);
            if (!File.Exists(settingsPath))
                AtomicWrite(settingsPath, new Settings(1, data, library, markers[1].Id, markers[2].Id));
            if (!serverRecovery)
            {
                var session = Path.Combine(library, ".hcsl", "server-session.json");
                LocalPaths.RejectLink(session);
                if (File.Exists(session) || Directory.EnumerateFiles(Path.GetDirectoryName(session)!, "server-session.json.tmp-*").Any())
                    throw new WorkspaceException("HCSL-SERVER-RECOVERY", "An unfinished server session must be recovered before changing this workspace.");
                foreach (var runtime in Directory.EnumerateDirectories(children[3]))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(runtime), "D", out _)) continue;
                    LocalPaths.RejectLink(runtime);
                    var running = Path.Combine(runtime, ".hcsl-running.json"); LocalPaths.RejectLink(running);
                    if (File.Exists(running) || Directory.EnumerateFiles(runtime, ".hcsl-running.json.tmp-*").Any()) throw new WorkspaceException("HCSL-SERVER-RECOVERY", "Runtime ownership marker requires server recovery.");
                }
            }
            return new Lease(new(config, data, library, children[0], children[1], children[2], children[3]),
                new(markers[0].Id, markers[1].Id, markers[2].Id), capacity, locks);
        }
        catch (Exception ex)
        {
            for (int i = locks.Count - 1; i >= 0; i--) locks[i].Dispose();
            if (ex is WorkspaceException) throw;
            if (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                throw new WorkspaceException("HCSL-STORAGE", "Workspace initialization failed: " + ex.Message, ex);
            throw;
        }
    }

    private static void Preflight(string root, string kind, string markerName, bool hasLease = false)
    {
        if (!Directory.Exists(root)) return;
        var marker = Path.Combine(root, markerName);
        LocalPaths.RejectLink(marker);
        var lockPath = Path.Combine(root, LockName);
        LocalPaths.RejectLink(lockPath);
        if (!hasLease) CheckExistingLock(lockPath);
        if (File.Exists(marker))
        {
            ReadMarker(root, kind, markerName);
            return;
        }
        // Only an empty root or our stable bootstrap lock is eligible for first initialization.
        if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != LockName))
        {
            // An initializer may have published a temporary file after our first check.
            if (!hasLease) CheckExistingLock(lockPath);
            throw new WorkspaceException("HCSL-UNOWNED", "Refusing to take over an unmarked nonempty directory: " + root);
        }
    }

    private static void CheckExistingLock(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            // Open only: this read-only preflight never creates or modifies the lock file.
            using var probe = LocalPaths.CreateFile(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new WorkspaceException("HCSL-LOCK", "Cannot acquire management lock (busy or I/O failure): " + path, ex);
        }
    }

    private static Marker ReadMarker(string root, string kind, string markerName)
    {
        var path = Path.Combine(root, markerName);
        if (!File.Exists(path)) return new Marker(1, "HCSL", kind, Guid.NewGuid());
        var marker = ReadJson<Marker>(path);
        if (marker.FormatVersion != 1 || marker.Product != "HCSL" || marker.Kind != kind || marker.Id == Guid.Empty)
            throw new WorkspaceException("HCSL-FORMAT", "Unknown or invalid workspace marker: " + path);
        return marker;
    }

    private static T ReadJson<T>(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024)
                throw new WorkspaceException("HCSL-FORMAT", "Workspace metadata exceeds its size limit.");
            return JsonSerializer.Deserialize(File.ReadAllText(path), JsonType<T>())
                ?? throw new WorkspaceException("HCSL-FORMAT", "Empty JSON document: " + path);
        }
        catch (JsonException ex)
        {
            throw new WorkspaceException("HCSL-FORMAT", "Invalid JSON document: " + path, ex);
        }
    }

    private static long Probe(string root)
    {
        var space = new DriveInfo(root).AvailableFreeSpace;
        if (space < 64 * 1024)
            throw new WorkspaceException("HCSL-SPACE", "Insufficient space for workspace initialization: " + root);
        var first = Path.Combine(root, ".hcsl-probe-" + Guid.NewGuid().ToString("N"));
        var second = first + ".renamed";
        try
        {
            using (var stream = LocalPaths.CreateFile(first, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush(true);
            }
            File.Move(first, second);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
        return space;
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".hcsl-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = LocalPaths.CreateFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonType<T>());
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private sealed class Lease(WorkspaceLayout layout, WorkspaceIdentity identity, WorkspaceCapacity capacity,
        List<FileStream> locks) : IWorkspaceLease
    {
        public WorkspaceLayout Layout { get; } = layout;
        public WorkspaceIdentity Identity { get; } = identity;
        public WorkspaceCapacity Capacity { get; } = capacity;
        public void Dispose()
        {
            for (int i = locks.Count - 1; i >= 0; i--) locks[i].Dispose();
        }
    }
}
