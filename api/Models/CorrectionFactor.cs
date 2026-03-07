using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BdoMarketTracker.Models;

[Table("correction_factors")]
public class CorrectionFactor
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("item_id")]
    public int ItemId { get; set; }

    [Column("window")]
    [MaxLength(10)]
    public string Window { get; set; } = string.Empty;

    [Column("factor")]
    public double Factor { get; set; } = 1.0;

    [Column("sample_count")]
    public int SampleCount { get; set; }

    [Column("last_updated")]
    public DateTime LastUpdated { get; set; }

    [ForeignKey(nameof(ItemId))]
    public TrackedItem Item { get; set; } = null!;
}
