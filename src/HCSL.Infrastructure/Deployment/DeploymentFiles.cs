using System.Runtime.InteropServices;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Deployment;

internal static class DeploymentFiles
{
    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, int source);
    internal static void Copy(string source, string destination, DeploymentCopyMode mode)
    {
        LocalPaths.RejectLink(source); LocalPaths.RejectLink(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            if (mode == DeploymentCopyMode.FullCopy) File.Copy(source, destination, false);
            else
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = LocalPaths.CreateFile(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (ioctl(output.SafeFileHandle.DangerousGetHandle().ToInt32(), 0x40049409, input.SafeFileHandle.DangerousGetHandle().ToInt32()) != 0)
                    throw new DeploymentException("HCSL-DEPLOY-REFLINK", "Filesystem cannot clone a source file. No full-copy fallback was attempted; use an explicit FullCopy plan if sufficient space is available.");
                output.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            if (new FileInfo(source).Length != new FileInfo(destination).Length)
                throw new DeploymentException("HCSL-DEPLOY-VERIFY", "Copied file length differs from its source.");
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }
    internal static void AtomicJson<T>(string path, T value)
    {
        LocalPaths.RejectLink(path);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { System.Text.Json.JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Tree(string root, Action<string> file, CancellationToken ct)
    {
        LocalPaths.RejectLink(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            ct.ThrowIfCancellationRequested(); LocalPaths.RejectLink(entry);
            if (Directory.Exists(entry)) Tree(entry, file, ct); else file(entry);
        }
    }
    internal static T Read<T>(string path)
    {
        LocalPaths.RejectLink(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new DeploymentException("HCSL-DEPLOY-STATE", "Deployment metadata is too large.");
        return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path))
            ?? throw new DeploymentException("HCSL-DEPLOY-STATE", "Deployment metadata is empty.");
    }
}
