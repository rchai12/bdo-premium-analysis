using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BdoMarketTracker.Models;

[Table("trade_snapshots")]
public class TradeSnapshot
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("item_id")]
    public int ItemId { get; set; }

    [Column("recorded_at")]
    public DateTime RecordedAt { get; set; }

    [Column("total_trades")]
    public long TotalTrades { get; set; }

    [Column("current_stock")]
    public long CurrentStock { get; set; }

    [Column("base_price")]
    public long BasePrice { get; set; }

    [Column("last_sold_price")]
    public long LastSoldPrice { get; set; }

    [Column("total_preorders")]
    public long TotalPreorders { get; set; }

    [ForeignKey(nameof(ItemId))]
    public TrackedItem Item { get; set; } = null!;
}
