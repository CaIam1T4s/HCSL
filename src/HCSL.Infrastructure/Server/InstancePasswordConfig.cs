using System.Text;
using HCSL.Core;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Server;

/// <summary>Instance-owned, Linux-private CS2 config; never part of preset metadata.</summary>
internal static class InstancePasswordConfig
{
    internal const string ExecName = "hcsl-instance-password.cfg";
    private const string Prefix = "// HCSL managed instance password\nexec server.cfg\nsv_password \"";
    private const string Suffix = "\"\n";
    private static string FilePath(string instance) => Path.Combine(instance, "private", "server-password.cfg");
    private static void CheckPaths(string instance)
    {
        LocalPaths.RejectLink(instance);
        LocalPaths.RejectLink(Path.Combine(instance, "private"));
        LocalPaths.RejectLink(FilePath(instance));
    }
    internal static string Read(string instance)
    {
        CheckPaths(instance);
        var path = FilePath(instance);
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 1024) throw Invalid();
        const UnixFileMode publicAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if (OperatingSystem.IsLinux() && ((File.GetUnixFileMode(path) | File.GetUnixFileMode(Path.GetDirectoryName(path)!)) & publicAccess) != 0)
            throw new InstanceException("HCSL-INSTANCE-PASSWORD", "Password config permissions must be owner-only (0600).");
        var text = File.ReadAllText(path);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || !text.EndsWith(Suffix, StringComparison.Ordinal)) throw Invalid();
        var password = text[Prefix.Length..^Suffix.Length];
        InstanceRules.Password(password);
        return password;
    }
    private static InstanceException Invalid() => new("HCSL-INSTANCE-PASSWORD", "Managed password config is invalid; original file preserved.");
    internal static void Write(string instance, string password)
    {
        InstanceRules.Password(password);
        if (!OperatingSystem.IsLinux()) throw new InstanceException("HCSL-INSTANCE-PASSWORD-PLATFORM", "Server password storage currently requires Linux private file permissions.");
        CheckPaths(instance);
        var directory = Path.Combine(instance, "private");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = FilePath(instance);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                stream.Write(Encoding.UTF8.GetBytes(Prefix + password + Suffix));
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static Binding Bind(string instance, string presetId)
    {
        var secret = Read(instance);
        if (!File.Exists(FilePath(instance))) Write(instance, "");
        var cfg = Path.Combine(instance, "presets", presetId, "cfg");
        LocalPaths.RejectLink(Path.Combine(instance, "presets"));
        LocalPaths.RejectLink(Path.Combine(instance, "presets", presetId));
        LocalPaths.RejectLink(cfg);
        var link = Path.Combine(cfg, ExecName);
        var target = FilePath(instance);
        var old = new FileInfo(link);
        if (old.LinkTarget is { } existing)
        {
            if (existing != target) throw Invalid();
            File.Delete(link); // residue from a crashed run, only our exact target is accepted
        }
        else if (File.Exists(link) || Directory.Exists(link)) throw Invalid();
        File.CreateSymbolicLink(link, target);
        return new Binding(link, target, secret);
    }
    internal sealed class Binding(string link, string target, string secret) : IDisposable
    {
        internal string Redact(string text) => secret.Length == 0 ? text : text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        public void Dispose()
        {
            if (new FileInfo(link).LinkTarget == target) File.Delete(link);
        }
    }
}
