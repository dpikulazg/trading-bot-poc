using CryptoTradingBot.Configuration;
using CryptoTradingBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoTradingBot.Services
{
    /// <summary>
    /// Enforces risk controls: max daily loss, position limits, cooldowns.
    /// Every order MUST pass through CanTrade() before execution.
    /// </summary>
    public sealed class RiskManager
    {
        private readonly RiskConfig _config;
        private readonly ILogger<RiskManager> _logger;
        private readonly object _lock = new();

        private DailyPnL _todayPnL = new();
        private decimal _currentPositionUsdt;
        private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

        public RiskManager(IOptions<BotConfiguration> options, ILogger<RiskManager> logger)
        {
            _config = options.Value.Risk;
            _logger = logger;
        }

        public DailyPnL TodayPnL => _todayPnL;

        public bool CanTrade(decimal orderValueUsdt, OrderSide side)
        {
            lock (_lock)
            {
                // Reset daily PnL if new day
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (_todayPnL.Date != today)
                {
                    _logger.LogInformation(
                        "New trading day - resetting daily PnL. Yesterday: {PnL:F2} USDT",
                        _todayPnL.RealizedPnL
                    );
                    _todayPnL = new DailyPnL { Date = today };
                }

                // Check cooldown
                if (DateTimeOffset.UtcNow < _cooldownUntil)
                {
                    _logger.LogWarning("In cooldown until {Until}. Skipping.", _cooldownUntil);
                    return false;
                }

                // Check daily loss limit
                if (_todayPnL.RealizedPnL <= -_config.MaxDailyLossUsdt)
                {
                    _logger.LogWarning(
                        "Daily loss limit reached: {Loss:F2} USDT. Trading paused.",
                        _todayPnL.RealizedPnL
                    );
                    _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(
                        _config.CooldownAfterLossMinutes
                    );
                    return false;
                }

                // Check position limit (only for buys)
                if (
                    side == OrderSide.Buy
                    && _currentPositionUsdt + orderValueUsdt > _config.MaxPositionUsdt
                )
                {
                    _logger.LogWarning(
                        "Position limit would be exceeded: {Current:F2} + {Order:F2} > {Max:F2}",
                        _currentPositionUsdt,
                        orderValueUsdt,
                        _config.MaxPositionUsdt
                    );
                    return false;
                }

                return true;
            }
        }

        public void RecordTrade(TradeResult trade)
        {
            lock (_lock)
            {
                _todayPnL.TradeCount++;
                _todayPnL.RealizedPnL += trade.PnL;
                _todayPnL.TotalVolume += trade.Price * trade.Quantity;

                if (trade.PnL > 0)
                    _todayPnL.WinCount++;
                else if (trade.PnL < 0)
                    _todayPnL.LossCount++;

                if (trade.Side == OrderSide.Buy)
                    _currentPositionUsdt += trade.Price * trade.Quantity;
                else
                    _currentPositionUsdt -= trade.Price * trade.Quantity;

                _currentPositionUsdt = Math.Max(0, _currentPositionUsdt);

                _logger.LogInformation(
                    "Trade recorded: {Side} {Qty} @ {Price} | PnL: {PnL:F2} | Daily: {Daily:F2} | Win/Loss: {W}/{L}",
                    trade.Side,
                    trade.Quantity,
                    trade.Price,
                    trade.PnL,
                    _todayPnL.RealizedPnL,
                    _todayPnL.WinCount,
                    _todayPnL.LossCount
                );
            }
        }

        public decimal GetStopLossPrice(decimal entryPrice, OrderSide side)
        {
            var stopDistance = entryPrice * (_config.StopLossPercent / 100m);
            return side == OrderSide.Buy ? entryPrice - stopDistance : entryPrice + stopDistance;
        }
    }
}
