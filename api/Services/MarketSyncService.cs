using System.Net.WebSockets;
using System.Text;
using BdoMarketTracker.Data;
using BdoMarketTracker.Models;
using Microsoft.EntityFrameworkCore;

namespace BdoMarketTracker.Services;

public class MarketSyncService(
    IServiceScopeFactory scopeFactory,
    IArshaApiClient arshaClient,
    ILogger<MarketSyncService> logger,
    IConfiguration config) : BackgroundService
{
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SnapshotDebounce = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(5);
    private const int BatchSize = 20;
    private readonly string _region = config.GetValue("Arsha:Region", "na")!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for the app to fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await SyncTrackedItemsAsync(stoppingToken);
        await ValidateTrackedItemsAsync(stoppingToken);
        await TakeSnapshotAsync(stoppingToken);

        var reconnectDelay = InitialReconnectDelay;

        // Try WebSocket-driven sync with exponential backoff on failure
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunWebSocketSyncAsync(stoppingToken);
                reconnectDelay = InitialReconnectDelay; // reset on clean exit
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "WebSocket sync failed, retrying in {Delay}s", reconnectDelay.TotalSeconds);

                try
                {
                    await Task.Delay(reconnectDelay, stoppingToken);
                    await TakeSnapshotAsync(stoppingToken);
                }
                catch (Exception pollEx) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(pollEx, "Snapshot during backoff also failed");
                }

                reconnectDelay = TimeSpan.FromSeconds(
                    Math.Min(reconnectDelay.TotalSeconds * 2, MaxReconnectDelay.TotalSeconds));
            }
        }
    }

    private async Task RunWebSocketSyncAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        var wsUrl = config.GetValue("Arsha:WebSocketUrl", "wss://api.arsha.io/events")!;
        logger.LogInformation("Connecting to arsha.io WebSocket at {Url}", wsUrl);

        await ws.ConnectAsync(new Uri(wsUrl), ct);
        logger.LogInformation("WebSocket connected");

        var buffer = new byte[4096];
        var lastSnapshot = DateTime.UtcNow;

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogInformation("WebSocket closed by server");
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            // Check if this is a cache expiry event (not a heartbeat)
            if (message.Contains("ExpiredEvent", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("expired", StringComparison.OrdinalIgnoreCase))
            {
                // Debounce: don't snapshot more often than SnapshotDebounce interval
                if (DateTime.UtcNow - lastSnapshot > SnapshotDebounce)
                {
                    logger.LogInformation("Cache expired event received, taking snapshot");
                    await TakeSnapshotAsync(ct);
                    lastSnapshot = DateTime.UtcNow;
                }
            }
            else if (message.Contains("heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                // Heartbeat - check if we should do a fallback snapshot
                if (DateTime.UtcNow - lastSnapshot > FallbackInterval)
                {
                    logger.LogInformation("Fallback interval reached during WebSocket, taking snapshot");
                    await TakeSnapshotAsync(ct);
                    lastSnapshot = DateTime.UtcNow;
                }
            }
        }
    }

    private async Task SyncTrackedItemsAsync(CancellationToken ct)
    {
        try
        {
            var allItems = await arshaClient.GetItemDatabaseAsync(ct);
            var premiumItems = allItems
                .Where(i => i.Name.Contains("Premium", StringComparison.OrdinalIgnoreCase)
                         && i.Name.Contains("Set", StringComparison.OrdinalIgnoreCase)
                         && !i.Name.Contains("Days)", StringComparison.OrdinalIgnoreCase)
                         && !i.Name.Contains("Day)", StringComparison.OrdinalIgnoreCase))
                .ToList();

            logger.LogInformation("Found {Count} premium set items in database", premiumItems.Count);

            if (premiumItems.Count < 100)
                logger.LogWarning("Suspiciously few premium sets ({Count}). The /util/db response may have been truncated or timed out", premiumItems.Count);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingIds = await db.TrackedItems.Select(i => i.Id).ToHashSetAsync(ct);

            var newItems = premiumItems
                .Where(i => !existingIds.Contains(i.Id))
                .Select(i => new TrackedItem
                {
                    Id = i.Id,
                    Name = i.Name,
                    Grade = i.Grade
                })
                .ToList();

            if (newItems.Count > 0)
            {
                db.TrackedItems.AddRange(newItems);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Added {Count} new tracked items", newItems.Count);
            }

            // Update names for existing items in case they changed
            foreach (var apiItem in premiumItems.Where(i => existingIds.Contains(i.Id)))
            {
                var existing = await db.TrackedItems.FindAsync([apiItem.Id], ct);
                if (existing != null && existing.Name != apiItem.Name)
                {
                    existing.Name = apiItem.Name;
                }
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync tracked items");
        }
    }

    private async Task ValidateTrackedItemsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var trackedItems = await db.TrackedItems.ToListAsync(ct);
            logger.LogInformation("Validating {Count} tracked items against market API", trackedItems.Count);

            // Remove time-limited items that slipped through earlier syncs
            var timedItems = trackedItems
                .Where(i => i.Name.Contains("Days)", StringComparison.OrdinalIgnoreCase)
                         || i.Name.Contains("Day)", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (timedItems.Count > 0)
            {
                db.TrackedItems.RemoveRange(timedItems);
                logger.LogInformation("Removed {Count} time-limited items", timedItems.Count);
                trackedItems = trackedItems.Except(timedItems).ToList();
            }

            // Validate each item individually against the market API
            var invalidIds = new List<int>();
            foreach (var item in trackedItems)
            {
                try
                {
                    var result = await arshaClient.GetItemDataAsync([item.Id], _region, ct);
                    if (result.Count == 0)
                        invalidIds.Add(item.Id);
                }
                catch
                {
                    invalidIds.Add(item.Id);
                }

                await Task.Delay(RequestDelay, ct);
            }

            if (invalidIds.Count > 0)
            {
                var toRemove = await db.TrackedItems.Where(i => invalidIds.Contains(i.Id)).ToListAsync(ct);
                db.TrackedItems.RemoveRange(toRemove);
                logger.LogInformation("Removed {Count} items that returned 404 from market API", toRemove.Count);
            }

            await db.SaveChangesAsync(ct);
            var remaining = await db.TrackedItems.CountAsync(ct);
            logger.LogInformation("Validation complete: {Count} valid tracked items remaining", remaining);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to validate tracked items");
        }
    }

    private async Task TakeSnapshotAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var trackedIds = await db.TrackedItems.Select(i => i.Id).ToListAsync(ct);
            if (trackedIds.Count == 0)
            {
                logger.LogWarning("No tracked items found, skipping snapshot");
                return;
            }

            logger.LogInformation("Taking snapshot for {Count} items", trackedIds.Count);
            var now = DateTime.UtcNow;
            var snapshots = new List<TradeSnapshot>();

            // Batch fetch item data (smaller batches to avoid 404s)
            var batches = trackedIds.Chunk(BatchSize);
            foreach (var batch in batches)
            {
                List<ArshaMarketItem> marketItems;
                try
                {
                    marketItems = await arshaClient.GetItemDataAsync(batch, _region, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Failed to fetch batch of {Count} items, skipping", batch.Length);
                    continue;
                }

                foreach (var item in marketItems.Where(m => m.Sid == 0))
                {
                    try
                    {
                        await Task.Delay(RequestDelay, ct);
                        var orderBook = await arshaClient.GetOrderBookAsync(item.Id, 0, _region, ct);
                        var totalPreorders = orderBook?.Orders.Sum(o => o.Buyers) ?? 0;

                        snapshots.Add(new TradeSnapshot
                        {
                            ItemId = item.Id,
                            RecordedAt = now,
                            TotalTrades = item.TotalTrades,
                            CurrentStock = item.CurrentStock,
                            BasePrice = item.BasePrice,
                            LastSoldPrice = item.LastSoldPrice,
                            TotalPreorders = totalPreorders
                        });
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Failed to fetch order book for item {ItemId}, skipping", item.Id);
                    }
                }

                await Task.Delay(RequestDelay, ct);
            }

            db.TradeSnapshots.AddRange(snapshots);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Saved {Count} snapshots", snapshots.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to take snapshot");
        }
    }
}
