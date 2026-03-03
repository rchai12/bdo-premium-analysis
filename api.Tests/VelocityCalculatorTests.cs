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

        // 24h window should include both snapshots: (124 - 100) / 12h = 2.0 sales/hr
        var window24h = result.Windows.Single(w => w.Window == "24h");
        Assert.Equal(24, window24h.SalesCount);
        Assert.Equal(2.0, window24h.SalesPerHour);

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

        // Sales: (240 - 200) / 20h = 2.0/hr
        Assert.Equal(2.0, item.SalesPerHour24h);

        // Fulfillment: 2.0 / 50 = 0.04
        Assert.Equal(0.04, item.FulfillmentScore);

        // Fill time: 50 / 2.0 = 25 hours = ~1.0 days
        Assert.Contains("days", item.EstimatedFillTime);
    }
}
