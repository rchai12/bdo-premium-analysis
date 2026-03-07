using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BdoMarketTracker.Models;

[Table("velocity_predictions")]
public class VelocityPrediction
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("item_id")]
    public int ItemId { get; set; }

    [Column("window")]
    [MaxLength(10)]
    public string Window { get; set; } = string.Empty;

    [Column("predicted_at")]
    public DateTime PredictedAt { get; set; }

    [Column("predicted_sales_per_hour")]
    public double PredictedSalesPerHour { get; set; }

    [Column("predicted_preorders")]
    public long PredictedPreorders { get; set; }

    [Column("evaluation_due_at")]
    public DateTime EvaluationDueAt { get; set; }

    [Column("actual_sales_per_hour")]
    public double? ActualSalesPerHour { get; set; }

    [Column("actual_preorders")]
    public long? ActualPreorders { get; set; }

    [Column("accuracy_ratio")]
    public double? AccuracyRatio { get; set; }

    [Column("evaluated_at")]
    public DateTime? EvaluatedAt { get; set; }

    [ForeignKey(nameof(ItemId))]
    public TrackedItem Item { get; set; } = null!;
}
