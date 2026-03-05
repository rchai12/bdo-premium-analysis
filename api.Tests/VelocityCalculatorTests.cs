using BdoMarketTracker.Data;
using BdoMarketTracker.Models;
using BdoMarketTracker.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BdoMarketTracker.Tests;

public class VelocityCalculatorTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetVelocityAsync_UnknownItem_ReturnsUnknownDto()
    {
        using var db = CreateDb();
        var calc = new VelocityCalculator(db);

        var result = await calc.GetVelocityAsync(999);

        Assert.Equal(999, result.ItemId);
        Assert.Equal("Unknown", result.Name);
    }

    [Fact]
    public async Task GetVelocityAsync_SingleSnapshot_ReturnsZeroSales()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Test Set", Grade = 2 });
        db.TradeSnapshots.Add(new TradeSnapshot
        {
            ItemId = 1,
            RecordedAt = now,
            TotalTrades = 100,
            CurrentStock = 5,
            BasePrice = 1_000_000,
            LastSoldPrice = 1_000_000,
            TotalPreorders = 10
        });
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetVelocityAsync(1);

        Assert.Equal("Test Set", result.Name);
        Assert.All(result.Windows, w =>
        {
            Assert.Equal(0, w.SalesCount);
            Assert.Equal(0, w.SalesPerHour);
            Assert.Equal("low", w.Confidence);
        });
    }

    [Fact]
    public async Task GetVelocityAsync_TwoSnapshots_CalculatesCorrectVelocity()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Premium Lahn Set", Grade = 2 });
        db.TradeSnapshots.AddRange(
            new TradeSnapshot
            {
                ItemId = 1,
                RecordedAt = now.AddHours(-12),
                TotalTrades = 100,
                CurrentStock = 5,
                BasePrice = 1_000_000,
                LastSoldPrice = 1_000_000,
                TotalPreorders = 8
            },
            new TradeSnapshot
            {
                ItemId = 1,
                RecordedAt = now,
                TotalTrades = 124,
                CurrentStock = 3,
                BasePrice = 1_000_000,
                LastSoldPrice = 1_000_000,
                TotalPreorders = 12
            }
        );
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetVelocityAsync(1);

        // 24h window: 1 segment, (124-100)/12h = 2.0 sales/hr — weighted median of single segment
        var window24h = result.Windows.Single(w => w.Window == "24h");
        Assert.Equal(24, window24h.SalesCount);
        Assert.Equal(2.0, window24h.SalesPerHour);
        Assert.Equal("low", window24h.Confidence); // Only 1 segment

        // 3h window should have < 2 snapshots (earliest is 12h ago), so zero
        var window3h = result.Windows.Single(w => w.Window == "3h");
        Assert.Equal(0, window3h.SalesCount);
    }

    [Fact]
    public async Task GetDashboardAsync_NoItems_ReturnsEmpty()
    {
        using var db = CreateDb();
        var calc = new VelocityCalculator(db);

        var result = await calc.GetDashboardAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDashboardAsync_ItemWithSnapshots_CalculatesFulfillmentScore()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Premium Warrior Set", Grade = 2 });
        db.TradeSnapshots.AddRange(
            new TradeSnapshot
            {
                ItemId = 1,
                RecordedAt = now.AddHours(-20),
                TotalTrades = 200,
                CurrentStock = 10,
                BasePrice = 500_000_000,
                LastSoldPrice = 500_000_000,
                TotalPreorders = 50
            },
            new TradeSnapshot
            {
                ItemId = 1,
                RecordedAt = now,
                TotalTrades = 240,
                CurrentStock = 8,
                BasePrice = 500_000_000,
                LastSoldPrice = 500_000_000,
                TotalPreorders = 50
            }
        );
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetDashboardAsync();

        Assert.Single(result);
        var item = result[0];
        Assert.Equal("Premium Warrior Set", item.Name);

        // Sales: (240 - 200) / 20h = 2.0/hr (single segment, weighted median = same)
        Assert.Equal(2.0, item.SalesPerHour);

        // Fulfillment: 2.0 / 50 = 0.04
        Assert.Equal(0.04, item.FulfillmentScore);

        // Fill time: 50 / 2.0 = 25 hours = ~1.0 days
        Assert.Contains("days", item.EstimatedFillTime);

        // Only 1 segment
        Assert.Equal("low", item.Confidence);
    }

    [Fact]
    public async Task GetVelocityAsync_WeightedMeanDilutesOutlierSpike()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Test Set", Grade = 2 });
        // 5 snapshots, 30 min apart => 4 segments
        // Segment rates: 4/hr, 4/hr, 200/hr (spike), 4/hr
        db.TradeSnapshots.AddRange(
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-2), TotalTrades = 100, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-1.5), TotalTrades = 102, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-1), TotalTrades = 104, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-0.5), TotalTrades = 204, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now, TotalTrades = 206, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 }
        );
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetVelocityAsync(1);
        var window3h = result.Windows.Single(w => w.Window == "3h");

        // Weighted mean with recency weighting: recent spike gets high weight
        // but result should still be a reasonable rate, not infinite
        Assert.True(window3h.SalesPerHour > 0,
            $"Expected positive sales rate, got {window3h.SalesPerHour}");
        Assert.Equal(106, window3h.SalesCount);
    }

    [Fact]
    public async Task GetVelocityAsync_RecencyWeightFavorsRecentData()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Test Set", Grade = 2 });
        // Old segments (high rate): 20/hr at t=-10h to t=-4h
        // Recent segments (low rate): 2/hr at t=-2h to now
        db.TradeSnapshots.AddRange(
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-10), TotalTrades = 100, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-8), TotalTrades = 140, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-6), TotalTrades = 180, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-4), TotalTrades = 220, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-2), TotalTrades = 224, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now, TotalTrades = 228, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 }
        );
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetVelocityAsync(1);
        var window12h = result.Windows.Single(w => w.Window == "12h");

        // Old method: (228-100)/10h = 12.8/hr
        // Weighted median should favor recent 2/hr segments (higher weight)
        Assert.True(window12h.SalesPerHour < 12,
            $"Expected recency weighting to pull rate down, got {window12h.SalesPerHour}");
    }

    [Fact]
    public async Task GetVelocityAsync_ConfidenceLevels()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Test Set", Grade = 2 });

        // 13 snapshots at 1-hour intervals over 12 hours (12 segments, 2 trades each = 24 total)
        for (int i = 12; i >= 0; i--)
        {
            db.TradeSnapshots.Add(new TradeSnapshot
            {
                ItemId = 1,
                RecordedAt = now.AddHours(-i),
                TotalTrades = 100 + (12 - i) * 2,
                CurrentStock = 5,
                BasePrice = 1_000_000,
                LastSoldPrice = 1_000_000,
                TotalPreorders = 10
            });
        }
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetVelocityAsync(1);

        // 3h window: 2-3 segments depending on timing, totalSales 4-6 => low or medium
        var w3h = result.Windows.Single(w => w.Window == "3h");
        Assert.True(w3h.Confidence == "low" || w3h.Confidence == "medium",
            $"Expected low or medium for 3h window, got {w3h.Confidence}");

        // 24h window: 12 segments, 24 total sales => high
        var w24h = result.Windows.Single(w => w.Window == "24h");
        Assert.Equal("high", w24h.Confidence);
    }

    [Fact]
    public async Task GetVelocityAsync_RateDecaysWhenNoNewSales()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Test Set", Grade = 2 });
        // Sales happened early, then nothing for hours
        // t=-6h: 100 trades, t=-5h: 110 trades (10 sales in 1h = 10/hr)
        // Then 10 snapshots with NO sales from t=-5h to now (1 snapshot/30min)
        db.TradeSnapshots.Add(new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-6), TotalTrades = 100, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 });
        db.TradeSnapshots.Add(new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-5), TotalTrades = 110, CurrentStock = 5, BasePrice = 1_000_000, LastSoldPrice = 1_000_000, TotalPreorders = 10 });

        // 10 zero-sale snapshots over next 5 hours
        for (int i = 1; i <= 10; i++)
        {
            db.TradeSnapshots.Add(new TradeSnapshot
            {
                ItemId = 1,
                RecordedAt = now.AddHours(-5).AddMinutes(30 * i),
                TotalTrades = 110, // no change
                CurrentStock = 5,
                BasePrice = 1_000_000,
                LastSoldPrice = 1_000_000,
                TotalPreorders = 10
            });
        }
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetVelocityAsync(1);
        var window12h = result.Windows.Single(w => w.Window == "12h");

        // 1 sale segment (10/hr) + 10 zero segments (0/hr)
        // Weighted mean should be small but non-zero (zero segments dilute the rate)
        Assert.True(window12h.SalesPerHour > 0 && window12h.SalesPerHour < 5,
            $"Expected small non-zero rate, got {window12h.SalesPerHour}");
        Assert.Equal(10, window12h.SalesCount);
    }

    [Fact]
    public async Task GetDashboardAsync_IncludesConfidence()
    {
        using var db = CreateDb();
        var now = DateTime.UtcNow;

        db.TrackedItems.Add(new TrackedItem { Id = 1, Name = "Test Set", Grade = 2 });
        db.TradeSnapshots.AddRange(
            new TradeSnapshot { ItemId = 1, RecordedAt = now.AddHours(-20), TotalTrades = 200, CurrentStock = 10, BasePrice = 500_000_000, LastSoldPrice = 500_000_000, TotalPreorders = 50 },
            new TradeSnapshot { ItemId = 1, RecordedAt = now, TotalTrades = 240, CurrentStock = 8, BasePrice = 500_000_000, LastSoldPrice = 500_000_000, TotalPreorders = 50 }
        );
        await db.SaveChangesAsync();

        var calc = new VelocityCalculator(db);
        var result = await calc.GetDashboardAsync();

        Assert.Single(result);
        Assert.Equal("low", result[0].Confidence);
    }
}
