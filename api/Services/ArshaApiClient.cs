using System.Text.Json;
using System.Text.Json.Serialization;
using BdoMarketTracker.Models;

namespace BdoMarketTracker.Services;

public class ArshaApiClient(HttpClient httpClient, ILogger<ArshaApiClient> logger) : IArshaApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<List<ArshaDbItem>> GetItemDatabaseAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Fetching full item database from arsha.io");
        var response = await httpClient.GetAsync("/util/db?lang=en", ct);
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<List<ArshaDbItem>>(JsonOptions, ct);
        return items ?? [];
    }

    public async Task<List<ArshaMarketItem>> GetItemDataAsync(IEnumerable<int> itemIds, string region = "na", CancellationToken ct = default)
    {
        var ids = itemIds.ToList();
        if (ids.Count == 0) return [];

        var queryParams = string.Join("&", ids.Select(id => $"id={id}"));
        var url = $"/v2/{region}/item?{queryParams}&lang=en";

        var response = await httpClient.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogDebug("Item data returned 404 for batch, skipping");
            return [];
        }

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);

        // API returns a single object when only one item, array when multiple
        if (ids.Count == 1)
        {
            // Single item might return object or array depending on enhancement levels
            try
            {
                var items = JsonSerializer.Deserialize<List<ArshaMarketItem>>(content, JsonOptions);
                return items ?? [];
            }
            catch (JsonException)
            {
                var item = JsonSerializer.Deserialize<ArshaMarketItem>(content, JsonOptions);
                return item != null ? [item] : [];
            }
        }

        // Multiple items always returns array of arrays or flat array
        try
        {
            var items = JsonSerializer.Deserialize<List<ArshaMarketItem>>(content, JsonOptions);
            return items ?? [];
        }
        catch (JsonException)
        {
            // Might be nested arrays for items with enhancement levels
            var nested = JsonSerializer.Deserialize<List<List<ArshaMarketItem>>>(content, JsonOptions);
            return nested?.SelectMany(x => x).ToList() ?? [];
        }
    }

    public async Task<ArshaOrderBook?> GetOrderBookAsync(int itemId, int sid = 0, string region = "na", CancellationToken ct = default)
    {
        var url = $"/v2/{region}/orders?id={itemId}&sid={sid}&lang=en";
        var response = await httpClient.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogDebug("No order book found for item {ItemId} (404)", itemId);
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ArshaOrderBook>(JsonOptions, ct);
    }
}
