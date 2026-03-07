using BdoMarketTracker.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BdoMarketTracker.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<TrackedItem> TrackedItems => Set<TrackedItem>();
    public DbSet<TradeSnapshot> TradeSnapshots => Set<TradeSnapshot>();
    public DbSet<DailySummary> DailySummaries => Set<DailySummary>();
    public DbSet<VelocityPrediction> VelocityPredictions => Set<VelocityPrediction>();
    public DbSet<CorrectionFactor> CorrectionFactors => Set<CorrectionFactor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TradeSnapshot>(entity =>
        {
            entity.HasIndex(e => new { e.ItemId, e.RecordedAt })
                  .IsDescending(false, true);

            entity.HasOne(e => e.Item)
                  .WithMany(i => i.Snapshots)
                  .HasForeignKey(e => e.ItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DailySummary>(entity =>
        {
            entity.HasIndex(e => new { e.ItemId, e.Date })
                  .IsUnique();

            entity.HasOne(e => e.Item)
                  .WithMany(i => i.DailySummaries)
                  .HasForeignKey(e => e.ItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VelocityPrediction>(entity =>
        {
            entity.HasIndex(e => e.EvaluationDueAt)
                  .HasFilter("evaluated_at IS NULL");

            entity.HasIndex(e => new { e.ItemId, e.Window, e.PredictedAt })
                  .IsDescending(false, false, true);

            entity.HasOne(e => e.Item)
                  .WithMany(i => i.VelocityPredictions)
                  .HasForeignKey(e => e.ItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CorrectionFactor>(entity =>
        {
            entity.HasIndex(e => new { e.ItemId, e.Window })
                  .IsUnique();

            entity.HasOne(e => e.Item)
                  .WithMany(i => i.CorrectionFactors)
                  .HasForeignKey(e => e.ItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
