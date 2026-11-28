using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Infrastructure.Frameworks;
using HCSL.Infrastructure.Storage;

namespace HCSL.ServerTests;

public static class FrameworkCatalogTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hcsl-framework-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            using (new LocalWorkspaceManager().Acquire(workspace)) { }
            var fresh = new WorkspaceRequest(Path.Combine(root, "fresh-settings"), Path.Combine(root, "fresh-data"), Path.Combine(root, "fresh-library"));
            using var bootstrapHttp = new CatalogHandler();
            var beforeSetup = await new FrameworkManager(bootstrapHttp).CatalogAsync(fresh, new("counterstrikesharp"));
            Check(beforeSetup.Versions.Length > 0 && !Directory.Exists(fresh.DataRoot), "Catalog lookup created an unowned workspace.");
            using (new LocalWorkspaceManager().Acquire(fresh)) { }
            using var http = new CatalogHandler();
            var manager = new FrameworkManager(http);
            var empty = await manager.CatalogAsync(workspace, new("counterstrikesharp", CacheOnly: true));
            Check(empty.Versions.Length == 0 && http.Count == 0, "Cache-only made a network request.");
            var first = await manager.CatalogAsync(workspace, new("counterstrikesharp"));
            Check(first.Versions.Length == 2 && first.NextPage == 2 && http.Count == 1 && first.Versions.All(v => v.Downloadable), "Initial page projection.");
            var cached = await manager.CatalogAsync(workspace, new("counterstrikesharp"));
            Check(cached.FromCache && http.Count == 1, "Cache-first requested the head again.");
            using var offline = new CatalogHandler { Offline = true };
            var reopened = await new FrameworkManager(offline).CatalogAsync(workspace, new("counterstrikesharp", CacheOnly: true));
            Check(reopened.Versions.Length == 2 && offline.Count == 0, "Persisted cache was not usable offline.");
            http.NotModified = true;
            var unchanged = await manager.CatalogAsync(workspace, new("counterstrikesharp", Refresh: true));
            Check(http.SawETag && unchanged.Versions.Length == 2 && unchanged.NextPage == 2, "ETag/304 lost the history cursor.");
            http.NotModified = false; http.NewHead = true;
            var updated = await manager.CatalogAsync(workspace, new("counterstrikesharp", Refresh: true));
            Check(updated.TotalCached == 3 && updated.Versions[0].Version == "1.0.3" && updated.NextPage == 2, "New release was not prepended.");
            var history = await manager.CatalogAsync(workspace, new("counterstrikesharp", Page: 2));
            Check(history.TotalCached == 4 && history.NextPage is null && history.Versions.Select(v => v.ReleaseId).Distinct().Count() == history.Versions.Length, "Historical pages duplicated or replaced cached releases.");
            var localPage = await manager.CatalogAsync(workspace, new("counterstrikesharp", CacheOnly: true, Offset: 2));
            Check(localPage.Versions.Length == 2 && localPage.TotalCached == 4, "Local cached pagination.");
            var allCached = await manager.CatalogAsync(workspace, new("counterstrikesharp", CacheOnly: true));
            Check(allCached.Versions.Select(v => v.ReleaseId).Distinct().Count() == 4, "Merged cache contains duplicates or lost releases.");
            try { await manager.CatalogAsync(workspace, new("counterstrikesharp", Page: 7)); throw new Exception("Invalid cursor accepted."); }
            catch (InstanceException e) when (e.Code == "HCSL-FRAMEWORK-CURSOR") { }
            http.Fail = true;
            var failed = await manager.CatalogAsync(workspace, new("counterstrikesharp", Refresh: true));
            var calls = http.Count;
            var backedOff = await manager.CatalogAsync(workspace, new("counterstrikesharp", Refresh: true));
            Check(failed.Error == "HCSL-FRAMEWORK-NET" && failed.TotalCached == 4 && backedOff.TotalCached == 4 && http.Count == calls, "Failure cleared cache or ignored retry backoff.");
            http.Fail = false;
            try { await manager.InstallAsync(workspace, new("counterstrikesharp", "v1.0.0")); throw new Exception("Mismatched archive digest was accepted."); }
            catch (InstanceException e) when (e.Code == "HCSL-FRAMEWORK-HASH") { }
            Check(http.SawHistoricalTag, "Historical installation was not revalidated by its official tag.");
            var frame = JsonSerializer.SerializeToUtf8Bytes(new LocalResponse(LocalProtocol.Version, Guid.NewGuid(), true,
                FrameworkCatalogJson: JsonSerializer.Serialize(history)));
            Check(frame.Length < LocalProtocol.MaxFrameBytes, "Catalog response exceeds the IPC frame limit.");
            using var manyHttp = new CatalogHandler { Many = true };
            var manyManager = new FrameworkManager(manyHttp);
            var many = await manyManager.CatalogAsync(workspace, new("source2mod"));
            var nextLocal = await manyManager.CatalogAsync(workspace, new("source2mod", CacheOnly: true, Offset: 20));
            Check(many.TotalCached == 45 && many.Versions.Length == 20 && nextLocal.Versions.Length == 20 && manyHttp.Count == 1, "Cached local pages should not trigger network traffic.");
            using var rateHttp = new CatalogHandler { RateLimited = true };
            var rateManager = new FrameworkManager(rateHttp);
            var rate = await rateManager.CatalogAsync(workspace, new("swiftlys2"));
            await rateManager.CatalogAsync(workspace, new("swiftlys2", Refresh: true));
            Check(rate.Error == "HCSL-FRAMEWORK-RATE-LIMIT" && rateHttp.Count == 1, "Rate-limit retry delay was not honored.");
            File.WriteAllText(Path.Combine(workspace.DataRoot!, "cache", "framework-releases", "counterstrikesharp.json"), "invalid json");
            var corrupt = await new FrameworkManager(offline).CatalogAsync(workspace, new("counterstrikesharp", CacheOnly: true));
            Check(corrupt.Versions.Length == 0 && offline.Count == 0, "Malformed cache did not degrade safely.");
            Console.WriteLine("PASS framework.catalog.cache-first-persistence-etag-head-history-backoff-and-revalidation");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public int Count;
        public bool Offline, Fail, NotModified, NewHead, SawETag, SawHistoricalTag, Many, RateLimited;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            if (Offline) throw new HttpRequestException("Offline fixture");
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (RateLimited)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(limited);
            }
            if (Many) return Task.FromResult(Response("[" + string.Join(",", Enumerable.Range(100, 45).Select(i => Release(i, "1.0." + i))) + "]"));
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                SawHistoricalTag = uri.AbsolutePath.EndsWith("v1.0.0", StringComparison.Ordinal);
                return Task.FromResult(Response(Release(100, "1.0.0")));
            }
            if (!uri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) });
            SawETag |= request.Headers.IfNoneMatch.Any();
            if (NotModified) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var page2 = uri.Query.Contains("&page=2", StringComparison.Ordinal);
            var text = page2 ? "[" + Release(101, "1.0.1") + "," + Release(100, "1.0.0") + "]" :
                "[" + (NewHead ? Release(103, "1.0.3") + "," : "") + Release(102, "1.0.2") + "," + Release(101, "1.0.1") + "]";
            var response = Response(text);
            response.Headers.ETag = new EntityTagHeaderValue(NewHead ? "\"head-2\"" : "\"head-1\"");
            if (!page2) response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/ignored>; rel=\"next\"");
            return Task.FromResult(response);
        }
        private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        private static string Release(int id, string version) => JsonSerializer.Serialize(new
        {
            id, tag_name = "v" + version, draft = false, prerelease = false, published_at = "2026-01-" + (id - 99).ToString("00") + "T00:00:00Z",
            assets = new[] { "linux", "windows" }.Select(os => new { name = "counterstrikesharp-with-runtime-" + os + "-" + version + ".zip", size = 16,
                browser_download_url = "https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v" + version + "/fixture.zip", digest = "sha256:" + new string('a', 64) }).ToArray()
        });
    }
}
