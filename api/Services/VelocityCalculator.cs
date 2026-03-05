using BdoMarketTracker.Data;
using BdoMarketTracker.Dtos;
using BdoMarketTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace BdoMarketTracker.Services;

public class VelocityCalculator(AppDbContext db) : IVelocityCalculator
{
    public static readonly Dictionary<string, TimeSpan> WindowDefinitions = new()
    {
        ["3h"] = TimeSpan.FromHours(3),
        ["12h"] = TimeSpan.FromHours(12),
        ["24h"] = TimeSpan.FromHours(24),
        ["3d"] = TimeSpan.FromDays(3),
        ["7d"] = TimeSpan.FromDays(7),
        ["14d"] = TimeSpan.FromDays(14),
    };

    public static bool IsValidWindow(string window) => WindowDefinitions.ContainsKey(window);

    public async Task<VelocityDto> GetVelocityAsync(int itemId, CancellationToken ct = default)
    {
        var item = await db.TrackedItems.FindAsync([itemId], ct);
        if (item == null)
            return new VelocityDto { ItemId = itemId, Name = "Unknown" };

        var dto = new VelocityDto { ItemId = itemId, Name = item.Name };
        var now = DateTime.UtcNow;

        // Single query: fetch all snapshots within the largest window (14d)
        var maxCutoff = now - WindowDefinitions.Values.Max();
        var allSnapshots = await db.TradeSnapshots
            .Where(s => s.ItemId == itemId && s.RecordedAt >= maxCutoff)
            .OrderBy(s => s.RecordedAt)
            .ToListAsync(ct);

        foreach (var (windowName, duration) in WindowDefinitions)
        {
            var cutoff = now - duration;
            var snapshots = allSnapshots.Where(s => s.RecordedAt >= cutoff).ToList();

            if (snapshots.Count < 2)
            {
                dto.Windows.Add(new VelocityWindowDto
                {
                    Window = windowName,
                    SalesCount = 0,
                    SalesPerHour = 0,
                    AvgPreorders = snapshots.FirstOrDefault()?.TotalPreorders ?? 0,
                    Confidence = "low"
                });
                continue;
            }

            var halfLifeHours = duration.TotalHours / 2;
            var (segments, totalSalesCount) = AnalyzeSegments(snapshots, now, halfLifeHours);

            var salesPerHour = WeightedMean(segments);
            var avgPreorders = snapshots.Average(s => (double)s.TotalPreorders);
            var salesCount = snapshots.Last().TotalTrades - snapshots.First().TotalTrades;

            dto.Windows.Add(new VelocityWindowDto
            {
                Window = windowName,
                SalesCount = salesCount,
                SalesPerHour = Math.Round(salesPerHour, 2),
                AvgPreorders = Math.Round(avgPreorders, 0),
                Confidence = GetConfidence(segments.Count, totalSalesCount)
            });
        }

        return dto;
    }

    public async Task<List<DashboardItemDto>> GetDashboardAsync(string window = "24h", CancellationToken ct = default)
    {
        // Validate and resolve window
        if (!WindowDefinitions.TryGetValue(window, out var windowDuration))
            windowDuration = WindowDefinitions["24h"];

        var items = await db.TrackedItems.ToListAsync(ct);
        if (items.Count == 0) return [];

        var now = DateTime.UtcNow;
        var cutoff = now - windowDuration;
        var itemIds = items.Select(i => i.Id).ToList();

        // Batch query 1: latest snapshot per item
        var latestSnapshots = await db.TradeSnapshots
            .Where(s => itemIds.Contains(s.ItemId))
            .GroupBy(s => s.ItemId)
            .Select(g => g.OrderByDescending(s => s.RecordedAt).First())
            .ToDictionaryAsync(s => s.ItemId, ct);

        // Batch query 2: all snapshots within the selected window
        var snapshotsByItem = (await db.TradeSnapshots
            .Where(s => itemIds.Contains(s.ItemId) && s.RecordedAt >= cutoff)
            .OrderBy(s => s.RecordedAt)
            .ToListAsync(ct))
            .GroupBy(s => s.ItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var halfLifeHours = windowDuration.TotalHours / 2;
        var result = new List<DashboardItemDto>();

        foreach (var item in items)
        {
            if (!latestSnapshots.TryGetValue(item.Id, out var latestSnapshot))
                continue;

            double salesPerHour = 0;
            string confidence = "low";
            long salesCount = 0;

            if (snapshotsByItem.TryGetValue(item.Id, out var itemSnapshots) && itemSnapshots.Count >= 2)
            {
                var (segments, totalSalesCount) = AnalyzeSegments(itemSnapshots, now, halfLifeHours);
                salesPerHour = WeightedMean(segments);
                confidence = GetConfidence(segments.Count, totalSalesCount);
                salesCount = itemSnapshots.Last().TotalTrades - itemSnapshots.First().TotalTrades;
            }

            var totalPreorders = latestSnapshot.TotalPreorders;
            var fulfillmentScore = totalPreorders > 0 ? salesPerHour / totalPreorders : 0;
            var estimatedFillHours = salesPerHour > 0 ? totalPreorders / salesPerHour : double.MaxValue;

            result.Add(new DashboardItemDto
            {
                ItemId = item.Id,
                Name = item.Name,
                Grade = item.Grade,
                TotalPreorders = totalPreorders,
                SalesCount = salesCount,
                SalesPerHour = Math.Round(salesPerHour, 2),
                Window = window,
                FulfillmentScore = Math.Round(fulfillmentScore, 4),
                EstimatedFillTime = FormatFillTime(estimatedFillHours),
                Confidence = confidence
            });
        }

        return result.OrderByDescending(r => r.FulfillmentScore).ToList();
    }

    // Day-of-week weight multipliers for BDO market patterns
    private static readonly Dictionary<DayOfWeek, double> DayOfWeekWeights = new()
    {
        [DayOfWeek.Monday] = 1.3,    // Purchase limit resets on discounted bundles
        [DayOfWeek.Tuesday] = 1.0,
        [DayOfWeek.Wednesday] = 1.0,
        [DayOfWeek.Thursday] = 1.5,  // Maintenance + Pearl Shop refresh
        [DayOfWeek.Friday] = 1.3,    // Payday spending
        [DayOfWeek.Saturday] = 1.2,  // Weekend spending
        [DayOfWeek.Sunday] = 1.0,
    };

    private record SaleSegment(double SalesPerHour, double Weight);

    private static (List<SaleSegment> Segments, long TotalSalesCount) AnalyzeSegments(
        List<TradeSnapshot> snapshots, DateTime now, double halfLifeHours)
    {
        var segments = new List<SaleSegment>();
        long totalSalesCount = 0;

        for (int i = 1; i < snapshots.Count; i++)
        {
            var prev = snapshots[i - 1];
            var curr = snapshots[i];
            var salesDelta = curr.TotalTrades - prev.TotalTrades;

            if (salesDelta < 0)
                continue;

            totalSalesCount += salesDelta;

            var hours = (curr.RecordedAt - prev.RecordedAt).TotalHours;
            if (hours <= 0)
                continue;

            var segmentRate = (double)salesDelta / hours;

            // Exponential decay weight based on segment midpoint age
            var midpoint = prev.RecordedAt.AddHours(hours / 2);
            var ageHours = (now - midpoint).TotalHours;
            var weight = Math.Exp(-Math.Log(2) * ageHours / halfLifeHours)
                       * DayOfWeekWeights[midpoint.DayOfWeek];

            segments.Add(new SaleSegment(segmentRate, weight));
        }

        return (segments, totalSalesCount);
    }

    private static double WeightedMean(List<SaleSegment> segments)
    {
        if (segments.Count == 0) return 0;

        var totalWeight = segments.Sum(s => s.Weight);
        if (totalWeight <= 0) return 0;

        var weightedSum = segments.Sum(s => s.SalesPerHour * s.Weight);
        return weightedSum / totalWeight;
    }

    private static string GetConfidence(int segmentCount, long totalSalesCount)
    {
        if (segmentCount < 3 || totalSalesCount < 3) return "low";
        if (segmentCount > 10 && totalSalesCount >= 10) return "high";
        return "medium";
    }

    private static string FormatFillTime(double hours)
    {
        if (hours >= double.MaxValue || hours <= 0) return "N/A";
        if (hours < 1) return $"~{hours * 60:F0} min";
        if (hours < 24) return $"~{hours:F1} hrs";
        return $"~{hours / 24:F1} days";
    }
}
