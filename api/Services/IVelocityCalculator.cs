using BdoMarketTracker.Dtos;

namespace BdoMarketTracker.Services;

public interface IVelocityCalculator
{
    Task<VelocityDto> GetVelocityAsync(int itemId, CancellationToken ct = default);
    Task<List<DashboardItemDto>> GetDashboardAsync(string window = "24h", CancellationToken ct = default);
}
