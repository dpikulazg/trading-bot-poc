using CryptoTradingBot.Models;
using Microsoft.Extensions.Logging;

namespace CryptoTradingBot.Analytics
{
    /// <summary>
    /// Adaptive Trailing Stop Engine
    /// ═════════════════════════════
    ///
    /// Problem: Fixed 2.5% callback loses most profit during sudden spikes.
    ///          A +8% spike followed by -2.5% callback = you exit at +5.5%.
    ///          With adaptive tightening, spike exit = +7.2% or better.
    ///
    /// Solution: Three-layer defence that tightens dynamically:
    ///
    ///   Layer 1 — Multi-Phase Callback
    ///   ┌──────────────────────────────────────────────┐
    ///   │  Profit Zone        Callback %   Behaviour   │
    ///   │  0%  → +3%          2.50%        Normal      │
    ///   │  +3% → +6%          1.80%        Tighter     │
    ///   │  +6% → +10%         1.20%        Aggressive  │
    ///   │  +10%+              0.80%        Lockdown    │
    ///   └──────────────────────────────────────────────┘
    ///   Higher profit = tighter callback = protect more gains.
    ///
    ///   Layer 2 — Spike Detection (Rate-of-Change)
    ///   If price moves > SpikeThreshold% in SpikeWindow ticks:
    ///     → Temporarily halve the callback (capture the spike top)
    ///     → Lock a minimum profit floor at (current - spike buffer)
    ///     → Spike mode expires after SpikeDecayTicks if no new highs
    ///
    ///   Layer 3 — Profit Floor Lock
    ///   Once position reaches ProfitFloorActivation%:
    ///     → Guarantee exit no lower than FloorPercent% profit
    ///     → Floor is the HIGHER of: trailing stop vs profit floor
    ///     → Floor never decreases (same ratchet principle)
    /// </summary>
    public sealed class AdaptiveTrailingStopEngine
    {
        private readonly ILogger<AdaptiveTrailingStopEngine> _logger;

        public AdaptiveTrailingStopEngine(ILogger<AdaptiveTrailingStopEngine> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Evaluate trailing stop for a single position.
        /// Returns updated stop price and any state mutations on the position.
        /// </summary>
        public TrailingStopResult Evaluate(
            PortfolioPosition pos,
            decimal currentPrice,
            AdaptiveTrailingConfig config
        )
        {
            var result = new TrailingStopResult
            {
                PreviousStopPrice = pos.TrailingStopPrice,
                SpikeDetected = false,
                PhaseChanged = false,
            };

            var profitPct =
                pos.AvgEntryPrice > 0
                    ? (currentPrice - pos.AvgEntryPrice) / pos.AvgEntryPrice * 100m
                    : 0m;

            // ── 0. Track price history for spike detection ───────
            pos.RecentPrices.Add(currentPrice);
            if (pos.RecentPrices.Count > config.SpikeWindowTicks + 5)
                pos.RecentPrices.RemoveAt(0);

            // ── 1. Update High Water Mark ────────────────────────
            if (currentPrice > pos.HighWaterMark)
                pos.HighWaterMark = currentPrice;

            // ── 2. Spike Detection ───────────────────────────────
            var spikeActive = DetectSpike(pos, config, out var spikeRateOfChange);

            if (spikeActive && !pos.SpikeMode)
            {
                // Entering spike mode
                pos.SpikeMode = true;
                pos.SpikeEntryPrice = currentPrice;
                pos.SpikeTicksRemaining = config.SpikeDecayTicks;
                result.SpikeDetected = true;

                _logger.LogInformation(
                    "⚡ {Symbol} SPIKE DETECTED: RoC={RoC:+0.00}% in {Window} ticks → tightening callback",
                    pos.Symbol,
                    spikeRateOfChange,
                    config.SpikeWindowTicks
                );
            }
            else if (pos.SpikeMode)
            {
                // Decay spike mode if price stops making new highs
                if (currentPrice >= pos.HighWaterMark)
                {
                    pos.SpikeTicksRemaining = config.SpikeDecayTicks; // reset decay
                }
                else
                {
                    pos.SpikeTicksRemaining--;
                    if (pos.SpikeTicksRemaining <= 0)
                    {
                        pos.SpikeMode = false;
                        _logger.LogInformation(
                            "⚡ {Symbol} spike mode EXPIRED — returning to normal trailing",
                            pos.Symbol
                        );
                    }
                }
            }

            // ── 3. Determine active callback % (multi-phase) ────
            var baseCallback = GetPhaseCallback(profitPct, config);
            var previousPhase = pos.CurrentTrailingPhase;

            pos.CurrentTrailingPhase = GetPhaseName(profitPct, config);
            if (pos.CurrentTrailingPhase != previousPhase)
            {
                result.PhaseChanged = true;
                _logger.LogInformation(
                    "📊 {Symbol} trailing phase: {Old} → {New} (callback: {Cb}%)",
                    pos.Symbol,
                    previousPhase ?? "Initial",
                    pos.CurrentTrailingPhase,
                    baseCallback
                );
            }

            // Spike tightening: halve callback during spike
            var effectiveCallback = pos.SpikeMode
                ? baseCallback * config.SpikeCallbackMultiplier
                : baseCallback;

            result.EffectiveCallbackPercent = effectiveCallback;
            result.Phase = pos.CurrentTrailingPhase ?? "Normal";

            // ── 4. Calculate new trailing stop price ─────────────
            var trailingFromHwm = pos.HighWaterMark * (1m - effectiveCallback / 100m);

            // ── 5. Profit Floor Lock ─────────────────────────────
            var profitFloor = decimal.MinValue;
            if (profitPct >= config.ProfitFloorActivationPercent)
            {
                var floorPct = Math.Max(
                    config.ProfitFloorPercent,
                    profitPct - config.ProfitFloorBufferPercent
                );

                profitFloor = pos.AvgEntryPrice * (1m + floorPct / 100m);

                // Floor from spike: lock at spike entry minus small buffer
                if (pos.SpikeMode && pos.SpikeEntryPrice > 0)
                {
                    var spikeFloor =
                        pos.SpikeEntryPrice * (1m - config.SpikeFloorBufferPercent / 100m);
                    profitFloor = Math.Max(profitFloor, spikeFloor);
                }

                // Update position's profit floor (never decreases)
                if (profitFloor > pos.ProfitFloorPrice)
                {
                    var oldFloor = pos.ProfitFloorPrice;
                    pos.ProfitFloorPrice = profitFloor;

                    if (oldFloor > 0)
                    {
                        _logger.LogInformation(
                            "🔒 {Symbol} profit floor raised: {Old:F2} → {New:F2} (guarantees +{Pct:F2}%)",
                            pos.Symbol,
                            oldFloor,
                            profitFloor,
                            (profitFloor - pos.AvgEntryPrice) / pos.AvgEntryPrice * 100m
                        );
                    }
                }
            }

            // ── 6. Final stop = MAX(trailing, profit floor, current stop) ──
            var newStopPrice = trailingFromHwm;

            if (pos.ProfitFloorPrice > 0)
                newStopPrice = Math.Max(newStopPrice, pos.ProfitFloorPrice);

            // Ratchet: never lower the stop
            newStopPrice = Math.Max(newStopPrice, pos.TrailingStopPrice);

            result.NewStopPrice = newStopPrice;
            result.StopMoved = newStopPrice > pos.TrailingStopPrice;

            if (result.StopMoved)
            {
                _logger.LogInformation(
                    "🔄 {Symbol} stop: {Old:F2} → {New:F2} | phase={Phase} cb={Cb:F2}% {Spike}| HWM: {Hwm:F2} (+{HwmPct:F2}%)",
                    pos.Symbol,
                    pos.TrailingStopPrice,
                    newStopPrice,
                    pos.CurrentTrailingPhase,
                    effectiveCallback,
                    pos.SpikeMode ? "⚡SPIKE " : "",
                    pos.HighWaterMark,
                    pos.HighWaterMarkPercent
                );

                pos.TrailingStopPrice = newStopPrice;
            }

            // ── 7. Check if stop is hit ──────────────────────────
            result.StopHit = currentPrice <= pos.TrailingStopPrice;

            return result;
        }

        // ── Spike Detection ──────────────────────────────────────

        private bool DetectSpike(
            PortfolioPosition pos,
            AdaptiveTrailingConfig config,
            out decimal rateOfChange
        )
        {
            rateOfChange = 0m;

            if (pos.RecentPrices.Count < config.SpikeWindowTicks + 1)
                return false;

            var windowStart = pos.RecentPrices[^(config.SpikeWindowTicks + 1)];
            var windowEnd = pos.RecentPrices[^1];

            if (windowStart <= 0)
                return false;

            rateOfChange = (windowEnd - windowStart) / windowStart * 100m;

            // Spike = positive RoC exceeding threshold
            return rateOfChange >= config.SpikeThresholdPercent;
        }

        // ── Multi-Phase Callback Resolution ──────────────────────

        private static decimal GetPhaseCallback(decimal profitPct, AdaptiveTrailingConfig config)
        {
            // Walk phases in reverse (highest profit first)
            for (var i = config.Phases.Count - 1; i >= 0; i--)
            {
                if (profitPct >= config.Phases[i].MinProfitPercent)
                    return config.Phases[i].CallbackPercent;
            }

            return config.Phases.Count > 0 ? config.Phases[0].CallbackPercent : 2.5m; // fallback
        }

        private static string GetPhaseName(decimal profitPct, AdaptiveTrailingConfig config)
        {
            for (var i = config.Phases.Count - 1; i >= 0; i--)
            {
                if (profitPct >= config.Phases[i].MinProfitPercent)
                    return config.Phases[i].Name;
            }
            return "Inactive";
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Configuration
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Full configuration for adaptive trailing stop behaviour.
    /// </summary>
    public sealed class AdaptiveTrailingConfig
    {
        /// <summary>
        /// Multi-phase callback tiers. Must be ordered by MinProfitPercent ascending.
        /// </summary>
        public List<TrailingPhase> Phases { get; init; } =
        [
            new("Normal", MinProfitPercent: 0m, CallbackPercent: 2.50m),
            new("Tighter", MinProfitPercent: 3m, CallbackPercent: 1.80m),
            new("Aggressive", MinProfitPercent: 6m, CallbackPercent: 1.20m),
            new("Lockdown", MinProfitPercent: 10m, CallbackPercent: 0.80m),
        ];

        // ── Spike Detection ──
        /// <summary>Rate-of-change threshold (%) over SpikeWindowTicks to trigger spike mode.</summary>
        public decimal SpikeThresholdPercent { get; init; } = 2.5m;

        /// <summary>Number of ticks to measure rate-of-change over.</summary>
        public int SpikeWindowTicks { get; init; } = 5;

        /// <summary>Multiply callback by this during spike (0.5 = halve the callback).</summary>
        public decimal SpikeCallbackMultiplier { get; init; } = 0.5m;

        /// <summary>Ticks before spike mode expires if no new HWM.</summary>
        public int SpikeDecayTicks { get; init; } = 10;

        /// <summary>Buffer below spike entry price for spike profit floor.</summary>
        public decimal SpikeFloorBufferPercent { get; init; } = 0.5m;

        // ── Profit Floor ──
        /// <summary>Profit % at which profit floor activates.</summary>
        public decimal ProfitFloorActivationPercent { get; init; } = 4m;

        /// <summary>Minimum guaranteed profit % once floor is active.</summary>
        public decimal ProfitFloorPercent { get; init; } = 2m;

        /// <summary>Buffer below current profit for floor calculation.</summary>
        public decimal ProfitFloorBufferPercent { get; init; } = 1.5m;

        public override string ToString()
        {
            var phases = string.Join(
                " → ",
                Phases.Select(p => $"{p.Name}(≥{p.MinProfitPercent}%→{p.CallbackPercent}%)")
            );
            return $"Phases: {phases} | Spike: ≥{SpikeThresholdPercent}% in {SpikeWindowTicks} ticks "
                + $"(×{SpikeCallbackMultiplier}) | Floor: ≥{ProfitFloorActivationPercent}% → lock {ProfitFloorPercent}%+";
        }
    }

    public record TrailingPhase(string Name, decimal MinProfitPercent, decimal CallbackPercent);

    // ──────────────────────────────────────────────────────────────
    //  Result DTO
    // ──────────────────────────────────────────────────────────────

    public sealed class TrailingStopResult
    {
        public decimal PreviousStopPrice { get; init; }
        public decimal NewStopPrice { get; set; }
        public bool StopMoved { get; set; }
        public bool StopHit { get; set; }
        public bool SpikeDetected { get; set; }
        public bool PhaseChanged { get; set; }
        public decimal EffectiveCallbackPercent { get; set; }
        public string Phase { get; set; } = "Normal";
    }
}
