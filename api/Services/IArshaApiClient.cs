using BdoMarketTracker.Models;

namespace BdoMarketTracker.Services;

public interface IArshaApiClient
{
    Task<List<ArshaDbItem>> GetItemDatabaseAsync(CancellationToken ct = default);
    Task<List<ArshaMarketItem>> GetItemDataAsync(IEnumerable<int> itemIds, string region = "na", CancellationToken ct = default);
    Task<ArshaOrderBook?> GetOrderBookAsync(int itemId, int sid = 0, string region = "na", CancellationToken ct = default);
}
