using CryptoTradingBot.Models;

namespace CryptoTradingBot.Configuration
{
    // ──────────────────────────────────────────────────
    //  Configuration
    // ──────────────────────────────────────────────────

    public sealed class TradingConfig
    {
        public string Symbol { get; set; } = "BTCUSDT";

        public string[] Symbols => WatchlistSymbols ?? new[] { Symbol };

        public int PollingIntervalMs { get; init; } = 1000;

        public decimal OrderSizeUsdt { get; init; } = 10m;

        // -- Grid Strategy params --
        public decimal GridLowerPrice { get; init; } = 20000m;
        public decimal GridUpperPrice { get; init; } = 40000m;
        public int GridLevels { get; init; } = 10;

        // -- DCA Strategy params --
        public decimal DcaBuyDropPercent { get; init; } = 2.0m;
        public decimal DcaTakeProfitPercent { get; init; } = 2.0m;
        public int DcaMaxOrders { get; init; } = 5;
        public decimal DcaMultiplier { get; init; } = 2.0m;

        // ── Position sizing ──
        public decimal MaxPositionSizeUsdt { get; init; } = 500m;
        public decimal MaxPortfolioRiskPercent { get; init; } = 1.5m;
        public int MaxOpenPositions { get; init; } = 5;

        // ── Default risk params ──
        public decimal DefaultStopLossPercent { get; init; } = 3m;
        public decimal DefaultTrailingCallbackPercent { get; init; } = 2.5m;
        public decimal TrailingActivationPercent { get; init; } = 1.5m;

        // ── Take-profit tiers (3-tier scale-out) ──
        public TakeProfitTier[] DefaultTakeProfitTiers { get; init; } =
        [
            new(TargetPercent: 3m, PortionPercent: 30m),
            new(TargetPercent: 6m, PortionPercent: 40m),
            new(TargetPercent: 10m, PortionPercent: 30m),
        ];

        // ── Time activation window ──
        public TimeOnly DefaultActivateAt { get; init; } = new(8, 0);
        public TimeOnly DefaultDeactivateAt { get; init; } = new(22, 0);
        public DayOfWeek[] DefaultActiveDays { get; init; } =
        [
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday,
        ];

        // ── Strategy indicator periods ──
        public int ShortSmaPeriod { get; init; } = 9;
        public int LongSmaPeriod { get; init; } = 21;
        public int RsiPeriod { get; init; } = 14;
        public int MacdFast { get; init; } = 12;
        public int MacdSlow { get; init; } = 26;
        public int MacdSignalPeriod { get; init; } = 9;
        public int BollingerPeriod { get; init; } = 20;
        public decimal BollingerStdDev { get; init; } = 2.0m;
        public int AtrPeriod { get; init; } = 14;

        // ── Confidence thresholds ──
        public decimal MinBuyConfidence { get; init; } = 0.45m;
        public decimal MinSellConfidence { get; init; } = 0.40m;

        public string[] WatchlistSymbols { get; init; } =
        ["BTCUSDT", "ETHUSDT", "SOLUSDT", "ADAUSDT", "DOTUSDT"];

        public void Validate()
        {
            var errors = new List<string>();

            // Position sizing & risk limits
            if (MaxPositionSizeUsdt <= 0)
                errors.Add("MaxPositionSizeUsdt must be > 0.");
            if (MaxPortfolioRiskPercent <= 0 || MaxPortfolioRiskPercent > 100)
                errors.Add("MaxPortfolioRiskPercent must be in (0, 100].");
            if (MaxOpenPositions <= 0)
                errors.Add("MaxOpenPositions must be > 0.");

            // Stops & trailing
            if (DefaultStopLossPercent <= 0 || DefaultStopLossPercent > 100)
                errors.Add("DefaultStopLossPercent must be in (0, 100].");
            if (DefaultTrailingCallbackPercent <= 0 || DefaultTrailingCallbackPercent > 100)
                errors.Add("DefaultTrailingCallbackPercent must be in (0, 100].");
            if (TrailingActivationPercent < 0 || TrailingActivationPercent > 100)
                errors.Add("TrailingActivationPercent must be in [0, 100].");
            if (TrailingActivationPercent > DefaultTrailingCallbackPercent)
                errors.Add("TrailingActivationPercent must be <= DefaultTrailingCallbackPercent.");

            // Take-profit tiers
            if (DefaultTakeProfitTiers is null || DefaultTakeProfitTiers.Length == 0)
            {
                errors.Add("DefaultTakeProfitTiers must contain at least one tier.");
            }
            else
            {
                var totalPortion = 0m;
                var lastTarget = 0m;

                foreach (var tier in DefaultTakeProfitTiers)
                {
                    if (tier.TargetPercent <= 0)
                        errors.Add("Take-profit tier TargetPercent must be > 0.");
                    if (tier.PortionPercent <= 0)
                        errors.Add("Take-profit tier PortionPercent must be > 0.");
                    if (tier.TargetPercent <= lastTarget)
                        errors.Add(
                            "Take-profit tier TargetPercent values must be strictly increasing."
                        );

                    totalPortion += tier.PortionPercent;
                    lastTarget = tier.TargetPercent;
                }

                if (Math.Abs(totalPortion - 100m) > 0.01m)
                    errors.Add("Take-profit tier PortionPercent values must sum to 100.");
            }

            // Time activation window
            if (DefaultActivateAt == DefaultDeactivateAt)
                errors.Add("DefaultActivateAt must be different from DefaultDeactivateAt.");
            if (DefaultActiveDays is null || DefaultActiveDays.Length == 0)
                errors.Add("DefaultActiveDays must contain at least one day.");

            // Indicator periods
            if (ShortSmaPeriod <= 1)
                errors.Add("ShortSmaPeriod must be > 1.");
            if (LongSmaPeriod <= 1)
                errors.Add("LongSmaPeriod must be > 1.");
            if (ShortSmaPeriod >= LongSmaPeriod)
                errors.Add("ShortSmaPeriod must be < LongSmaPeriod.");

            if (RsiPeriod <= 1)
                errors.Add("RsiPeriod must be > 1.");

            if (errors.Any())
                throw new InvalidOperationException($"TradingConfig validation failed: {string.Join(", ", errors)}");
        }
    }
}
