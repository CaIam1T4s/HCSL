using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Frameworks;

/// <summary>
/// Downloads only the closed <see cref="FrameworkRules.Known"/> catalog from official GitHub releases.
/// Every archive must match the release digest before the normal plugin importer accepts it.
/// </summary>
public sealed class FrameworkManager : IFrameworkManager
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ReleaseInfo[] Releases)> cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly HttpMessageHandler? handler;
    private readonly TimeProvider clock;

    public FrameworkManager(HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        this.handler = handler;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<FrameworkCheckResult> CheckAsync(WorkspaceRequest workspace, string? frameworkId = null, CancellationToken ct = default)
    {
        var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        var packagesRoot = Path.Combine(paths.LibraryRoot, "plugin-packages");
        var results = new List<FrameworkVersionInfo>(FrameworkRules.Known.Length);
        var catalog = frameworkId is null ? FrameworkRules.Known : [FrameworkRules.Find(frameworkId)
            ?? throw new InstanceException("HCSL-FRAMEWORK-ID", "Unknown framework.")];
        foreach (var framework in catalog)
        {
            var installed = InstalledVersion(packagesRoot, framework.Id);
            if (!framework.Installable)
            {
                results.Add(new(framework.Id, framework.DisplayName, framework.Kind == FrameworkKind.Base, false,
                    installed, null, null, false, framework.Requires, null));
                continue;
            }
            try
            {
                var releases = await Releases(framework, ct);
                var latest = Latest(framework, releases);
                if (latest is null)
                {
                    results.Add(new(framework.Id, framework.DisplayName, framework.Kind == FrameworkKind.Base, true,
                        installed, null, null, false, framework.Requires, "HCSL-FRAMEWORK-RELEASE"));
                    continue;
                }
                var latestVersion = latest.Value.Version;
                var latestAsset = latest.Value.Asset;
                var update = installed is not null
                    && FrameworkRules.CompareVersions(framework.Id, latestVersion, installed) > 0;
                results.Add(new(framework.Id, framework.DisplayName, framework.Kind == FrameworkKind.Base, true,
                    installed, latestVersion, latestAsset, update, framework.Requires, null));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                results.Add(new(framework.Id, framework.DisplayName, framework.Kind == FrameworkKind.Base, true,
                    installed, null, null, false, framework.Requires, "HCSL-FRAMEWORK-NET"));
            }
            catch (InstanceException e)
            {
                results.Add(new(framework.Id, framework.DisplayName, framework.Kind == FrameworkKind.Base, true,
                    installed, null, null, false, framework.Requires, e.Code));
            }
        }
        return new([.. results]);
    }

    public async Task<FrameworkInstallResult> InstallAsync(WorkspaceRequest workspace, FrameworkInstallCommand command, CancellationToken ct = default)
    {
        var framework = FrameworkRules.Find(command.Id)
            ?? throw new InstanceException("HCSL-FRAMEWORK-ID", "Unknown framework; only the built-in catalog can be installed.");
        if (!framework.Installable)
            throw new InstanceException("HCSL-FRAMEWORK-ID", framework.DisplayName + " has no installer in this build; import its release manually.");
        var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        var releases = await Releases(framework, ct);
        var chosen = Select(framework, releases, command.Version);
        if (chosen is null)
            throw new InstanceException("HCSL-FRAMEWORK-RELEASE", "No matching release/asset was found for this platform.");
        var (release, asset, version) = chosen.Value;
        var warnings = new List<string>();
        var runtime = command.WithRuntime && !FrameworkRules.IsBase(framework.Id);
        if (runtime && FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), true) is null)
        {
            runtime = false;
            warnings.Add("This release has no with-runtime asset; the server host must provide the .NET runtime.");
        }
        var assetName = FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), runtime)
            ?? throw new InstanceException("HCSL-FRAMEWORK-ASSET", "No asset matches this platform (" + PlatformName + ").");
        var file = release.Assets.First(a => a.Name == assetName);
        if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            && !assetName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            throw new InstanceException("HCSL-FRAMEWORK-ASSET", "Unsupported archive type: " + assetName);
        if (file.Size is <= 0 or > FrameworkRules.MaxDownloadBytes)
            throw new InstanceException("HCSL-FRAMEWORK-LIMIT", "Release asset size is outside the accepted range.");
        if (string.IsNullOrWhiteSpace(file.Digest) || !file.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InstanceException("HCSL-FRAMEWORK-HASH", "The release has no SHA-256 digest; refusing an unverifiable download.");

        var downloadRoot = Path.Combine(paths.LibraryRoot, ".hcsl", "framework-downloads");
        Directory.CreateDirectory(downloadRoot);
        LocalPaths.RejectLink(downloadRoot);
        var extension = assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(assetName);
        var temp = Path.Combine(downloadRoot, framework.Id + "-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            var actual = await Download(file.Url, temp, file.Size, ct);
            var expected = file.Digest["sha256:".Length..].Trim();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InstanceException("HCSL-FRAMEWORK-HASH", "Downloaded file hash does not match the release digest.");
            if (!FrameworkRules.IsBase(framework.Id) && InstalledVersion(Path.Combine(paths.LibraryRoot, "plugin-packages"), framework.Requires!) is null
                && framework.Requires is not null)
                warnings.Add(framework.DisplayName + " requires " + framework.Requires + "; install it as well or the server will not load this framework.");
            var result = new LocalPluginImporter().Execute(workspace, new(Guid.Empty, Guid.Empty, temp, framework.Id, framework.DisplayName,
                version, null, Enable: false, Force: true, ArchivePath: temp));
            warnings.AddRange(result.Warnings);
            return new(framework.Id, result.Version, result.PackagePath, result.Files, result.Bytes, actual, true, [.. warnings.Distinct(StringComparer.Ordinal)]);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private async Task<string> Download(string url, string path, long expectedSize, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("github.com" or "objects.githubusercontent.com" or "api.github.com" or "raw.githubusercontent.com"))
            throw new InstanceException("HCSL-FRAMEWORK-NET", "Release asset URL is not an approved HTTPS host.");
        using var client = Client();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && finalUri.Scheme == Uri.UriSchemeHttps
            && finalUri.Host is not ("github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com"))
            throw new InstanceException("HCSL-FRAMEWORK-NET", "Release download redirected to an unapproved host.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InstanceException("HCSL-FRAMEWORK-NET", "Release download failed (HTTP " + (int)response.StatusCode + ").");
        var length = response.Content.Headers.ContentLength;
        if (length is > 0 && length > FrameworkRules.MaxDownloadBytes)
            throw new InstanceException("HCSL-FRAMEWORK-LIMIT", "Release download is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = LocalPaths.CreateFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > FrameworkRules.MaxDownloadBytes) throw new InstanceException("HCSL-FRAMEWORK-LIMIT", "Release download is too large.");
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (expectedSize > 0 && total != expectedSize)
            throw new InstanceException("HCSL-FRAMEWORK-SIZE", "Downloaded size does not match the release metadata.");
        await output.FlushAsync(ct);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private (ReleaseInfo Release, ReleaseAsset Asset, string Version)? Select(FrameworkDefinition framework, ReleaseInfo[] releases, string? requested)
    {
        foreach (var release in releases)
        {
            var version = FrameworkRules.VersionFromTag(framework.Id, release.Tag);
            if (version is null) continue;
            var assetName = FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), true)
                ?? FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), false);
            if (assetName is null) continue;
            if (requested is not null && !string.Equals(requested, version, StringComparison.Ordinal)
                && !string.Equals(requested, release.Tag, StringComparison.Ordinal)) continue;
            var assetVersion = FrameworkRules.VersionFromAsset(framework.Id, assetName);
            if (assetVersion is not null && !string.Equals(assetVersion, version, StringComparison.Ordinal))
                throw new InstanceException("HCSL-FRAMEWORK-RELEASE", "Release tag and asset version disagree.");
            return (release, release.Assets.First(a => a.Name == assetName), version);
        }
        return null;
    }

    private static (string Version, string Asset)? Latest(FrameworkDefinition framework, ReleaseInfo[] releases)
    {
        foreach (var release in releases)
        {
            var version = FrameworkRules.VersionFromTag(framework.Id, release.Tag);
            if (version is null) continue;
            var asset = FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), true)
                ?? FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), false);
            if (asset is not null) return (version, asset);
        }
        return null;
    }

    private async Task<ReleaseInfo[]> Releases(FrameworkDefinition framework, CancellationToken ct)
    {
        if (cache.TryGetValue(framework.Id, out var cached) && clock.GetUtcNow() - cached.At < CacheLifetime) return cached.Releases;
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + framework.Repository + "/releases?per_page=20");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InstanceException("HCSL-FRAMEWORK-NET", "GitHub API rate limit reached; retry later or set HCSL_GITHUB_TOKEN.");
        if (!response.IsSuccessStatusCode)
            throw new InstanceException("HCSL-FRAMEWORK-NET", "Release metadata request failed (HTTP " + (int)response.StatusCode + ").");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, default, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InstanceException("HCSL-FRAMEWORK-RELEASE", "Unexpected release metadata.");
        var releases = new List<ReleaseInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var draft = item.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
            if (draft) continue;
            var tag = item.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                    var digest = asset.TryGetProperty("digest", out var dig) ? dig.GetString() : null;
                    assets.Add(new(name, url, size, digest));
                }
            }
            releases.Add(new(tag, assets.ToArray()));
        }
        var result = releases.ToArray();
        if (cache.Count > FrameworkRules.MaxCatalogFrameworks * 4) cache.Clear();
        cache[framework.Id] = (clock.GetUtcNow(), result);
        return result;
    }

    private HttpClient Client()
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HCSL/1.0 (+https://github.com/)");
        var token = Environment.GetEnvironmentVariable("HCSL_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static string PlatformName => OperatingSystem.IsWindows() ? "Windows" : "Linux";

    private static string? InstalledVersion(string packagesRoot, string id)
    {
        if (!Directory.Exists(packagesRoot)) return null;
        string? best = null;
        try
        {
            foreach (var versionDir in Directory.EnumerateDirectories(Path.Combine(packagesRoot, id)))
            {
                var version = Path.GetFileName(versionDir);
                try { LocalPluginStore.LoadPackage(packagesRoot, id, version); }
                catch (Exception e) when (e is InstanceException or IOException or UnauthorizedAccessException) { continue; }
                if (best is null || FrameworkRules.CompareVersions(id, version, best) > 0) best = version;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return best;
    }

    private sealed record ReleaseInfo(string Tag, ReleaseAsset[] Assets);
    private sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);
}
