namespace CryptoTradingBot.Models;

public enum OrderSide { Buy, Sell }
public enum OrderStatus { New, Filled, Cancelled, PartiallyFilled, Rejected }
public enum OrderType { Limit, Market }

public sealed record Ticker(
    string Symbol,
    decimal Price,
    decimal BidPrice,
    decimal AskPrice,
    decimal Volume24h,
    DateTimeOffset Timestamp);

public sealed record Kline(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed class Order
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ExchangeOrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FilledAt { get; set; }
    public string? Tag { get; set; } // GridLevel, DCA-1, etc.
}

public sealed class TradeResult
{
    public string OrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal Fee { get; set; }
    public decimal PnL { get; set; }
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GridLevel
{
    public int Level { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal SellPrice { get; set; }
    public Order? ActiveBuyOrder { get; set; }
    public Order? ActiveSellOrder { get; set; }
    public bool HasPosition { get; set; }
}

public sealed class DailyPnL
{
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public int TradeCount { get; set; }
    public int WinCount { get; set; }
    public int LossCount { get; set; }
    public decimal TotalVolume { get; set; }
}
