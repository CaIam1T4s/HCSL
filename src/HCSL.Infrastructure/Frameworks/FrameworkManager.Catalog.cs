using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;

namespace HCSL.Infrastructure.Frameworks;

public sealed partial class FrameworkManager
{
    private const int CatalogPageSize = 20;
    private const int MaxCatalogBytes = 8 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> catalogGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CatalogState> catalogs = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonSerializable(typeof(CatalogState))]
    private sealed partial class CatalogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

    private sealed record CatalogState(int FormatVersion, string Repository, ReleaseInfo[] Releases,
        int? NextPage, string? HeadETag = null, DateTimeOffset? CheckedAt = null, DateTimeOffset? RetryAt = null);

    public async Task<FrameworkCatalogResult> CatalogAsync(WorkspaceRequest workspace, FrameworkCatalogCommand command, CancellationToken ct = default)
    {
        var framework = FrameworkRules.Find(command.Id) ?? throw new InstanceException("HCSL-FRAMEWORK-ID", "Unknown framework.");
        if (command.Offset is < 0 or > 10000 || command.Page is < 1 or > 10000 || command.Refresh && command.Page is > 1)
            throw new InstanceException("HCSL-FRAMEWORK-PAGE", "Invalid catalog page.");
        FrameworkCatalogResult Slice(FrameworkCatalogResult result, HashSet<string>? fetchedTags = null)
        {
            // A remote page may bridge a gap at the head, rather than append at the tail.
            var visible = fetchedTags is null ? result.Versions.Skip(command.Offset).Take(20).ToArray() :
                result.Versions.Where(v => fetchedTags.Contains(v.Tag)).Take(20).ToArray();
            return result with
            {
                TotalCached = result.Versions.Length,
                Versions = visible,
                InstalledVersions = result.InstalledVersions.Take(1).Concat(result.InstalledVersions.Where(v => visible.Any(r => r.Version == v))).Distinct().ToArray()
            };
        }
        var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        var file = CatalogPath(paths.DataRoot, framework.Id);
        var gate = catalogGates.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        var requestedAt = clock.GetUtcNow();
        await gate.WaitAsync(ct);
        try
        {
            var state = catalogs.GetOrAdd(file, _ => ReadCatalog(file, framework.Repository));
            var cached = state.CheckedAt is not null;
            if (command.CacheOnly || cached && !command.Refresh && command.Page is null ||
                command.Refresh && state.CheckedAt is { } checkedAt && checkedAt > requestedAt)
                return Slice(Project(framework, paths.LibraryRoot, state, true));
            var now = clock.GetUtcNow();
            if (state.RetryAt is { } retry && retry > now)
                return Slice(Project(framework, paths.LibraryRoot, state, true, "HCSL-FRAMEWORK-RETRY-LATER"));
            var page = command.Refresh ? 1 : command.Page ?? 1;
            if (page > 1 && state.NextPage != page)
                throw new InstanceException("HCSL-FRAMEWORK-CURSOR", "Catalog changed; reload the cached cursor before requesting history.");
            try
            {
                using var client = Client();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/" + framework.Repository + "/releases?per_page=" + CatalogPageSize + "&page=" + page);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                if (page == 1 && cached && state.HeadETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    var retryAt = response.Headers.RetryAfter?.Date ?? now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                    state = state with { RetryAt = retryAt > now.AddDays(1) ? now.AddDays(1) : retryAt < now.AddSeconds(30) ? now.AddSeconds(30) : retryAt };
                    return Slice(SaveAndProject(file, framework, paths.LibraryRoot, state, true, "HCSL-FRAMEWORK-RATE-LIMIT"));
                }
                if (response.StatusCode == HttpStatusCode.NotModified && page == 1 && cached)
                    return Slice(SaveAndProject(file, framework, paths.LibraryRoot, state with { CheckedAt = clock.GetUtcNow(), RetryAt = null }, true));
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Release metadata request failed.");
                if (response.Content.Headers.ContentLength > MaxCatalogBytes) throw new IOException("Catalog page exceeds the size limit.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var data = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (data.Length + count > MaxCatalogBytes) throw new IOException("Catalog page exceeds the size limit.");
                    data.Write(buffer, 0, count);
                }
                using var document = JsonDocument.Parse(data.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected releases array.");
                var incoming = document.RootElement.EnumerateArray().Select(ParseCatalogRelease).Where(r => r is not null).Cast<ReleaseInfo>().ToArray();
                var entries = incoming.Concat(state.Releases).DistinctBy(ReleaseKey).DistinctBy(r => r.Tag).Take(10000).ToArray();
                var hasMore = response.Headers.TryGetValues("Link", out var links) && links.Any(l => l.Contains("rel=\"next\"", StringComparison.Ordinal));
                // Offset pagination shifts when releases are added. Only restart when the new head no longer
                // overlaps the cache; otherwise the next historical page overlaps safely without skipping entries.
                var headGap = page == 1 && (!cached || !incoming.Any(r => state.Releases.Any(old => ReleaseKey(old) == ReleaseKey(r))));
                var nextPage = page > 1 || headGap ? (hasMore ? page + 1 : (int?)null) : state.NextPage;
                if (page == 1 && !hasMore) nextPage = null;
                state = new(1, framework.Repository, entries, nextPage,
                    page == 1 ? response.Headers.ETag?.ToString() : state.HeadETag,
                    page == 1 ? clock.GetUtcNow() : state.CheckedAt, null);
                return Slice(SaveAndProject(file, framework, paths.LibraryRoot, state, false),
                    page > 1 ? incoming.Select(r => r.Tag).ToHashSet(StringComparer.Ordinal) : null);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                state = state with { RetryAt = now.AddMinutes(1) };
                return Slice(SaveAndProject(file, framework, paths.LibraryRoot, state, true, "HCSL-FRAMEWORK-NET"));
            }
        }
        finally { gate.Release(); }
    }

    private ReleaseInfo? CachedRelease(WorkspaceRequest workspace, FrameworkDefinition framework, string requested)
    {
        var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        var file = CatalogPath(paths.DataRoot, framework.Id);
        var state = catalogs.GetOrAdd(file, _ => ReadCatalog(file, framework.Repository));
        return state.Releases.FirstOrDefault(r => r.Tag == requested || FrameworkRules.VersionFromTag(framework.Id, r.Tag) == requested);
    }

    private async Task<ReleaseInfo> HistoricalReleaseAsync(WorkspaceRequest workspace, FrameworkDefinition framework, string requested, CancellationToken ct)
    {
        var tag = CachedRelease(workspace, framework, requested)?.Tag ?? requested;
        if (tag.Length > 256) throw new InstanceException("HCSL-FRAMEWORK-RELEASE", "Invalid release tag.");
        using var client = Client();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.GetAsync("https://api.github.com/repos/" + framework.Repository + "/releases/tags/" + Uri.EscapeDataString(tag), ct);
        if (!response.IsSuccessStatusCode) throw new InstanceException("HCSL-FRAMEWORK-RELEASE", "The selected release could not be revalidated; it may be unavailable.");
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > MaxCatalogBytes) throw new IOException("Release metadata exceeds the size limit.");
        using var document = JsonDocument.Parse(text);
        var release = ParseCatalogRelease(document.RootElement);
        if (release is null || release.Tag != tag) throw new InstanceException("HCSL-FRAMEWORK-RELEASE", "Selected release identity changed.");
        return release;
    }

    private static string ReleaseKey(ReleaseInfo release) => release.Id > 0 ? "id:" + release.Id : "tag:" + release.Tag;

    private static ReleaseInfo? ParseCatalogRelease(JsonElement item)
    {
        if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        var tag = item.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128) return null;
        var id = item.TryGetProperty("id", out var i) && i.TryGetInt64(out var number) ? number : 0;
        DateTimeOffset? published = item.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out var stamp) ? stamp : null;
        var prerelease = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        var assets = new List<ReleaseAsset>();
        if (item.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var asset in list.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || string.IsNullOrWhiteSpace(url) || url.Length > 4096) continue;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var length) ? length : 0;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                assets.Add(new(name, url, size, digest));
            }
        return new(tag, prerelease, [.. assets], id, published);
    }

    private static string CatalogPath(string dataRoot, string id)
    {
        LocalPaths.RejectLink(dataRoot);
        var folder = Path.Combine(dataRoot, "cache");
        LocalPaths.RejectLink(folder);
        folder = Path.Combine(folder, "framework-releases");
        LocalPaths.RejectLink(folder);
        var file = Path.Combine(folder, id + ".json");
        LocalPaths.RejectLink(file);
        return file;
    }
    private static CatalogState ReadCatalog(string file, string repository)
    {
        try
        {
            if (new FileInfo(file) is { Exists: true, Length: <= MaxCatalogBytes })
            {
                var state = JsonSerializer.Deserialize(File.ReadAllText(file), CatalogJsonContext.Default.CatalogState);
                if (state is { FormatVersion: 1 } && state.Repository == repository && state.Releases is not null && state.Releases.Length <= 10000 && state.Releases.All(r => r is not null && !string.IsNullOrWhiteSpace(r.Tag) && r.Assets is not null && r.Assets.All(a => a is not null && a.Name is not null && a.Url is not null)) && state.NextPage is null or >= 1)
                    return state;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new(1, repository, [], 1);
    }
    private FrameworkCatalogResult SaveAndProject(string file, FrameworkDefinition framework, string libraryRoot, CatalogState state, bool fromCache, string? error = null)
    {
        catalogs[file] = state;
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, CatalogJsonContext.Default.CatalogState);
            if (bytes.Length > MaxCatalogBytes) throw new IOException("Catalog cache exceeds the size limit.");
            var directory = Path.GetDirectoryName(file)!;
            var parent = Path.GetDirectoryName(directory)!;
            LocalPaths.RejectLink(parent); LocalPaths.CreatePrivateDirectory(parent);
            LocalPaths.RejectLink(directory); LocalPaths.CreatePrivateDirectory(directory);
            LocalPaths.RejectLink(file);
            using (var stream = LocalPaths.CreateFile(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { error ??= "HCSL-FRAMEWORK-CACHE-WRITE"; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        return Project(framework, libraryRoot, state, fromCache, error);
    }
    private FrameworkCatalogResult Project(FrameworkDefinition framework, string libraryRoot, CatalogState state, bool fromCache, string? error = null)
    {
        var entries = new List<FrameworkReleaseEntry>();
        foreach (var release in state.Releases.OrderByDescending(r => r.PublishedAt).ThenByDescending(r => r.Id))
        {
            var version = FrameworkRules.VersionFromTag(framework.Id, release.Tag);
            // Metamod's repository also carries the Source 1 line (1.12.0.<build>). Those are a
            // different product for a different engine, not an unsupported spelling of this
            // framework, so they never belong in the Source 2 catalog. Other unparsable tags stay
            // listed with a reason, so a genuinely new naming scheme is visible instead of vanishing.
            if (FrameworkRules.IsBase(framework.Id) && FrameworkRules.IsSupersededMetamodTag(release.Tag)) continue;
            var name = FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), true)
                ?? FrameworkRules.SelectAsset(framework.Id, release.Assets.Select(a => a.Name), OperatingSystem.IsWindows(), false);
            var asset = release.Assets.FirstOrDefault(a => a.Name == name);
            var reason = version is null ? "该历史版本的命名尚不支持" : asset is null ? "没有适用于当前平台的资源" :
                asset.Size is <= 0 or > FrameworkRules.MaxDownloadBytes ? "发行包大小不符合限制" :
                asset.Digest is null || asset.Digest.Length != 71 || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !asset.Digest[7..].All(Uri.IsHexDigit) ? "缺少官方 SHA-256 校验信息" : null;
            if (reason is null && FrameworkRules.VersionFromAsset(framework.Id, name!) is { } assetVersion && assetVersion != version)
                reason = "版本标签与发行包版本不一致";
            // Metamod has published no stable Source 2 channel yet, so GitHub flags every 2.0.0 build
            // as a prerelease. Showing them all as prereleases would mislabel the only installable line.
            var prerelease = FrameworkRules.IsBase(framework.Id) ? false : release.Prerelease;
            entries.Add(new(release.Id, version ?? release.Tag, release.Tag, release.PublishedAt, prerelease, name,
                asset?.Size ?? 0, reason is null, reason));
        }
        var packagesRoot = Path.Combine(libraryRoot, "plugin-packages");
        var installed = new List<string>();
        try
        {
            var directory = Path.Combine(packagesRoot, framework.Id);
            if (Directory.Exists(directory))
                foreach (var version in FrameworkRules.OrderVersions(framework.Id, Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>()))
                    try { LocalPluginStore.LoadPackage(packagesRoot, framework.Id, version); installed.Add(version); }
                    catch (Exception e) when (e is InstanceException or IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error ??= "HCSL-FRAMEWORK-LOCAL"; }
        return new(framework.Id, [.. entries], [.. installed], state.NextPage, state.CheckedAt,
            state.CheckedAt is null || clock.GetUtcNow() - state.CheckedAt >= CacheLifetime, fromCache, error);
    }
}
