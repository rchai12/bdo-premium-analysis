namespace BdoMarketTracker.Services;

public interface ICorrectionFactorProvider
{
    Task<Dictionary<(int ItemId, string Window), double>> GetFactorsAsync(
        IEnumerable<int> itemIds, CancellationToken ct = default);

    void InvalidateCache();
}
