namespace HCSL.Core;

/// <summary>CacheOnly never contacts the network; Refresh revalidates the head; Page loads the next historical page.</summary>
public sealed record FrameworkCatalogCommand(string Id, bool Refresh = false, int? Page = null, bool CacheOnly = false, int Offset = 0);
public sealed record FrameworkReleaseEntry(long ReleaseId, string Version, string Tag, DateTimeOffset? PublishedAt,
    bool Prerelease, string? Asset, long Bytes, bool Downloadable, string? UnavailableReason);
public sealed record FrameworkCatalogResult(string Id, FrameworkReleaseEntry[] Versions, string[] InstalledVersions,
    int? NextPage, DateTimeOffset? CheckedAt, bool NeedsRefresh, bool FromCache, string? Error = null, int TotalCached = 0);
