using HCSL.Application.Abstractions;

namespace HCSL.Infrastructure.Storage;

public static class LocalPaths
{
    internal static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string DiagnosticsDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HCSL", "logs")
        : Path.Combine(Defaults().DataRoot, "logs");

    public static WorkspaceRequest Defaults(string? settingsRoot = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return LocalWorkspaceManager.ConfiguredOrDefault(new(settingsRoot ?? Path.Combine(local, "HCSL", "config"),
                Path.Combine(local, "HCSL", "data"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HCSL-Library")));
        }
        if (!OperatingSystem.IsLinux())
            throw new WorkspaceException("HCSL-PLATFORM", "Only Windows and Linux are supported.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            // XDG variables must be absolute; ignore relative or empty values.
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : fallback;
        }
        var config = Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        var data = Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        // Large content is independent of XDG application data; existing settings keep their original library.
        return LocalWorkspaceManager.ConfiguredOrDefault(new(settingsRoot ?? Path.Combine(config, "hcsl"),
            Path.Combine(data, "hcsl"), Path.Combine(home, "HCSL-Library")));
    }

    public static string Canonicalize(string path) => Canonicalize(path, 0);

    private static string Canonicalize(string path, int depth)
    {
        if (depth > 40)
            throw new WorkspaceException("HCSL-PATH", "Too many directory links.");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new WorkspaceException("HCSL-PATH", "Paths must be absolute and nonempty.");
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(part => part == ".."))
            throw new WorkspaceException("HCSL-PATH", "Parent traversal (..) is not accepted.");
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var directory = new DirectoryInfo(next);
            if (directory.LinkTarget is not null)
            {
                var target = directory.ResolveLinkTarget(true);
                if (target is not DirectoryInfo || !target.Exists)
                    throw new WorkspaceException("HCSL-PATH", "Directory link is broken or not a directory: " + next);
                current = Canonicalize(target.FullName, depth + 1);
            }
            else
            {
                if (File.Exists(next))
                    throw new WorkspaceException("HCSL-PATH", "A path component is a file: " + next);
                current = next;
            }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    internal static bool Contains(string parent, string child) =>
        string.Equals(parent, child, Comparison) || child.StartsWith(
            Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, Comparison);

    internal static void ValidateRoots(params string[] roots)
    {
        for (int i = 0; i < roots.Length; i++)
        {
            if (string.Equals(roots[i], Path.GetPathRoot(roots[i]), Comparison))
                throw new WorkspaceException("HCSL-PATH", "A filesystem root cannot be managed.");
            if (roots[i].StartsWith(@"\\", StringComparison.Ordinal))
                throw new WorkspaceException("HCSL-FILESYSTEM", "Network paths are not supported.");
            var existing = roots[i];
            while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing)!;
            var drive = new DriveInfo(existing);
            if (drive.DriveType == DriveType.Network ||
                new[] { "nfs", "nfs4", "cifs", "smb", "smbfs", "9p", "fuse.sshfs" }.Contains(drive.DriveFormat))
                throw new WorkspaceException("HCSL-FILESYSTEM", "A local filesystem is required.");
            for (int j = 0; j < i; j++)
                if (Contains(roots[i], roots[j]) || Contains(roots[j], roots[i]))
                    throw new WorkspaceException("HCSL-PATH", "Settings, data and library roots must not overlap.");
        }
    }

    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null ||
            (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new WorkspaceException("HCSL-PATH", "Managed files and directories cannot be links: " + path);
    }

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static FileStream CreateFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows() && mode is FileMode.CreateNew or FileMode.OpenOrCreate)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
