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
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CompactionInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PredictionRetention = TimeSpan.FromDays(30);
    private readonly string _region = config.GetValue("Arsha:Region", "na")!;

    private const double EmaAlpha = 0.15;
    private const double EmaAlphaWarmup = 0.3;
    private const int WarmupSamples = 10;

    // Windows to track predictions for (subset of VelocityCalculator.WindowDefinitions)
    private static readonly Dictionary<string, TimeSpan> EvaluationHorizons = new()
    {
        ["24h"] = TimeSpan.FromHours(6),
        ["3d"] = TimeSpan.FromHours(12),
        ["7d"] = TimeSpan.FromHours(24),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for the app to fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await SyncTrackedItemsAsync(stoppingToken);
        await ValidateTrackedItemsAsync(stoppingToken);
        await TakeSnapshotAsync(stoppingToken);
        await EvaluatePredictionsAsync(stoppingToken);
        await LogPredictionsAsync(stoppingToken);
        await CompactSnapshotsAsync(stoppingToken);

        var reconnectDelay = InitialReconnectDelay;
        var lastCompaction = DateTime.UtcNow;

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
                    await EvaluatePredictionsAsync(stoppingToken);
                    await LogPredictionsAsync(stoppingToken);

                    if (DateTime.UtcNow - lastCompaction > CompactionInterval)
                    {
                        await CompactSnapshotsAsync(stoppingToken);
                        lastCompaction = DateTime.UtcNow;
                    }
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
        var lastCompaction = DateTime.UtcNow;

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
                    await EvaluatePredictionsAsync(ct);
                    await LogPredictionsAsync(ct);
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
                    await EvaluatePredictionsAsync(ct);
                    await LogPredictionsAsync(ct);
                    lastSnapshot = DateTime.UtcNow;
                }

                // Daily compaction check
                if (DateTime.UtcNow - lastCompaction > CompactionInterval)
                {
                    await CompactSnapshotsAsync(ct);
                    lastCompaction = DateTime.UtcNow;
                }
            }
        }
    }

    private async Task LogPredictionsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var velocityCalc = new VelocityCalculator(db);

            var items = await db.TrackedItems.ToListAsync(ct);
            if (items.Count == 0) return;

            var now = DateTime.UtcNow;

            // Check which (item, window) pairs already have a pending prediction
            var pendingKeys = await db.VelocityPredictions
                .Where(p => p.EvaluatedAt == null)
                .Select(p => new { p.ItemId, p.Window })
                .ToListAsync(ct);
            var pendingSet = pendingKeys.ToHashSet();

            var predictions = new List<VelocityPrediction>();

            foreach (var (window, horizon) in EvaluationHorizons)
            {
                // Get dashboard data for this window (reuses VelocityCalculator without correction — raw predictions)
                var dashboard = await velocityCalc.GetDashboardAsync(window, ct);

                foreach (var item in dashboard)
                {
                    // Skip if already have a pending prediction or low confidence
                    if (pendingSet.Contains(new { ItemId = item.ItemId, Window = window }))
                        continue;
                    if (item.Confidence == "low")
                        continue;

                    predictions.Add(new VelocityPrediction
                    {
                        ItemId = item.ItemId,
                        Window = window,
                        PredictedAt = now,
                        PredictedSalesPerHour = item.RawSalesPerHour,
                        PredictedPreorders = item.TotalPreorders,
                        EvaluationDueAt = now + horizon,
                    });
                }
            }

            if (predictions.Count > 0)
            {
                db.VelocityPredictions.AddRange(predictions);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Logged {Count} velocity predictions", predictions.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to log predictions");
        }
    }

    private async Task EvaluatePredictionsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var now = DateTime.UtcNow;
            var pending = await db.VelocityPredictions
                .Where(p => p.EvaluatedAt == null && p.EvaluationDueAt <= now)
                .ToListAsync(ct);

            if (pending.Count == 0) return;

            logger.LogInformation("Evaluating {Count} pending predictions", pending.Count);
            var evaluated = 0;

            foreach (var prediction in pending)
            {
                // Get snapshots between prediction time and evaluation due time
                var snapshots = await db.TradeSnapshots
                    .Where(s => s.ItemId == prediction.ItemId
                             && s.RecordedAt >= prediction.PredictedAt
                             && s.RecordedAt <= prediction.EvaluationDueAt)
                    .OrderBy(s => s.RecordedAt)
                    .ToListAsync(ct);

                if (snapshots.Count < 2)
                {
                    // Not enough data to evaluate — extend deadline by the horizon
                    var window = prediction.Window;
                    if (EvaluationHorizons.TryGetValue(window, out var horizon))
                        prediction.EvaluationDueAt = now + horizon;
                    continue;
                }

                var first = snapshots.First();
                var last = snapshots.Last();
                var hours = (last.RecordedAt - first.RecordedAt).TotalHours;

                if (hours <= 0) continue;

                var actualSales = last.TotalTrades - first.TotalTrades;
                if (actualSales < 0) actualSales = 0;

                var actualSalesPerHour = (double)actualSales / hours;

                prediction.ActualSalesPerHour = actualSalesPerHour;
                prediction.ActualPreorders = last.TotalPreorders;
                prediction.EvaluatedAt = now;

                // Compute accuracy ratio
                if (prediction.PredictedSalesPerHour > 0)
                {
                    prediction.AccuracyRatio = actualSalesPerHour / prediction.PredictedSalesPerHour;
                }
                else if (actualSalesPerHour == 0)
                {
                    prediction.AccuracyRatio = 1.0; // Both zero — correct
                }
                else
                {
                    prediction.AccuracyRatio = 2.0; // Predicted zero but sales happened — cap
                }

                // Clamp to prevent extreme outliers
                prediction.AccuracyRatio = Math.Clamp(prediction.AccuracyRatio.Value, 0.1, 3.0);

                // Update correction factor via EMA
                await UpdateCorrectionFactorAsync(db, prediction.ItemId, prediction.Window, prediction.AccuracyRatio.Value, ct);
                evaluated++;
            }

            await db.SaveChangesAsync(ct);

            if (evaluated > 0)
            {
                // Invalidate cache so VelocityCalculator picks up new factors
                var correctionProvider = scope.ServiceProvider.GetService<ICorrectionFactorProvider>();
                correctionProvider?.InvalidateCache();

                logger.LogInformation("Evaluated {Count} predictions, updated correction factors", evaluated);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to evaluate predictions");
        }
    }

    private static async Task UpdateCorrectionFactorAsync(AppDbContext db, int itemId, string window, double accuracyRatio, CancellationToken ct)
    {
        var existing = await db.CorrectionFactors
            .FirstOrDefaultAsync(f => f.ItemId == itemId && f.Window == window, ct);

        if (existing == null)
        {
            db.CorrectionFactors.Add(new CorrectionFactor
            {
                ItemId = itemId,
                Window = window,
                Factor = Math.Clamp(accuracyRatio, 0.2, 3.0),
                SampleCount = 1,
                LastUpdated = DateTime.UtcNow,
            });
        }
        else
        {
            var alpha = existing.SampleCount < WarmupSamples ? EmaAlphaWarmup : EmaAlpha;
            existing.Factor = existing.Factor * (1 - alpha) + accuracyRatio * alpha;
            existing.Factor = Math.Clamp(existing.Factor, 0.2, 3.0);
            existing.SampleCount++;
            existing.LastUpdated = DateTime.UtcNow;
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

    private async Task CompactSnapshotsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = DateTime.UtcNow - RetentionPeriod;
            var oldSnapshots = await db.TradeSnapshots
                .Where(s => s.RecordedAt < cutoff)
                .OrderBy(s => s.ItemId)
                .ThenBy(s => s.RecordedAt)
                .ToListAsync(ct);

            if (oldSnapshots.Count == 0)
            {
                logger.LogInformation("No snapshots older than {Days} days to compact", RetentionPeriod.TotalDays);
            }
            else
            {
                logger.LogInformation("Compacting {Count} snapshots older than {Cutoff:u}", oldSnapshots.Count, cutoff);

                // Group by item and date, aggregate into daily summaries
                var groups = oldSnapshots
                    .GroupBy(s => new { s.ItemId, Date = DateOnly.FromDateTime(s.RecordedAt) });

                var summaries = new List<DailySummary>();
                foreach (var group in groups)
                {
                    var sorted = group.OrderBy(s => s.RecordedAt).ToList();
                    var salesCount = sorted.Last().TotalTrades - sorted.First().TotalTrades;
                    if (salesCount < 0) salesCount = 0;

                    summaries.Add(new DailySummary
                    {
                        ItemId = group.Key.ItemId,
                        Date = group.Key.Date,
                        SalesCount = salesCount,
                        AvgBasePrice = (long)sorted.Average(s => s.BasePrice),
                        AvgPreorders = (long)sorted.Average(s => s.TotalPreorders),
                        SnapshotCount = sorted.Count,
                    });
                }

                // Upsert: skip dates that already have a summary
                var existingKeys = await db.DailySummaries
                    .Where(d => d.Date < DateOnly.FromDateTime(cutoff))
                    .Select(d => new { d.ItemId, d.Date })
                    .ToListAsync(ct);
                var existingSet = existingKeys.ToHashSet();

                var newSummaries = summaries
                    .Where(s => !existingSet.Contains(new { s.ItemId, s.Date }))
                    .ToList();

                if (newSummaries.Count > 0)
                    db.DailySummaries.AddRange(newSummaries);

                db.TradeSnapshots.RemoveRange(oldSnapshots);
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Compaction complete: {Deleted} snapshots removed, {Created} daily summaries created",
                    oldSnapshots.Count, newSummaries.Count);
            }

            // Clean up old evaluated predictions
            var predictionCutoff = DateTime.UtcNow - PredictionRetention;
            var oldPredictions = await db.VelocityPredictions
                .Where(p => p.EvaluatedAt != null && p.EvaluatedAt < predictionCutoff)
                .ToListAsync(ct);

            if (oldPredictions.Count > 0)
            {
                db.VelocityPredictions.RemoveRange(oldPredictions);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Cleaned up {Count} old evaluated predictions", oldPredictions.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compact snapshots");
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
