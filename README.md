# CryptoTradingBot

Daily trading bot with **Grid** and **DCA** strategies, a walk-forward validation harness,
and a deployment gate that decides whether a strategy has earned real capital.

## Solution layout

| Project | Purpose |
|---|---|
| `CryptoTradingBot.csproj` | The bot: exchange clients, strategies, risk manager, backtesting |
| `tests/CryptoTradingBot.Tests` | xunit suite — 68 tests, no network required |

```bash
dotnet build CryptoTradingBot.sln
```

```bash
dotnet test CryptoTradingBot.sln
```

## Running

The mode is the first argument.

```bash
dotnet run
```
Starts the trading loop. Paper trading is on by default (`TradingBot:Risk:PaperTrading`).

```bash
dotnet run -- backtest ETHUSDT 180
```
Single backtest over the last 180 days, followed by Monte Carlo significance tests.

```bash
dotnet run -- walkforward ETHUSDT 365
```
Walk-forward optimization across folds, then the deployment gate. Exit code `0` means every
criterion passed, `2` means it did not.

## Credentials

API keys are read from the environment, never from a committed file:

```bash
export TRADINGBOT_TradingBot__Exchange__ApiKey=…
```

`appsettings.json` ships with empty credentials and `UseSandbox: true`.

## The deployment gate

`walkforward` refuses to pass unless **all ten** of these hold. Exit code `0` means every
criterion passed, `2` means it did not.

**Performance — did the edge survive the walk to unseen data?**

| Criterion | Threshold | What it catches |
|---|---|---|
| OOS/IS Sharpe ratio | ≥ 0.50 | The optimizer fitting noise instead of signal |
| Folds profitable out of sample | ≥ 60% | An edge that only existed in one market regime |
| Mean OOS Sharpe | > 0 | — |

**Significance — is it distinguishable from luck?**

| Criterion | Threshold | What it catches |
|---|---|---|
| Random-entry p-value | < 0.05 | A long-biased rule riding a bull market |
| Deflated Sharpe, worst fold | ≥ 0.95 | The winner being the luckiest of N parameter sets |

Searching N configurations and keeping the best is a multiple-comparisons problem: with enough
trials some configuration will look excellent on pure noise. The deflated Sharpe is the
probability the winner beats what the *same search* would produce on noise, so it is gated on
the **worst** fold — one fold selecting noise means the optimizer failed there.

The random-entry null is scaled to the capital the strategy actually risks per trade. Comparing
a grid that deploys 0.5% of equity against a null that swings the full balance gives the null
~200× the leverage and makes the test unable to reject anything.

**Sample adequacy — is there enough data for any of the above to mean anything?**

| Criterion | Threshold | What it catches |
|---|---|---|
| Total OOS round trips | ≥ 100 | A p-value computed from a handful of trades |
| Round trips per fold | ≥ 10 | Folds that closed nothing at all |
| Fold length | ≥ 500 IS / 200 OOS bars | Annualised Sharpe inflated by a short window |

Annualising a tiny per-bar mean over a near-flat equity curve multiplies by √(bars per year).
On hourly bars that is ×93.6, which manufactures double-digit Sharpe ratios out of noise. These
three checks exist because a 60-day sample produced a "Sharpe of 14" that meant nothing.

**Search quality — did the optimizer find a real region?**

| Criterion | Threshold | What it catches |
|---|---|---|
| Parameter stability (CV) | ≤ 0.35 | An "optimum" that is a different spike on every fold |
| Boundary-pinned axes | ≤ 50% of folds | The sweep reporting its own edge as the optimum |

When the winner sits on the edge of the search space, the real optimum is probably outside it —
the number reported is a limit of the sweep, not a property of the strategy. Widen the range
until the winner is interior.

Each criterion is necessary and none is sufficient.

## What the grid search actually sweeps

Only **rung spacing** (`StepPercent`). The band is pinned to 1.5× the price range observed in
each fold's in-sample window, and level count falls out of `band ÷ spacing`.

Two earlier parameterisations failed for instructive reasons:

- `GridLevels` + `RangeWidthPercent` — both are proxies for the distance between rungs, so the
  optimizer drove one to its floor and the other to its ceiling and reported the corner of the
  box.
- `StepPercent` + `RangeWidthPercent` — spacing found a stable interior optimum, but band width
  scattered across 50–160% with no preference. Once spacing is right, rungs beyond the price's
  actual travel never fill, so every wide band scores identically. A parameter the data cannot
  distinguish should not be searched.

The band is derived **per fold from in-sample bars only**. `WalkForwardRequest` takes a
`SpaceFactory` rather than a prebuilt space for exactly this reason: sizing geometry once from
the whole series would put the evaluation period's own highs and lows into the parameters
under test.

## Execution assumptions in the backtester

Stated explicitly, because these are what separate a believable backtest from a flattering one:

- Orders placed while processing bar *i* can only fill from bar *i+1* onward — no lookahead.
- `GetKlinesAsync` never returns bars past the current one.
- Resting limit orders fill at their own limit price when the bar's range crosses them.
- Market orders fill at the current close, moved adversely by `SlippagePercent`.
- Maker fees on limit fills, taker fees on market fills. Reported P&L is **net of fees**.
- Orders the simulated account cannot fund are rejected, and resting orders reserve their funds.

## Configuration

Everything lives under `TradingBot` in `appsettings.json`, validated at startup —
`BotConfiguration.Validate()` fails fast rather than letting a bad value surface mid-session.
Notably it rejects a DCA ladder whose full depth would exceed `MaxPositionSizeUsdt`.
