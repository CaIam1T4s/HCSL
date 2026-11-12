using System.Text.Json;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Moves managed sub-plugins that were imported before deps.json classification to the
/// framework directory declared by Core. Only packages whose complete file set is one
/// wrapper folder with a direct deps.json are considered; unknown or ambiguous packages
/// are left untouched.
/// </summary>
internal static class LocalPluginMigration
{
    internal static void Run(string packagesRoot)
    {
        LocalPaths.RejectLink(packagesRoot);
        if (!Directory.Exists(packagesRoot)) return;
        foreach (var idDirectory in Directory.EnumerateDirectories(packagesRoot))
        {
            try
            {
                var id = Path.GetFileName(idDirectory);
                PluginRules.Id(id);
                LocalPaths.RejectLink(idDirectory);
                foreach (var versionDirectory in Directory.EnumerateDirectories(idDirectory))
                {
                    try
                    {
                        LocalPaths.RejectLink(versionDirectory);
                        MigratePackage(packagesRoot, id, Path.GetFileName(versionDirectory));
                    }
                    catch (Exception e) when (e is InstanceException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
                    { }
                }
            }
            catch (Exception e) when (e is InstanceException or IOException or UnauthorizedAccessException or JsonException or OverflowException)
            { }
        }
    }

    private static void MigratePackage(string packagesRoot, string id, string version)
    {
        var package = LocalPluginStore.LoadPackage(packagesRoot, id, version);
        if (package.FrameworkPayload) return;
        foreach (var definition in FrameworkRules.Known)
        {
            if (definition.PluginRoot is not { } currentRoot || definition.PluginDataRoot is null) continue;
            var prefix = currentRoot + "/";
            var owned = package.Files.Where(file => PluginRules.IsPathUnder(file.RelativePath, currentRoot)).ToArray();
            if (owned.Length == 0 || owned.Length != package.Files.Length) continue;
            var first = owned[0].RelativePath[prefix.Length..];
            var separator = first.IndexOf('/');
            if (separator <= 0) continue;
            var folder = first[..separator];
            var folderPrefix = prefix + folder + "/";
            if (!owned.All(file => file.RelativePath.StartsWith(folderPrefix, StringComparison.Ordinal))) continue;
            var deps = owned.FirstOrDefault(file => file.RelativePath.StartsWith(folderPrefix, StringComparison.Ordinal)
                && file.RelativePath[folderPrefix.Length..].EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                && !file.RelativePath[folderPrefix.Length..].Contains('/'));
            if (deps is null) continue;
            var framework = LocalPluginImporter.ClassifyManagedDependencyFile(deps.FullPath).FrameworkId();
            if (framework is null) continue;
            var targetRoot = FrameworkRules.PluginRootFor(framework);
            if (targetRoot is null || string.Equals(targetRoot, currentRoot, StringComparison.Ordinal)) continue;
            var source = Path.Combine(package.Root, currentRoot.Replace('/', Path.DirectorySeparatorChar), folder);
            var destination = Path.Combine(package.Root, targetRoot.Replace('/', Path.DirectorySeparatorChar), folder);
            LocalPaths.RejectLink(source);
            if (File.Exists(destination) || Directory.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            return;
        }
    }
}
