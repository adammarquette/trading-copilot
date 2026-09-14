# ADR-0009: Backtesting & historical simulation

**Status:** Accepted · **Date:** 2026-07-19 · **Deciders:** Adam (operator)
**Relates to:** PRD §6 *Future Considerations* (Backtesting), `R-1` (clean historical), `R-4` (suggestions), `R-7`
(rulebook rule-replay), `R-9` (feedback loop / simulation), `R-14` (practice); engineering §5 (deterministic evals),
§7 (AI spend); [ADR-0001](0001-event-backbone.md) (event log / replay), [ADR-0008](0008-ai-invocation-cost-model.md)
(AI cost model). Insight: the wiki's [QuantConnect page](../wiki/pages/quantconnect.md) — **backtest ↔ live parity**.

## Context
The operator wants **backtesting so the AI can test-run and refine** its suggestions, strategies, rulebook, and
confidence calibration against **up to ~2 years** of history. The pieces already exist to build on: the clean
historical store (R-1, ADR-0001), forward-simulation of untaken suggestions (R-9), rule-replay against history (R-7),
and the deterministic trigger layer + "LLM at the edges" cost model (ADR-0008). This ADR records how they compose
into a backtest, and the two calls the operator made — **cheaper-model** runs and **news-light** long backtests.

## Decision
- **Engine parity — one engine, two clocks.** A backtest replays recorded data through the **same**
  indicators → triggers → suggestion → risk-gate pipeline as live; the *only* difference is the clock / data source
  (historical replay vs. live feed). **No divergent backtest logic** that could lie about live behavior.
- **Reproducible, look-ahead-safe replay** over the clean-historical store (ADR-0001) — deterministic given fixed
  data + logic; each step sees **only data available at that historical point**. Reproducibility also requires the
  **adapter-derivation logic to be versioned**: `AccountSnapshot` carries the **`adapter_logic_version`** that
  produced its derived values (buying power / stage / status), so a replay interprets a historical snapshot with the
  logic that made it — a later fix to the derivation can't retroactively corrupt past results (data dictionary, Q-4).
- **Cheaper-model backtests** *(decided)* — the suggestion / refinement LLM step runs on a **cheaper model**
  (Haiku / Sonnet via the `ILlmProvider` seam), reserving the top model for live; **sampling** further bounds cost. A
  backtest is for **refinement signal**, not exact live reproduction (ADR-0008).
- **News-light long backtests** *(decided)* — ~2-yr **price / indicator** history is the substrate (ProjectX bars,
  venue-depth permitting); **news / soft-signal history is only ~recent 3 months** (Tiingo free), so long backtests
  are largely price + indicator, with full news context only for the recent window. Accepted; deeper news history is
  a later paid option.
- **Fill / outcome model** — simulate fills against recorded bars with **documented, consistent assumptions** (R-9),
  including the **no-fill / scratch** outcome (R-4/R-9 — entries never reached). Bar-level by default; tick where a
  setup needs it.
- **What it refines** — tuned **trigger thresholds / strategy parameters**, **confidence calibration** (predicted vs.
  realized), and **rulebook A/B** (extends R-7 rule-replay); refinement outputs are **proposed and confirmed**, never
  auto-applied. Results feed the same **strategy-performance breakdown** (R-9) over simulated historical suggestions.
- **Cost is tracked** — each run is attributed in **AI-spend tracking** (engineering §7 / `AIUsage`) and **reports
  its own $ cost**, bounded by the AI-spend governor (ADR-0008).

## Alternatives considered
- **A separate backtest engine.** Faster to special-case, but it diverges from live → misleading results. Rejected
  for **parity**.
- **Full top-model re-invocation in the backtest.** Most faithful, but cost-prohibitive over 2 years. Rejected for
  **cheaper-model + sampling**.
- **Wait for deep (paid) news history before backtesting.** Rejected — price/indicator backtests are useful now;
  **news-light is acceptable**.
- **Tick-level replay for everything.** Overkill for most refinement; **bar-level default**, tick where warranted.

## Consequences
**Positive**
- **Parity → trustworthy results** reusable for refinement; **reproducible / look-ahead-safe**.
- **Cost-bounded** (cheaper model + sampling + tracked spend).
- **Reuses** the historical store, R-9 simulation, the performance analytics, and the ADR-0008 cost model already
  designed — little net-new infrastructure.

**Negative / costs**
- **News-light long backtests** — a known blind spot on the news dimension for anything older than ~3 months.
- **Cheaper-model results aren't an exact live proxy** — treat as directional; calibrate against live.
- **Look-ahead safety must be enforced carefully** — a subtle, high-consequence bug source (needs deterministic-eval
  coverage, §5).
- The **fill-model assumptions bias results** — document and hold them constant.

## Follow-ups
- Define the **replay harness**: clock, historical data source, and the **look-ahead guards**.
- Define the **backtest config**: range, instruments, strategies / rules under test, model tier, sampling.
- Define the **fill / slippage** assumptions.
- Define how **refinement outputs** (threshold / parameter / calibration changes) are proposed + confirmed (R-7).
- The **backtest results surface** (reuse the performance analytics) + per-run **cost** display.
- **Deterministic-eval** coverage for look-ahead safety and parity (engineering §5).
