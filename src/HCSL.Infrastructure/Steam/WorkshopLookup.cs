using System.Text.Json;
using HCSL.Core;

namespace HCSL.Infrastructure.Steam;

public static class WorkshopLookup
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public static async Task<WorkshopItem> ReadAsync(string id, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var details = await Query("GetPublishedFileDetails", [id], timeout.Token);
            var entry = details.Single();
            var title = Title(entry);
            var hasType = entry.TryGetProperty("file_type", out var kind) && kind.TryGetInt32(out _);
            var declaredCollection = hasType && kind.GetInt32() == 2;
            // Published-file metadata and tags are not authoritative for collections.
            if (!entry.TryGetProperty("consumer_app_id", out var app) || app.GetInt32() != 730)
                throw new IOException("不是可用的 CS2 工坊条目。");
            var children = await Query("GetCollectionDetails", [id], timeout.Token);
            if (children.Length != 1) throw new IOException("无法确认工坊条目类型。");
            var collectionResult = children[0].GetProperty("result").GetInt32();
            if (collectionResult != 1)
            {
                // A normal map returns FileNotFound from the collection endpoint.
                if (!declaredCollection && collectionResult == 9 && IsMap(entry)) return new(id, title);
                throw new IOException("不是可用的 CS2 工坊地图或合集。");
            }
            var ids = children.Single().GetProperty("children").EnumerateArray().Select(x => x.GetProperty("publishedfileid").GetString()!)
                .Where(ServerMapRules.TryValidateWorkshopId).Distinct().Take(101).ToArray();
            if (ids.Length > 100) throw new IOException("暂不支持超过 100 个条目的合集。");
            var members = ids.Length == 0 ? [] : await Query("GetPublishedFileDetails", ids, timeout.Token);
            return new(id, title, members.Where(IsMap).Select(x => new WorkshopItem(x.GetProperty("publishedfileid").GetString()!, Title(x))).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstanceException("HCSL-WORKSHOP-TIMEOUT", "Steam 工坊 API（api.steampowered.com）未在 15 秒内响应，请检查守护进程的网络连接后重试。");
        }
        catch (HttpRequestException e)
        {
            var detail = e.StatusCode is { } status ? $"Steam 工坊 API 返回 HTTP {(int)status}。" : "守护进程无法连接 Steam 工坊 API（api.steampowered.com）。";
            throw new InstanceException("HCSL-WORKSHOP-NETWORK", detail);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InstanceException("HCSL-WORKSHOP-LOOKUP", e is IOException ? e.Message : "Steam 工坊返回了无法识别的条目数据。");
        }
    }

    private static bool IsMap(JsonElement x) => x.TryGetProperty("result", out var result) && result.GetInt32() == 1 &&
        x.TryGetProperty("consumer_app_id", out var app) && app.GetInt32() == 730 &&
        x.TryGetProperty("tags", out var tags) && tags.EnumerateArray().Any(t =>
            string.Equals(t.GetProperty("tag").GetString(), "Map", StringComparison.OrdinalIgnoreCase));

    private static string Title(JsonElement x)
    {
        var title = x.GetProperty("title").GetString()?.Trim();
        return title is { Length: > 0 and <= 200 } ? title : throw new IOException("工坊条目没有有效名称。");
    }

    private static async Task<JsonElement[]> Query(string method, string[] ids, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(ids.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id))
            .Prepend(new(method == "GetCollectionDetails" ? "collectioncount" : "itemcount", ids.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        using var response = await Http.PostAsync($"https://api.steampowered.com/ISteamRemoteStorage/{method}/v1/", form, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return json.RootElement.GetProperty("response").GetProperty(method == "GetCollectionDetails" ? "collectiondetails" : "publishedfiledetails")
            .EnumerateArray().Select(x => x.Clone()).ToArray();
    }
}
