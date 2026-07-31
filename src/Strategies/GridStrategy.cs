using CryptoTradingBot.Configuration;
using CryptoTradingBot.Exchange;
using CryptoTradingBot.Models;
using CryptoTradingBot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoTradingBot.Strategies;

public interface ITradingStrategy
{
    string Name { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task ExecuteTickAsync(Ticker ticker, CancellationToken ct = default);
    IReadOnlyList<TradeResult> GetTradeHistory();
}

/// <summary>
/// Grid Trading Bot - places buy/sell orders at evenly-spaced price levels.
/// When a buy fills, it places a sell one level up. When a sell fills, it places a buy one level down.
/// Ideal for sideways/range-bound markets (ETH/USDT, SOL/USDT).
/// </summary>
public sealed class GridStrategy : ITradingStrategy
{
    public string Name => "Grid";

    private readonly IExchangeClient _exchange;
    private readonly TradingConfig _config;
    private readonly RiskManager _risk;
    private readonly ILogger<GridStrategy> _logger;
    private readonly List<GridLevel> _gridLevels = [];
    private readonly List<TradeResult> _trades = [];

    public GridStrategy(
        IExchangeClient exchange,
        IOptions<BotConfiguration> options,
        RiskManager risk,
        ILogger<GridStrategy> logger
    )
    {
        _exchange = exchange;
        _config = options.Value.Trading;
        _risk = risk;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _gridLevels.Clear();
        var step = (_config.GridUpperPrice - _config.GridLowerPrice) / _config.GridLevels;

        _logger.LogInformation(
            "Initializing Grid: {Symbol} | Range: {Lower}-{Upper} | Levels: {N} | Step: {Step:F2}",
            _config.Symbol,
            _config.GridLowerPrice,
            _config.GridUpperPrice,
            _config.GridLevels,
            step
        );

        var ticker = await _exchange.GetTickerAsync(_config.Symbol, ct);

        for (int i = 0; i < _config.GridLevels; i++)
        {
            var buyPrice = _config.GridLowerPrice + (step * i);
            var sellPrice = buyPrice + step;

            var level = new GridLevel
            {
                Level = i,
                BuyPrice = Math.Round(buyPrice, 2),
                SellPrice = Math.Round(sellPrice, 2),
                HasPosition = ticker.Price > buyPrice, // Assume we have position if price is above buy level
            };

            _gridLevels.Add(level);
            _logger.LogDebug(
                "Grid Level {N}: Buy={Buy:F2}, Sell={Sell:F2}, HasPos={Pos}",
                i,
                level.BuyPrice,
                level.SellPrice,
                level.HasPosition
            );
        }

        // Place initial orders
        await PlaceGridOrdersAsync(ticker.Price, ct);
    }

    public async Task ExecuteTickAsync(Ticker ticker, CancellationToken ct = default)
    {
        _logger.LogDebug("Grid tick: {Symbol} @ {Price:F2}", ticker.Symbol, ticker.Price);

        // Check for filled orders and react
        foreach (var level in _gridLevels)
        {
            await CheckAndHandleBuyFillAsync(level, ticker, ct);
            await CheckAndHandleSellFillAsync(level, ticker, ct);
        }
    }

    private async Task CheckAndHandleBuyFillAsync(
        GridLevel level,
        Ticker ticker,
        CancellationToken ct
    )
    {
        if (level.ActiveBuyOrder is null || level.ActiveBuyOrder.Status != OrderStatus.New)
            return;

        var orderStatus = await _exchange.GetOrderStatusAsync(
            _config.Symbol,
            level.ActiveBuyOrder.ExchangeOrderId,
            ct
        );

        if (orderStatus?.Status == OrderStatus.Filled)
        {
            level.ActiveBuyOrder.Status = OrderStatus.Filled;
            level.HasPosition = true;

            var trade = new TradeResult
            {
                OrderId = level.ActiveBuyOrder.ExchangeOrderId,
                Symbol = _config.Symbol,
                Side = OrderSide.Buy,
                Price = level.BuyPrice,
                Quantity = level.ActiveBuyOrder.Quantity,
                Fee = level.BuyPrice * level.ActiveBuyOrder.Quantity * 0.001m, // ~0.1% fee
            };
            _trades.Add(trade);
            _risk.RecordTrade(trade);

            _logger.LogInformation(
                "✅ Grid BUY filled @ {Price:F2} (Level {N})",
                level.BuyPrice,
                level.Level
            );

            // Place sell at upper grid
            await PlaceSellOrderAsync(level, ct);
        }
    }

    private async Task CheckAndHandleSellFillAsync(
        GridLevel level,
        Ticker ticker,
        CancellationToken ct
    )
    {
        if (level.ActiveSellOrder is null || level.ActiveSellOrder.Status != OrderStatus.New)
            return;

        var orderStatus = await _exchange.GetOrderStatusAsync(
            _config.Symbol,
            level.ActiveSellOrder.ExchangeOrderId,
            ct
        );

        if (orderStatus?.Status == OrderStatus.Filled)
        {
            level.ActiveSellOrder.Status = OrderStatus.Filled;
            level.HasPosition = false;

            var pnl = (level.SellPrice - level.BuyPrice) * level.ActiveSellOrder.Quantity;
            var trade = new TradeResult
            {
                OrderId = level.ActiveSellOrder.ExchangeOrderId,
                Symbol = _config.Symbol,
                Side = OrderSide.Sell,
                Price = level.SellPrice,
                Quantity = level.ActiveSellOrder.Quantity,
                PnL = pnl,
                Fee = level.SellPrice * level.ActiveSellOrder.Quantity * 0.001m,
            };
            _trades.Add(trade);
            _risk.RecordTrade(trade);

            _logger.LogInformation(
                "✅ Grid SELL filled @ {Price:F2} (Level {N}) | PnL: {PnL:F2}",
                level.SellPrice,
                level.Level,
                pnl
            );

            // Place buy at lower grid
            await PlaceBuyOrderAsync(level, ct);
        }
    }

    private async Task PlaceGridOrdersAsync(decimal currentPrice, CancellationToken ct)
    {
        foreach (var level in _gridLevels)
        {
            if (!level.HasPosition && currentPrice > level.BuyPrice && level.ActiveBuyOrder is null)
                await PlaceBuyOrderAsync(level, ct);
            else if (
                level.HasPosition
                && currentPrice < level.SellPrice
                && level.ActiveSellOrder is null
            )
                await PlaceSellOrderAsync(level, ct);
        }
    }

    private async Task PlaceBuyOrderAsync(GridLevel level, CancellationToken ct)
    {
        var quantity = Math.Round(_config.OrderSizeUsdt / level.BuyPrice, 6);
        var orderValue = level.BuyPrice * quantity;

        if (!_risk.CanTrade(orderValue, OrderSide.Buy))
            return;

        level.ActiveBuyOrder = await _exchange.PlaceLimitOrderAsync(
            _config.Symbol,
            OrderSide.Buy,
            level.BuyPrice,
            quantity,
            ct
        );
        _logger.LogInformation("Placed {Side} LIMIT {Qty} {Symbol} @ {Price} → OrderId: {Id}", OrderSide.Buy, quantity, _config.Symbol, level.BuyPrice, level.ActiveBuyOrder.ExchangeOrderId);
        level.ActiveBuyOrder.Tag = $"Grid-L{level.Level}";
    }

    private async Task PlaceSellOrderAsync(GridLevel level, CancellationToken ct)
    {
        var quantity = Math.Round(_config.OrderSizeUsdt / level.SellPrice, 6);
        level.ActiveSellOrder = await _exchange.PlaceLimitOrderAsync(
            _config.Symbol,
            OrderSide.Sell,
            level.SellPrice,
            quantity,
            ct
        );
        _logger.LogInformation("Placed {Side} LIMIT {Qty} {Symbol} @ {Price} → OrderId: {Id}", OrderSide.Sell, quantity, _config.Symbol, level.SellPrice, level.ActiveSellOrder.ExchangeOrderId);
        level.ActiveSellOrder.Tag = $"Grid-L{level.Level}";
    }

    public IReadOnlyList<TradeResult> GetTradeHistory() => _trades.AsReadOnly();
}
