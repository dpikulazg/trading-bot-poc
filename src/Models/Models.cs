namespace CryptoTradingBot.Models
{
    // ──────────────────────────────────────────────────
    //  Market Data
    // ──────────────────────────────────────────────────

    public record CoinPrice(string Symbol, decimal Price, decimal Volume24h, DateTime Timestamp);

    public record Candle(
        string Symbol,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        DateTime OpenTime,
        DateTime CloseTime
    );

    // ──────────────────────────────────────────────────
    //  Orders & Execution
    // ──────────────────────────────────────────────────

    public record TradeOrder
    {
        public required string Id { get; init; }
        public required string Symbol { get; init; }
        public required OrderSide Side { get; init; }
        public required OrderType Type { get; init; }
        public required decimal Quantity { get; init; }
        public required decimal Price { get; init; }
        public required OrderStatus Status { get; set; }
        public required DateTime CreatedAt { get; init; }

        /// <summary>UTC time window when this order becomes eligible for execution.</summary>
        public TimeActivation? Activation { get; init; }

        /// <summary>Trailing stop configuration attached to the order.</summary>
        public TrailingStopConfig? TrailingStop { get; init; }

        /// <summary>Scale-out take-profit tiers.</summary>
        public IReadOnlyList<TakeProfitTier>? TakeProfitTiers { get; init; }
    }

    /// <summary>
    /// Defines a UTC time window during which the order is active.
    /// Outside this window the order is parked (not sent to exchange).
    /// </summary>
    public record TimeActivation(
        TimeOnly ActivateAt,
        TimeOnly DeactivateAt,
        DayOfWeek[]? ActiveDays = null
    )
    {
        public bool IsActiveNow()
        {
            var now = DateTime.UtcNow;
            var currentTime = TimeOnly.FromDateTime(now);

            if (ActiveDays is { Length: > 0 } && !ActiveDays.Contains(now.DayOfWeek))
                return false;

            // Handle overnight windows (e.g. 22:00 → 06:00)
            return ActivateAt <= DeactivateAt
                ? currentTime >= ActivateAt && currentTime <= DeactivateAt
                : currentTime >= ActivateAt || currentTime <= DeactivateAt;
        }

        public override string ToString()
        {
            var days = ActiveDays is { Length: > 0 }
                ? string.Join(",", ActiveDays.Select(d => d.ToString()[..3]))
                : "Every day";
            return $"{ActivateAt:HH:mm}–{DeactivateAt:HH:mm} UTC ({days})";
        }
    }

    /// <summary>
    /// Trailing stop that follows price by a fixed callback percentage.
    /// Once price moves in your favour the stop ratchets up and never goes back down.
    /// </summary>
    public record TrailingStopConfig(decimal CallbackPercent, decimal? ActivationPercent = null)
    {
        /// <summary>Minimum profit % before the trailing stop kicks in.</summary>
        public decimal ActivationThreshold => ActivationPercent ?? 0m;
    }

    /// <summary>
    /// One tier of a multi-level take-profit plan.
    /// e.g. sell 30% of position at +3%, another 40% at +6%, rest at +10%.
    /// </summary>
    public record TakeProfitTier(
        decimal TargetPercent,
        decimal PortionPercent,
        bool Triggered = false
    );

    // ──────────────────────────────────────────────────
    //  Portfolio & Positions
    // ──────────────────────────────────────────────────

    public record PortfolioPosition
    {
        public required string Symbol { get; init; }
        public required decimal OriginalQuantity { get; init; }
        public required decimal RemainingQuantity { get; set; }
        public required decimal AvgEntryPrice { get; init; }
        public decimal CurrentPrice { get; set; }
        public decimal HighWaterMark { get; set; }
        public decimal TrailingStopPrice { get; set; }
        public TrailingStopConfig TrailingStopConfig { get; init; } = new(2.5m, 1.5m);
        public List<TakeProfitTier> TakeProfitTiers { get; init; } = [];
        public DateTime OpenedAt { get; init; } = DateTime.UtcNow;
        public decimal RealizedPnL { get; set; }

        // ── Adaptive Trailing Stop State ──
        /// <summary>Sliding window of recent prices for spike detection.</summary>
        public List<decimal> RecentPrices { get; init; } = [];

        /// <summary>True when a price spike has been detected and trailing is tightened.</summary>
        public bool SpikeMode { get; set; }

        /// <summary>Price at which spike mode was activated.</summary>
        public decimal SpikeEntryPrice { get; set; }

        /// <summary>Remaining ticks before spike mode expires (if no new HWM).</summary>
        public int SpikeTicksRemaining { get; set; }

        /// <summary>Guaranteed minimum exit price - never decreases once set.</summary>
        public decimal ProfitFloorPrice { get; set; }

        /// <summary>Current trailing phase name for dashboard display.</summary>
        public string? CurrentTrailingPhase { get; set; }

        // ── Computed Properties ──
        public decimal UnrealizedPnL => (CurrentPrice - AvgEntryPrice) * RemainingQuantity;
        public decimal TotalPnL => RealizedPnL + UnrealizedPnL;
        public decimal UnrealizedPnLPercent =>
            AvgEntryPrice > 0 ? (CurrentPrice - AvgEntryPrice) / AvgEntryPrice * 100m : 0m;
        public decimal HighWaterMarkPercent =>
            AvgEntryPrice > 0 ? (HighWaterMark - AvgEntryPrice) / AvgEntryPrice * 100m : 0m;
        public decimal ProfitFloorPercent =>
            AvgEntryPrice > 0 && ProfitFloorPrice > 0
                ? (ProfitFloorPrice - AvgEntryPrice) / AvgEntryPrice * 100m
                : 0m;
        public bool IsFullyClosed => RemainingQuantity <= 0;
    }

    // ──────────────────────────────────────────────────
    //  Strategy Signals
    // ──────────────────────────────────────────────────

    public record TradingSignal(
        string Symbol,
        SignalType Type,
        decimal TargetPrice,
        decimal StopLoss,
        decimal Confidence,
        string Reason,
        IndicatorSnapshot? Indicators = null,
        TrailingStopConfig? TrailingStop = null,
        IReadOnlyList<TakeProfitTier>? TakeProfitTiers = null,
        TimeActivation? Activation = null
    );

    // ──────────────────────────────────────────────────
    //  Indicator Snapshots
    // ──────────────────────────────────────────────────

    public record IndicatorSnapshot(
        decimal ShortSma,
        decimal LongSma,
        decimal Rsi,
        decimal MacdLine,
        decimal MacdSignal,
        decimal MacdHistogram,
        decimal BollingerUpper,
        decimal BollingerMiddle,
        decimal BollingerLower,
        decimal Atr
    );

    public enum SignalType
    {
        StrongBuy,
        Buy,
        Sell,
        StrongSell,
        Hold,
    }
}
