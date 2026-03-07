using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BdoMarketTracker.Models;

[Table("tracked_items")]
public class TrackedItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("grade")]
    public int Grade { get; set; }

    public ICollection<TradeSnapshot> Snapshots { get; set; } = [];
    public ICollection<DailySummary> DailySummaries { get; set; } = [];
    public ICollection<VelocityPrediction> VelocityPredictions { get; set; } = [];
    public ICollection<CorrectionFactor> CorrectionFactors { get; set; } = [];
}
