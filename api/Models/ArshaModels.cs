namespace BdoMarketTracker.Models;

public class ArshaDbItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Grade { get; set; }
}

public class ArshaMarketItem
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
    public int Sid { get; set; }
    public long BasePrice { get; set; }
    public long CurrentStock { get; set; }
    public long TotalTrades { get; set; }
    public long LastSoldPrice { get; set; }
    public long LastSoldTime { get; set; }
}

public class ArshaOrderBook
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
    public int Sid { get; set; }
    public List<ArshaOrder> Orders { get; set; } = [];
}

public class ArshaOrder
{
    public long Price { get; set; }
    public long Sellers { get; set; }
    public long Buyers { get; set; }
}
