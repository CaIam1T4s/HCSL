using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Validates package folders under &lt;library&gt;/plugin-packages and the preset's plugins.json.
/// Package files are game-root relative (for example csgo/addons/metamod/...). This store never
/// executes a plugin and never reads package contents into the UI.
/// </summary>
public sealed class LocalPluginStore : IPluginStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };
    private static readonly string[] Roots = ["csgo", "core", "csgo_lv"];
    /// <summary>Metadata signature → validated package. Package files never change while HCSL runs.</summary>
    private static readonly ConcurrentDictionary<string, (string Signature, Package Package)> Cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 512;

    internal sealed record PackageFile(string RelativePath, string FullPath, string Sha256, long Bytes);
    internal sealed record Package(string Id, string Name, string Version, string Root, PackageFile[] Files, string Hash, long Bytes, string? Framework, bool FrameworkPayload);

    public PluginResult Execute(WorkspaceRequest workspace, PluginCommand command)
    {
        if (!Enum.IsDefined(command.Action)
            || (command.Action != PluginAction.RemovePackage && (command.InstanceId == Guid.Empty || command.PresetId == Guid.Empty)))
            throw new InstanceException("HCSL-PLUGIN-REQUEST", "Plugin action and stable instance/preset IDs are required.");
        try
        {
            if (command.Action == PluginAction.RemovePackage)
            {
                using var removeLease = new LocalWorkspaceManager().Acquire(workspace);
                return RemovePackage(removeLease.Layout.InstancesRoot, Path.Combine(removeLease.Layout.LibraryRoot, "plugin-packages"), command);
            }
            if (command.Action == PluginAction.List)
            {
                var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
                LocalPaths.ValidateRoots(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot);
                var packagesRoot = Path.Combine(paths.LibraryRoot, "plugin-packages");
                // List must not contend with workspace writes. Legacy packages are migrated on save or deployment.
                return ExecuteRoot(Path.Combine(paths.DataRoot, "instances"), packagesRoot, command);
            }
            using var lease = new LocalWorkspaceManager().Acquire(workspace);
            if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
                throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before editing plugins.");
            var currentPackagesRoot = Path.Combine(lease.Layout.LibraryRoot, "plugin-packages");
            LocalPluginMigration.Run(currentPackagesRoot);
            return ExecuteRoot(lease.Layout.InstancesRoot, currentPackagesRoot, command);
        }
        catch (InstanceException) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { throw new InstanceException("HCSL-PLUGIN-IO", "Plugin storage failed; no file content is logged."); }
    }

    private static PluginResult RemovePackage(string instancesRoot, string packagesRoot, PluginCommand command)
    {
        var id = PluginRules.Id(command.RemoveId);
        var version = PluginRules.Version(command.RemoveVersion);
        var root = SafePath(Path.Combine(packagesRoot, id, version));
        if (!Directory.Exists(root)) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin package version is not installed.");
        if (Directory.EnumerateDirectories(instancesRoot).Any(instance =>
        {
            var presets = Path.Combine(instance, "presets");
            if (!Directory.Exists(presets)) return false;
            foreach (var preset in Directory.EnumerateDirectories(presets))
            {
                var file = Path.Combine(preset, "plugins.json");
                if (!File.Exists(file)) continue;
                try
                {
                    if (ReadManifest(file).Manifest.Plugins.Any(p => p.Id == id && p.Version == version)) return true;
                }
                catch (InstanceException) { return true; }
            }
            return false;
        })) throw new InstanceException("HCSL-PLUGIN-IN-USE", "The package version is referenced by a preset; remove it from those presets first.");
        SafePath(root);
        Directory.Delete(root, true);
        return new([], [], 0, "");
    }

    internal static Package LoadPackage(string packagesRoot, string id, string version)
    {
        PluginRules.Id(id); PluginRules.Version(version);
        var root = SafePath(Path.Combine(packagesRoot, id, version));
        var manifestPath = SafePath(Path.Combine(root, "plugin.json"));
        if (!File.Exists(manifestPath)) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin package manifest is missing.");
        if (new FileInfo(manifestPath).Length > PluginRules.MaxManifestBytes) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin manifest is too large.");
        var manifestText = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestText, Json)
            ?? throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin manifest is empty.");
        if (manifest.FormatVersion != 1 || manifest.Id != id || manifest.Version != version || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100 || manifest.Name.Any(char.IsControl))
            throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin manifest identity does not match its folder.");
        var filesRoot = SafeRelative(manifest.FilesRoot ?? "files");
        var source = SafePath(Path.Combine(root, filesRoot));
        if (!Directory.Exists(source)) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin files directory is missing.");
        var files = new List<PackageFile>();
        var signature = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        signature.AppendData(Encoding.UTF8.GetBytes(manifestText));
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            SafePath(file);
            var relative = PluginRules.RelativePath(Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'));
            var info = new FileInfo(file);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Package links are not accepted.");
            bytes = checked(bytes + info.Length);
            if (files.Count >= PluginRules.MaxPackageFiles || bytes > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Plugin package is too large.");
            // Content hashing a large package is expensive; metadata is enough to detect edits, adds and removals.
            signature.AppendData(Encoding.UTF8.GetBytes($"{relative}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\n"));
            files.Add(new(relative, file, "", info.Length));
        }
        var cacheKey = root;
        var stamp = Convert.ToHexString(signature.GetHashAndReset());
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.Signature == stamp) return cached.Package;
        for (var i = 0; i < files.Count; i++) files[i] = files[i] with { Sha256 = Hash(files[i].FullPath) };
        var hash = PackageHash(files);
        var framework = FrameworkRules.Resolve(files.Select(f => f.RelativePath));
        var package = new Package(id, manifest.Name, version, source, files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(), hash, bytes,
            framework, FrameworkRules.IsFrameworkPayload(framework, files.Select(f => f.RelativePath)));
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[cacheKey] = (stamp, package);
        return package;
    }

    private static PluginResult ExecuteRoot(string instancesRoot, string packagesRoot, PluginCommand c)
    {
        SafePath(instancesRoot);
        var instance = SafePath(Path.Combine(instancesRoot, c.InstanceId.ToString("D")));
        var entry = LocalInstanceStore.Load(instance, c.InstanceId);
        if (entry.Definition is null || entry.Presets.All(p => p.Id != c.PresetId))
            throw new InstanceException("HCSL-PLUGIN-PRESET", "The instance or preset no longer exists.");
        var preset = SafePath(Path.Combine(instance, "presets", c.PresetId.ToString("D")));
        var file = SafePath(Path.Combine(preset, "plugins.json"));
        var (manifest, hash) = ReadManifest(file);
        if (c.Action == PluginAction.List && c.FrameworkOverviewOnly)
        {
            var metadata = FrameworkMetadata(packagesRoot, manifest);
            return Result(manifest, hash, metadata, [], metadata.Length);
        }
        var (available, rejected) = Catalog(packagesRoot);
        var ordered = available.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Version, StringComparer.Ordinal).ToArray();
        var listed = ordered.Length > PluginRules.MaxListedPackages ? ordered[..PluginRules.MaxListedPackages] : ordered;
        if (c.Action == PluginAction.List) return Result(manifest, hash, listed, rejected, ordered.Length);
        if (entry.Definition.Revision != c.InstanceRevision)
            throw new InstanceException("HCSL-INSTANCE-STALE", "Instance revision changed. Refresh before editing plugins.");
        PluginRules.Validate(c.Plugins);
        var next = c.Plugins ?? [];
        foreach (var plugin in next.Where(p => p.Enabled))
            if (!ordered.Any(p => p.Id == plugin.Id && p.Version == plugin.Version))
                throw new InstanceException("HCSL-PLUGIN-PACKAGE", "An enabled plugin package is not available in this library.");
        FrameworkRules.ValidateSelections(next.Where(p => p.Enabled).Select(p =>
        {
            var package = ordered.First(a => a.Id == p.Id && a.Version == p.Version);
            return new FrameworkRules.SelectedFramework(p.Id, package.Framework, package.FrameworkPayload);
        }));
        var current = File.Exists(file) ? Hash(file) : null;
        if (!string.Equals(current, c.ExpectedHash, StringComparison.Ordinal))
            throw new InstanceException("HCSL-PLUGIN-CONFLICT", "Plugin manifest changed. Reload before saving; your draft was not written.");
        var saved = new PluginManifest(1, manifest.Revision + 1, next);
        Write(file, saved);
        var (written, writtenHash) = ReadManifest(file);
        return Result(written, writtenHash, listed, rejected, ordered.Length);
    }

    private static PluginResult Result(PluginManifest manifest, string hash, PluginPackageInfo[] available, PluginPackageRejection[] rejected, int total)
    {
        var selected = manifest.Plugins.Select(p =>
        {
            var package = available.FirstOrDefault(a => a.Id == p.Id && a.Version == p.Version);
            return new PluginSelectionInfo(p.Id, package?.Name, p.Version, p.Enabled, package is not null,
                package is null ? "HCSL-PLUGIN-PACKAGE" : null);
        }).ToArray();
        return new(available, selected, manifest.Revision, hash, rejected, total);
    }

    internal static PluginManifest ReadManifestForDeployment(string file) => ReadManifest(file).Manifest;

    private static (PluginManifest Manifest, string Hash) ReadManifest(string file)
    {
        SafePath(file);
        if (!File.Exists(file)) return (new(1, 0, []), "");
        if (new FileInfo(file).Length > PluginRules.MaxManifestBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Plugin manifest is too large.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Json)
            ?? throw new InstanceException("HCSL-PLUGIN-STATE", "Plugin manifest is empty.");
        if (manifest.FormatVersion != 1) throw new InstanceException("HCSL-PLUGIN-STATE", "Unknown plugin manifest format.");
        var plugins = manifest.Plugins ?? [];
        PluginRules.Validate(plugins);
        return (new(1, manifest.Revision, plugins), Hash(file));
    }

    // Overview only: trust the package identity metadata for display, not for enabling or deployment.
    private static PluginPackageInfo[] FrameworkMetadata(string packagesRoot, PluginManifest manifest)
    {
        var result = new List<PluginPackageInfo>();
        foreach (var framework in FrameworkRules.Known)
        {
            var folder = SafePath(Path.Combine(packagesRoot, framework.Id));
            if (!Directory.Exists(folder)) continue;
            var versions = Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>();
            var enabledVersion = manifest.Plugins.FirstOrDefault(p => p.Id == framework.Id && p.Enabled)?.Version;
            var latestFound = false;
            foreach (var version in FrameworkRules.OrderVersions(framework.Id, versions))
            {
                try
                {
                    if (latestFound && version != enabledVersion) continue;
                    PluginRules.Version(version);
                    var root = SafePath(Path.Combine(folder, version));
                    var file = new FileInfo(SafePath(Path.Combine(root, "plugin.json")));
                    if (!file.Exists || file.Length > PluginRules.MaxManifestBytes) continue;
                    var package = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(file.FullName), Json);
                    if (package is not { FormatVersion: 1 } || package.Id != framework.Id || package.Version != version ||
                        !Directory.Exists(SafePath(Path.Combine(root, SafeRelative(package.FilesRoot ?? "files"))))) continue;
                    result.Add(new(framework.Id, framework.DisplayName, version, "", 0, 0, framework.Id, true, PluginCategory.Framework));
                    latestFound = true;
                    if (enabledVersion is null || version == enabledVersion) break;
                }
                catch (Exception e) when (e is InstanceException or IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        return [.. result];
    }

    private static (PluginPackageInfo[] Available, PluginPackageRejection[] Rejected) Catalog(string packagesRoot)
    {
        SafePath(packagesRoot);
        if (!Directory.Exists(packagesRoot)) return ([], []);
        var result = new List<PluginPackageInfo>();
        var rejected = new List<PluginPackageRejection>();
        foreach (var plugin in Directory.EnumerateDirectories(packagesRoot))
        {
            if (!Directory.Exists(plugin)) continue;
            var id = Path.GetFileName(plugin);
            try { PluginRules.Id(id); }
            catch (InstanceException e)
            {
                rejected.Add(new(id, "", e.Code));
                continue;
            }
            foreach (var versionDir in Directory.EnumerateDirectories(plugin))
            {
                var version = Path.GetFileName(versionDir);
                try
                {
                    var package = LoadPackage(packagesRoot, id, version);
                    result.Add(new(package.Id, package.Name, package.Version, package.Hash, package.Files.Length, package.Bytes, package.Framework, package.FrameworkPayload, FrameworkRules.CategoryFor(package.Framework, package.FrameworkPayload)));
                }
                catch (InstanceException e) { rejected.Add(new(id, version, e.Code)); }
                catch (WorkspaceException e) { rejected.Add(new(id, version, e.Code)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException)
                { rejected.Add(new(id, version, "HCSL-PLUGIN-IO")); }
            }
        }
        return (result.ToArray(), rejected.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Version, StringComparer.Ordinal).ToArray());
    }

    private static string PackageHash(IEnumerable<PackageFile> files)
    {
        using var sha = SHA256.Create();
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var line = Encoding.UTF8.GetBytes(file.RelativePath + "\0" + file.Sha256 + "\0" + file.Bytes + "\n");
            sha.TransformBlock(line, 0, line.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Write(string file, PluginManifest manifest)
    {
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, manifest, Json); stream.Flush(true); }
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string SafePath(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            LocalPaths.RejectLink(item.FullName);
        return path;
    }

    private static string SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Any(char.IsControl))
            throw new InstanceException("HCSL-PLUGIN-PATH", "Invalid package directory.");
        var parts = path.Split('/');
        if (parts.Any(p => p is "" or "." or "..")) throw new InstanceException("HCSL-PLUGIN-PATH", "Invalid package directory.");
        return string.Join('/', parts);
    }

    private sealed record PackageManifest(int FormatVersion, string? Id, string? Name, string? Version, string? FilesRoot);
}
