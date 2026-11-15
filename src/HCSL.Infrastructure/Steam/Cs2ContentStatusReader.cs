using System.Text.RegularExpressions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Steam;

/// <summary>Read-only presence probe. Failed baseline checks or an interrupted update never mean that installed content is absent.</summary>
public static class Cs2ContentStatusReader
{
    public static Cs2ContentStatus Read(string libraryRoot)
    {
        if (!Path.IsPathFullyQualified(libraryRoot)) throw new IOException("Content library must be an absolute path.");
        var server = Path.Combine(libraryRoot, "server");
        string Checked(string relative)
        {
            LocalPaths.RejectLink(libraryRoot);
            LocalPaths.RejectLink(server);
            var path = server;
            foreach (var part in relative.Split('/'))
            {
                path = Path.Combine(path, part);
                LocalPaths.RejectLink(path);
            }
            return path;
        }
        static bool Nonempty(string path)
        {
            try
            {
                _ = File.GetAttributes(path); // Unlike File.Exists, access errors must not be reported as absence.
                return new FileInfo(path).Length > 0;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        var binary = Checked(OperatingSystem.IsWindows() ? "game/bin/win64/cs2.exe" : "game/bin/linuxsteamrt64/cs2");
        if (!Nonempty(binary)) return new(false, null);
        var manifest = Checked("steamapps/appmanifest_730.acf");
        if (!Nonempty(manifest)) return new(true, null);
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new IOException("CS2 app manifest exceeds the size limit.");
        var text = File.ReadAllText(manifest);
        var build = Regex.Match(text, "\"buildid\"\\s*\"([0-9]+)\"", RegexOptions.IgnoreCase);
        var isCs2 = Regex.IsMatch(text, "\"appid\"\\s*\"730\"", RegexOptions.IgnoreCase);
        return new(true, isCs2 && build.Success ? build.Groups[1].Value : null);
    }
}
