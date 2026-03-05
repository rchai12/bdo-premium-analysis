using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BdoMarketTracker.Models;

[Table("daily_summaries")]
public class DailySummary
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("item_id")]
    public int ItemId { get; set; }

    [Column("date")]
    public DateOnly Date { get; set; }

    [Column("sales_count")]
    public long SalesCount { get; set; }

    [Column("avg_base_price")]
    public long AvgBasePrice { get; set; }

    [Column("avg_preorders")]
    public long AvgPreorders { get; set; }

    [Column("snapshot_count")]
    public int SnapshotCount { get; set; }

    [ForeignKey(nameof(ItemId))]
    public TrackedItem Item { get; set; } = null!;
}
