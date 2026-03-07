using BdoMarketTracker.Data;
using Microsoft.EntityFrameworkCore;

namespace BdoMarketTracker.Services;

public class CorrectionFactorProvider(AppDbContext db) : ICorrectionFactorProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static Dictionary<(int, string), double>? _cache;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly object _lock = new();

    public async Task<Dictionary<(int ItemId, string Window), double>> GetFactorsAsync(
        IEnumerable<int> itemIds, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_cache != null && DateTime.UtcNow < _cacheExpiry)
                return _cache;
        }

        var factors = await db.CorrectionFactors
            .ToDictionaryAsync(
                f => (f.ItemId, f.Window),
                f => f.Factor,
                ct);

        lock (_lock)
        {
            _cache = factors;
            _cacheExpiry = DateTime.UtcNow + CacheDuration;
        }

        return factors;
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _cacheExpiry = DateTime.MinValue;
        }
    }
}
