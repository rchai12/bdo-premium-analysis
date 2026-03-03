namespace BdoMarketTracker.Dtos;

public class VelocityDto
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<VelocityWindowDto> Windows { get; set; } = [];
}

public class VelocityWindowDto
{
    public string Window { get; set; } = string.Empty;
    public long SalesCount { get; set; }
    public double SalesPerHour { get; set; }
    public double AvgPreorders { get; set; }
    public string Confidence { get; set; } = "low";
}
