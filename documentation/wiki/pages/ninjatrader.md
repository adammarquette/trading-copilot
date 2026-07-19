# NinjaTrader

> **Trust tier:** authoritative
> **Verified:** vendor site, 2026-07-19 · **Sources:** https://ninjatrader.com/trading-platform
> **Informs:** design thinking — R-3 (DOM/order flow), R-9 (replay), R-11 (order/ATM), R-14 (sim), ADR-0007

A **reference / example system** — a platform the operator has used — **not a venue we integrate and not a template
to clone**. NinjaTrader is a **futures-focused discretionary trading platform + brokerage** (desktop / web / mobile,
synchronized charts / watchlists / accounts). Catalogued for the UX/feature ideas worth considering. Ours differs
*in kind* — an **AI co-pilot** (suggestion → approval → learning), not a manual + user-scripted terminal.

## Insights worth considering
- **ATM strategies (Automated Trade Management)** — **brackets, stop-loss, and self-tightening trailing stops** set
  once on a live position, to "eliminate emotions." Directly informs our **stop model** (staged stop + always-native
  safety stop) and **bracket / order ticket** (R-11, ADR-0007) — position management the human configures, then the
  system runs.
- **SuperDOM** — precision **DOM order entry** (place / modify from the ladder). Feeds the **order-flow pane**
  (ADR-0004) + the **order ticket** (R-11) UX.
- **Market replay / simulation** — tick-by-tick replay + **paper trading on live data**. Echoes our **replay** (R-9)
  and **practice accounts** (R-14).
- **NinjaScript (C#)** — user-scripted custom indicators / strategies. We're **C#/.NET** too, but our extensibility
  is **AI suggestions + rules** (R-4/R-7), not user scripts — still a useful reference for the indicator model.
- **Multi-surface sync** (desktop / web / mobile) — echoes our responsive + multi-screen direction (ADR-0005/0006).

## What we do differently
NinjaTrader is a **manual / user-scripted** terminal. Our co-pilot adds AI **suggestions** (R-4), an **enforcing
risk gate + daily governor** (R-5), **journaling + learning + AI follow-ups** (R-8/R-9/R-6), and a **deterministic
trigger layer** (ADR-0008). We reuse *ATM / DOM / replay UX*, not the manual trading model.

## Relevant-link index
- Trading platform (overview) — https://ninjatrader.com/trading-platform
