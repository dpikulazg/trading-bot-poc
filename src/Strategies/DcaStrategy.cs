using CryptoTradingBot.Configuration;
using CryptoTradingBot.Exchange;
using CryptoTradingBot.Models;
using CryptoTradingBot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoTradingBot.Strategies;

/// <summary>
/// DCA Bot — buys on dips with increasing position size, sells on recovery.
/// 1. Places initial buy order
/// 2. If price drops X%, places "safety order" with multiplied size
/// 3. Averages down the entry price
/// 4. Sells entire position when average price + take profit % is reached
/// Best for: bullish/choppy markets on high-cap coins (BTC, ETH, SOL).
/// </summary>
public sealed class DcaStrategy : ITradingStrategy
{
    public string Name => "DCA";

    private readonly IExchangeClient _exchange;
    private readonly TradingConfig _config;
    private readonly RiskManager _risk;
    private readonly ILogger<DcaStrategy> _logger;
    private readonly List<TradeResult> _trades = [];

    // Internal state
    private readonly List<(decimal Price, decimal Qty)> _entries = [];
    private decimal _averageEntryPrice;
    private decimal _totalQuantity;
    private int _currentSafetyOrder;
    private decimal _nextBuyTriggerPrice;
    private bool _hasActivePosition;

    public DcaStrategy(
        IExchangeClient exchange,
        IOptions<BotConfiguration> options,
        RiskManager risk,
        ILogger<DcaStrategy> logger
    )
    {
        _exchange = exchange;
        _config = options.Value.Trading;
        _risk = risk;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        _totalQuantity = 0;
        _averageEntryPrice = 0;
        _currentSafetyOrder = 0;
        _hasActivePosition = false;

        var ticker = await _exchange.GetTickerAsync(_config.Symbol, ct);
        _logger.LogInformation(
            "DCA Init: {Symbol} @ {Price:F2} | Drop%: {Drop} | TP%: {TP} | MaxOrders: {Max}",
            _config.Symbol,
            ticker.Price,
            _config.DcaBuyDropPercent,
            _config.DcaTakeProfitPercent,
            _config.DcaMaxOrders
        );

        // Place initial buy
        await PlaceBaseOrderAsync(ticker.Price, ct);
    }

    public async Task ExecuteTickAsync(Ticker ticker, CancellationToken ct = default)
    {
        if (!_hasActivePosition)
        {
            // No position — look for entry
            await PlaceBaseOrderAsync(ticker.Price, ct);
            return;
        }

        var takeProfitPrice = _averageEntryPrice * (1 + _config.DcaTakeProfitPercent / 100m);
        var stopLossPrice = _risk.GetStopLossPrice(_averageEntryPrice, OrderSide.Buy);

        _logger.LogDebug(
            "DCA tick: Price={Price:F2} | AvgEntry={Avg:F2} | TP={TP:F2} | SL={SL:F2} | Orders={N}/{Max}",
            ticker.Price,
            _averageEntryPrice,
            takeProfitPrice,
            stopLossPrice,
            _currentSafetyOrder,
            _config.DcaMaxOrders
        );

        // Take profit
        if (ticker.Price >= takeProfitPrice)
        {
            await CloseDealAsync(ticker.Price, "Take Profit", ct);
            return;
        }

        // Stop loss
        if (ticker.Price <= stopLossPrice)
        {
            await CloseDealAsync(ticker.Price, "Stop Loss", ct);
            return;
        }

        // Safety order trigger
        if (ticker.Price <= _nextBuyTriggerPrice && _currentSafetyOrder < _config.DcaMaxOrders)
        {
            await PlaceSafetyOrderAsync(ticker.Price, ct);
        }
    }

    private async Task PlaceBaseOrderAsync(decimal currentPrice, CancellationToken ct)
    {
        var quantity = Math.Round(_config.OrderSizeUsdt / currentPrice, 6);
        var orderValue = currentPrice * quantity;

        if (!_risk.CanTrade(orderValue, OrderSide.Buy))
            return;

        var order = await _exchange.PlaceMarketOrderAsync(
            _config.Symbol,
            OrderSide.Buy,
            quantity,
            ct
        );

        if (order.Status == OrderStatus.Filled || order.Status == OrderStatus.New)
        {
            AddEntry(currentPrice, quantity);
            _hasActivePosition = true;
            _currentSafetyOrder = 0;
            _nextBuyTriggerPrice = currentPrice * (1 - _config.DcaBuyDropPercent / 100m);

            var trade = new TradeResult
            {
                OrderId = order.ExchangeOrderId,
                Symbol = _config.Symbol,
                Side = OrderSide.Buy,
                Price = currentPrice,
                Quantity = quantity,
            };
            _trades.Add(trade);
            _risk.RecordTrade(trade);

            _logger.LogInformation(
                "📥 DCA Base Order: BUY {Qty:F6} @ {Price:F2} | NextSafety: {Next:F2}",
                quantity,
                currentPrice,
                _nextBuyTriggerPrice
            );
        }
    }

    private async Task PlaceSafetyOrderAsync(decimal currentPrice, CancellationToken ct)
    {
        _currentSafetyOrder++;

        // Each safety order is multiplied in size
        var multiplier = (decimal)Math.Pow((double)_config.DcaMultiplier, _currentSafetyOrder);
        var orderSizeUsdt = _config.OrderSizeUsdt * multiplier;
        var quantity = Math.Round(orderSizeUsdt / currentPrice, 6);

        if (!_risk.CanTrade(orderSizeUsdt, OrderSide.Buy))
        {
            _logger.LogWarning("Risk manager rejected safety order #{N}", _currentSafetyOrder);
            return;
        }

        var order = await _exchange.PlaceMarketOrderAsync(
            _config.Symbol,
            OrderSide.Buy,
            quantity,
            ct
        );

        AddEntry(currentPrice, quantity);

        // Next trigger drops further
        _nextBuyTriggerPrice = currentPrice * (1 - _config.DcaBuyDropPercent / 100m);

        var trade = new TradeResult
        {
            OrderId = order.ExchangeOrderId,
            Symbol = _config.Symbol,
            Side = OrderSide.Buy,
            Price = currentPrice,
            Quantity = quantity,
        };
        _trades.Add(trade);
        _risk.RecordTrade(trade);

        _logger.LogInformation(
            "📥 DCA Safety #{N}: BUY {Qty:F6} @ {Price:F2} (x{Mult:F1}) | AvgEntry: {Avg:F2} | NextSafety: {Next:F2}",
            _currentSafetyOrder,
            quantity,
            currentPrice,
            multiplier,
            _averageEntryPrice,
            _nextBuyTriggerPrice
        );
    }

    private async Task CloseDealAsync(decimal sellPrice, string reason, CancellationToken ct)
    {
        var order = await _exchange.PlaceMarketOrderAsync(
            _config.Symbol,
            OrderSide.Sell,
            _totalQuantity,
            ct
        );

        var totalCost = _entries.Sum(e => e.Price * e.Qty);
        var totalRevenue = sellPrice * _totalQuantity;
        var pnl = totalRevenue - totalCost;

        var trade = new TradeResult
        {
            OrderId = order.ExchangeOrderId,
            Symbol = _config.Symbol,
            Side = OrderSide.Sell,
            Price = sellPrice,
            Quantity = _totalQuantity,
            PnL = pnl,
        };
        _trades.Add(trade);
        _risk.RecordTrade(trade);

        _logger.LogInformation(
            "📤 DCA CLOSE ({Reason}): SELL {Qty:F6} @ {Price:F2} | PnL: {PnL:F2} USDT | SafetyOrders: {N}",
            reason,
            _totalQuantity,
            sellPrice,
            pnl,
            _currentSafetyOrder
        );

        // Reset state
        _entries.Clear();
        _totalQuantity = 0;
        _averageEntryPrice = 0;
        _currentSafetyOrder = 0;
        _hasActivePosition = false;
    }

    private void AddEntry(decimal price, decimal quantity)
    {
        _entries.Add((price, quantity));
        _totalQuantity = _entries.Sum(e => e.Qty);
        _averageEntryPrice = _entries.Sum(e => e.Price * e.Qty) / _totalQuantity;
    }

    public IReadOnlyList<TradeResult> GetTradeHistory() => _trades.AsReadOnly();
}
