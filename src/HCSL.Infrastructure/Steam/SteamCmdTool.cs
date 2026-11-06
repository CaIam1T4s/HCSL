using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Steam;

internal static class SteamCmdTool
{
    // Valve's public bootstrap distributions; never download through an arbitrary supplied URL.
    private const string LinuxUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    private const string WindowsUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const long MaxArchiveBytes = 64 * 1024 * 1024;
    private const long MaxExtractedBytes = 256 * 1024 * 1024;

    internal static string Locate(WorkspaceLayout layout, string? specified)
    {
        if (RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.X86))
            throw new SteamCmdException("HCSL-STEAMCMD-PLATFORM", "SteamCMD bootstrap supports x86/x64 hosts only.");
        if (specified is null)
            return Path.Combine(layout.SteamCmdRoot, "tool", OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
        if (!Path.IsPathFullyQualified(specified))
            throw new SteamCmdException("HCSL-STEAMCMD-PATH", "Executable path must be absolute.");
        var file = new FileInfo(specified);
        if (!file.Exists) throw new SteamCmdException("HCSL-STEAMCMD-MISSING", "Specified SteamCMD executable does not exist.");
        var resolved = file.ResolveLinkTarget(true)?.FullName ?? file.FullName;
        var directory = LocalPaths.Canonicalize(Path.GetDirectoryName(resolved)!);
        LocalPaths.ValidateRoots(directory);
        return Path.Combine(directory, Path.GetFileName(resolved));
    }

    internal static void Validate(string executable)
    {
        if (!File.Exists(executable))
            throw new SteamCmdException("HCSL-STEAMCMD-MISSING", "SteamCMD is not installed. Supply an executable or use --bootstrap.");
        if (OperatingSystem.IsLinux())
        {
            if ((File.GetUnixFileMode(executable) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                throw new SteamCmdException("HCSL-STEAMCMD-DEPENDENCY", "SteamCMD executable is missing execute permission.");
            if (!File.Exists("/usr/bin/setsid") && !File.Exists("/bin/setsid"))
                throw new SteamCmdException("HCSL-STEAMCMD-DEPENDENCY", "Linux requires setsid (util-linux) for process supervision.");
            // Official Linux bootstrap binary is 32-bit ELF, even on x64 hosts.
            var binary = Path.Combine(Path.GetDirectoryName(executable)!, "linux32", "steamcmd");
            if (File.Exists(binary))
            {
                using var stream = File.OpenRead(binary);
                Span<byte> magic = stackalloc byte[5];
                if (stream.Read(magic) == 5 && magic[0] == 0x7f && magic[1] == 'E' && magic[4] == 1 &&
                    !File.Exists("/lib/ld-linux.so.2") && !File.Exists("/lib32/ld-linux.so.2"))
                    throw new SteamCmdException("HCSL-STEAMCMD-DEPENDENCY", "32-bit glibc loader is missing. Install your distribution's SteamCMD runtime dependencies.");
            }
        }
    }

    internal static async Task InstallAsync(string executable, HttpClient client, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(executable)!;
        // The caller created this directory and holds its session lock. Do not merge into an unknown install.
        if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != ".hcsl-session.lock"))
            throw new SteamCmdException("HCSL-STEAMCMD-INSTALL", "Bootstrap target is not empty; refusing to overwrite it.");
        var staging = Path.Combine(Path.GetDirectoryName(directory)!, ".steamcmd-staging-" + Guid.NewGuid().ToString("N"));
        LocalPaths.CreatePrivateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "archive");
            using var response = await client.GetAsync(OperatingSystem.IsWindows() ? WindowsUrl : LinuxUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                throw new SteamCmdException("HCSL-STEAMCMD-INSTALL", "Bootstrap archive exceeds size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = LocalPaths.CreateFile(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(input, output, MaxArchiveBytes, ct);
            var extracted = Path.Combine(staging, "extracted");
            LocalPaths.CreatePrivateDirectory(extracted);
            await ExtractAsync(archive, extracted, ct);
            if (!File.Exists(Path.Combine(extracted, Path.GetFileName(executable))))
                throw new SteamCmdException("HCSL-STEAMCMD-INSTALL", "Bootstrap archive lacks the expected launcher.");
            // Journal incomplete publication so interrupted installs cannot be silently executed.
            var installing = Path.Combine(directory, ".hcsl-installing");
            File.WriteAllText(installing, "Incomplete bootstrap. Remove/reinstall only after inspecting this directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(extracted))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(directory, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }
            File.Delete(installing);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    // Bounded, regular-file-only extraction. Paths, links, devices and excessive expansion are rejected.
    internal static async Task ExtractAsync(string archive, string root, CancellationToken ct)
    {
        long remaining = MaxExtractedBytes;
        int count = 0;
        string Destination(string name)
        {
            if (++count > 10000 || name.Contains('\\') || name.Split('/').Any(p => p == "..") || Path.IsPathRooted(name))
                throw new SteamCmdException("HCSL-STEAMCMD-ARCHIVE", "Unsafe archive entry.");
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!LocalPaths.Contains(root, path) || path == root)
                throw new SteamCmdException("HCSL-STEAMCMD-ARCHIVE", "Archive path escapes extraction directory.");
            return path;
        }
        if (OperatingSystem.IsWindows())
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = Destination(entry.FullName);
                var type = (entry.ExternalAttributes >> 16) & 0xF000;
                if (type is not (0 or 0x8000 or 0x4000))
                    throw new SteamCmdException("HCSL-STEAMCMD-ARCHIVE", "Archive links/special files are unsupported.");
                if (entry.FullName.EndsWith('/')) { LocalPaths.CreatePrivateDirectory(path); continue; }
                LocalPaths.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
                await using var input = entry.Open();
                await using var output = LocalPaths.CreateFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                remaining -= await CopyBoundedAsync(input, output, remaining, ct);
            }
        }
        else
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                if (entry.Name is "." or "./" && entry.EntryType == TarEntryType.Directory)
                {
                    if (++count > 10000)
                        throw new SteamCmdException("HCSL-STEAMCMD-ARCHIVE", "Too many archive entries.");
                    continue;
                }
                var path = Destination(entry.Name);
                if (entry.EntryType == TarEntryType.Directory) { LocalPaths.CreatePrivateDirectory(path); continue; }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new SteamCmdException("HCSL-STEAMCMD-ARCHIVE", "Archive links/special files are unsupported.");
                LocalPaths.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = LocalPaths.CreateFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    remaining -= await CopyBoundedAsync(entry.DataStream!, output, remaining, ct);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    (entry.Mode & UnixFileMode.UserExecute));
            }
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > limit) throw new SteamCmdException("HCSL-STEAMCMD-ARCHIVE", "Download or extracted data exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }
}
