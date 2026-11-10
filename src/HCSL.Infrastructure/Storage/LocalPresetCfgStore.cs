using HCSL.Application.Abstractions;
using System.Security.Cryptography;
using System.Text;
using HCSL.Core;
using HCSL.Infrastructure.Server;
namespace HCSL.Infrastructure.Storage;

/// <summary>Flat preset CFG files only. Writes share the server/SteamCMD management lease.</summary>
public sealed class LocalPresetCfgStore : IPresetCfgStore
{
    public PresetCfgResult Execute(WorkspaceRequest workspace, PresetCfgCommand command)
    {
        if (!Enum.IsDefined(command.Action) || command.InstanceId == Guid.Empty || command.PresetId == Guid.Empty)
            throw new InstanceException("HCSL-CFG-REQUEST", "CFG action and stable instance/preset IDs are required.");
        try
        {
            if (command.Action is PresetCfgAction.List or PresetCfgAction.Read)
            {
                var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
                LocalPaths.ValidateRoots(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot);
                return ExecuteRoot(Path.Combine(paths.DataRoot, "instances"), command);
            }
            using var lease = new LocalWorkspaceManager().Acquire(workspace);
            if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
                throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before editing CFG.");
            return ExecuteRoot(lease.Layout.InstancesRoot, command);
        }
        catch (InstanceException) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { throw new InstanceException("HCSL-CFG-IO", "CFG access failed; check file existence, UTF-8 encoding and permissions. No file content is logged."); }
    }
    private static void Safe(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            LocalPaths.RejectLink(item.FullName);
    }
    private static PresetCfgResult ExecuteRoot(string root, PresetCfgCommand c)
    {
        Safe(root);
        var instance = Path.Combine(root, c.InstanceId.ToString("D"));
        Safe(instance);
        var entry = LocalInstanceStore.Load(instance, c.InstanceId);
        if (entry.Definition is null || entry.Presets.All(p => p.Id != c.PresetId))
            throw new InstanceException("HCSL-CFG-PRESET", "The instance or preset no longer exists.");
        var write = c.Action is not (PresetCfgAction.List or PresetCfgAction.Read);
        if (write && entry.Definition.Revision != c.InstanceRevision)
            throw new InstanceException("HCSL-INSTANCE-STALE", "Instance revision changed. Refresh before editing CFG.");
        var preset = Path.Combine(instance, "presets", c.PresetId.ToString("D"));
        var directory = Path.Combine(preset, "cfg"); Safe(directory);
        if (!Directory.Exists(directory)) throw new InstanceException("HCSL-CFG-DIRECTORY", "Preset CFG directory is missing.");
        if (c.Action == PresetCfgAction.List) return List(directory, c.Path);
        if (c.Action == PresetCfgAction.Read)
        {
            var relative = PresetCfgRules.Path(c.Name, requireCfgFile: true);
            var readFile = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)); Safe(readFile);
            var bytes = Read(readFile); var text = Decode(bytes); PresetCfgRules.Content(text);
            return new([], relative, text, Hash(bytes));
        }
        var name = PresetCfgRules.Name(c.Name);
        var file = Path.Combine(directory, name); Safe(file);
        if (c.Action == PresetCfgAction.Save)
        {
            PresetCfgRules.Content(c.Content);
            CheckVersion(file, c.ExpectedHash);
            if (!File.Exists(file) && CountFiles(directory) >= PresetCfgRules.MaxFiles)
                throw new InstanceException("HCSL-CFG-LIMIT", "CFG file limit reached.");
            var temp = Path.Combine(directory, ".cfg-tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false, true).GetBytes(c.Content!);
                    stream.Write(bytes); stream.Flush(true);
                }
                CheckVersion(file, c.ExpectedHash);
                File.Move(temp, file, overwrite: c.ExpectedHash is not null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return new([], name, c.Content, Hash(Read(file)));
        }
        CheckVersion(file, c.ExpectedHash, requireExisting: true);
        if (c.Action == PresetCfgAction.Rename)
        {
            var target = Path.Combine(directory, PresetCfgRules.Name(c.NewName)); Safe(target);
            if (Path.Exists(target)) throw new InstanceException("HCSL-CFG-EXISTS", "Rename target already exists.");
            File.Move(file, target);
        }
        else
        {
            var trash = Path.Combine(preset, ".cfg-trash"); Safe(trash);
            LocalPaths.CreatePrivateDirectory(trash);
            File.Move(file, Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + name));
        }
        return new([]);
    }
    private static PresetCfgResult List(string directory, string? relativePath)
    {
        var relative = PresetCfgRules.Path(relativePath, requireCfgFile: false);
        var target = relative.Length == 0 ? directory : Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Safe(target);
        if (!Directory.Exists(target)) throw new InstanceException("HCSL-CFG-DIRECTORY", "CFG directory does not exist.");
        var result = new List<PresetCfgFile>();
        var total = 0;
        foreach (var path in Directory.EnumerateDirectories(target).Order(StringComparer.Ordinal))
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var child = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
            try { PresetCfgRules.Path(child, requireCfgFile: false); } catch (InstanceException) { continue; }
            total++;
            if (result.Count >= PresetCfgRules.MaxListedEntries) continue;
            result.Add(new(info.Name, 0, PresetCfgKind.Directory, child));
        }
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            try { PresetCfgRules.Name(info.Name); } catch (InstanceException) { continue; }
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var child = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
            try { PresetCfgRules.Path(child, requireCfgFile: true); } catch (InstanceException) { continue; }
            total++;
            if (result.Count >= PresetCfgRules.MaxListedEntries) continue;
            result.Add(new(info.Name, info.Length, PresetCfgKind.File, child));
        }
        var parent = relative.Length == 0 ? null : (relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "");
        return new([.. result], Path: relative, Parent: parent, Truncated: total > result.Count, Total: total);
    }

    private static int CountFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cfg", SearchOption.TopDirectoryOnly).Count();

    private static byte[] Read(string file)
    {
        Safe(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > PresetCfgRules.MaxBytes) throw new InstanceException("HCSL-CFG-LIMIT", "File exceeds the 8 KiB editor limit; use an external editor.");
        using var output = new MemoryStream();
        var buffer = new byte[1024]; int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (output.Length + read > PresetCfgRules.MaxBytes) throw new InstanceException("HCSL-CFG-LIMIT", "CFG grew beyond the editor limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private static string Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsSpan(3) : bytes.AsSpan());
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void CheckVersion(string file, string? expected, bool requireExisting = false)
    {
        Safe(file);
        if (expected is null)
        {
            if (requireExisting || Path.Exists(file)) throw new InstanceException("HCSL-CFG-CONFLICT", "File already exists or no version was loaded.");
        }
        else if (!File.Exists(file) || !string.Equals(Hash(Read(file)), expected, StringComparison.Ordinal))
            throw new InstanceException("HCSL-CFG-CONFLICT", "File changed or was removed. Reload before writing; your draft was not saved.");
    }
}
