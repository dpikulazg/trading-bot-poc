using CryptoTradingBot.Configuration;
using CryptoTradingBot.Exchange;
using CryptoTradingBot.Strategies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoTradingBot.Services
{
    /// <summary>
    /// Long-running hosted service that drives the trading loop.
    /// Polls the exchange at configured intervals, feeds ticks to the strategy.
    /// </summary>
    public sealed class TradingBotService : BackgroundService
    {
        private readonly IExchangeClient _exchange;
        private readonly ITradingStrategy _strategy;
        private readonly RiskManager _risk;
        private readonly BotConfiguration _config;
        private readonly ILogger<TradingBotService> _logger;

        public TradingBotService(
            IExchangeClient exchange,
            ITradingStrategy strategy,
            RiskManager risk,
            IOptions<BotConfiguration> options,
            ILogger<TradingBotService> logger
        )
        {
            _exchange = exchange;
            _strategy = strategy;
            _risk = risk;
            _config = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                """

                ╔══════════════════════════════════════════════╗
                ║       Crypto Trading Bot v1.0 Started        ║
                ╠══════════════════════════════════════════════╣
                ║  Strategy : {Strategy,-32}                   ║
                ║  Symbol   : {Symbol,-32}                     ║
                ║  Exchange : {Exchange,-32}                   ║
                ║  Paper    : {Paper,-32}                      ║
                ╚══════════════════════════════════════════════╝
                """,
                _strategy.Name,
                _config.Trading.WatchlistSymbols,
                _config.Exchange.Name,
                _config.Risk.PaperTrading ? "YES (safe mode)" : "NO (LIVE!)"
            );

            try
            {
                await _strategy.InitializeAsync(stoppingToken);

                using var timer = new PeriodicTimer(
                    TimeSpan.FromMilliseconds(_config.Trading.PollingIntervalMs)
                );

                int tickCount = 0;

                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        tickCount++;
                        var ticker = await _exchange.GetTickerAsync(
                            _config.Trading.Symbol,
                            stoppingToken
                        );

                        if (ticker == null)
                        {
                            _logger.LogWarning("Received null ticker from exchange API. Skipping tick.");
                            continue;
                        }

                        // Simulate paper fills if applicable
                        if (_exchange is PaperTradingClient paper)
                            paper.SimulateFills(ticker.Price);

                        await _strategy.ExecuteTickAsync(ticker, stoppingToken);

                        // Periodic status report every 60 ticks
                        if (tickCount % 60 == 0)
                            LogStatus(ticker);
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogWarning(ex, "Exchange API error — retrying next tick");
                        await Task.Delay(2000, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Unexpected error in trading loop");
                        await Task.Delay(5000, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Bot shutdown requested");
            }
            finally
            {
                LogFinalReport();
            }
        }

        private void LogStatus(Models.Ticker ticker)
        {
            var pnl = _risk.TodayPnL;
            _logger.LogInformation(
                "📊 Status | {Symbol} @ {Price:F2} | Daily PnL: {PnL:F2} | Trades: {T} | W/L: {W}/{L}",
                ticker.Symbol,
                ticker.Price,
                pnl.RealizedPnL,
                pnl.TradeCount,
                pnl.WinCount,
                pnl.LossCount
            );
        }

        private void LogFinalReport()
        {
            var trades = _strategy.GetTradeHistory();
            var pnl = _risk.TodayPnL;
            var winRate = pnl.TradeCount > 0 ? (decimal)pnl.WinCount / pnl.TradeCount * 100 : 0;

            _logger.LogInformation(
                """

                ╔══════════════════════════════════════════════╗
                ║            SESSION REPORT                    ║
                ╠══════════════════════════════════════════════╣
                ║  Total Trades : {Trades,-28}                 ║
                ║  Win Rate     : {WinRate,-28:F1}%            ║
                ║  Realized PnL : {PnL,-28:F2}                 ║
                ║  Volume       : {Vol,-28:F2}                 ║
                ╚══════════════════════════════════════════════╝
                """,
                pnl.TradeCount,
                winRate,
                pnl.RealizedPnL,
                pnl.TotalVolume
            );
        }
    }
}
