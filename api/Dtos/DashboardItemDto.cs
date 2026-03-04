namespace BdoMarketTracker.Dtos;

public class DashboardItemDto
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Grade { get; set; }
    public long TotalPreorders { get; set; }
    public long SalesCount { get; set; }
    public double SalesPerHour { get; set; }
    public string Window { get; set; } = "24h";
    public double FulfillmentScore { get; set; }
    public string EstimatedFillTime { get; set; } = string.Empty;
    public string Confidence { get; set; } = "low";
}
