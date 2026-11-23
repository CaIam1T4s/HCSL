namespace HCSL.Core;

public sealed record WorkshopItem(string Id, string Title, WorkshopItem[]? Children = null);

public static class WorkshopCatalog
{
    public static string? ParseId(string input)
    {
        input = input.Trim();
        if (ServerMapRules.TryValidateWorkshopId(input)) return input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.Host != "steamcommunity.com" || uri.AbsolutePath.TrimEnd('/') is not ("/sharedfiles/filedetails" or "/workshop/filedetails")) return null;
        var ids = uri.Query.TrimStart('?').Split('&').Where(x => x.StartsWith("id=", StringComparison.Ordinal)).ToArray();
        return ids.Length == 1 && ServerMapRules.TryValidateWorkshopId(ids[0][3..]) ? ids[0][3..] : null;
    }
}
