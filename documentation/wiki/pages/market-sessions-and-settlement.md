# Market sessions, the close & settlement (reference)

> **Trust tier:** operator-provided domain knowledge (Adam), 2026-07-19 — **exact times per product / venue are to
> be confirmed** against the CME rulebook + venue help centers. **Informs:** R-13 (auto-flatten), R-5 (risk),
> ADR-0007 (execution), ADR-0013 (failure & recovery). See also [prop-firm-rules.md](prop-firm-rules.md),
> [topstep-brokerage.md](topstep-brokerage.md).

Why the co-pilot cares about the **shape of the trading day** — several distinct times drive the **auto-flatten**
(R-13) and the end-of-day **resiliency** model (ADR-0013). Times are **US Central (CT)** — the CME's timezone.

## The times that matter
| Time (CT) | Event | Why it matters |
|---|---|---|
| **~2:30 pm** | **Operator auto-flatten (default)** | Our R-13 flatten — **~30 min before the equity EOD** — to be **out before MOC** (below). **Configurable per account.** |
| **3:00 pm** | **US cash-equity EOD** (4:00 pm ET) | **Market-on-close (MOC)** auction: closing-order imbalances can spike volatility into the print. The reason to be flat by ~2:30. |
| **~3:10 pm** | **Prop-firm forced flatten** (e.g. **Topstep**) | The **venue** closes prop positions itself — a backstop **after** our 2:30, and **only on prop accounts** (a live brokerage has none). |
| **4:00 pm** | **CME equity-index futures close** | ES / NQ trading halts; the CME runs its **daily maintenance / settlement** (~4:00–5:00 pm) — squares books, strikes the **daily settlement price**, reopens ~5:00 pm. |

## The settlement / carryover trap (ADR-0013)
A position **held through** the ~4:00–5:00 pm maintenance window is **re-marked at the settlement price** — so the
mark you return to next session **isn't the last price you traded at**. This is an **unmanaged gap**, and the core
reason end-of-day handling leans on **resiliency + fail-over**, not on catching a live price.

## What this drives in the system
- **Auto-flatten is *our* feature (R-13), default ~2:30 pm CT, configurable.** Because it fires **ahead of** the
  venue's forced flatten (Topstep ~3:10) — and there is **no venue backstop at all on a live brokerage account**
  ([topstep-brokerage.md](topstep-brokerage.md)) — it must be **reliable on its own → redundancy** (the ADR-0013
  watchdog / independent trigger), not a lean on the venue.
- **End-of-day resiliency (ADR-0013):** on any disconnect / restart near the close, **reconcile from the venue as
  source of truth** (never trust local state), stay **aware of the maintenance window**, and **reconcile the
  settlement re-mark** — never present a stale pre-settlement mark as live.

## Open / to confirm
- Exact **settlement-price mechanics + timing per product** (ES / NQ vs. others) from the **CME rulebook**.
- Exact **prop-firm forced-flatten times** per firm / account (Topstep ~3:10; others?) — confirm in venue help centers.
- The CME **maintenance-window** boundaries per product (the ~4:00–5:00 pm CT figure is operator-stated; confirm).

## Relevant-link index
- *(to add: CME daily-settlement / trading-hours pages; Topstep auto-flatten help article.)*
