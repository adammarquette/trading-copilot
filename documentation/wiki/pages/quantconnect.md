# QuantConnect / LEAN — reference system

> **Trust tier:** authoritative (QuantConnect's official manual) · **Verified:** 2026-07-18
> **Source:** `sources/Quantconnect-Cloud-Platform-Python.pdf` — QuantConnect **Local Platform** manual (~107 pp)
> **Use:** **reference / example system — for insights only.** A comparable algo-trading platform catalogued to learn from; **not** a golden example, **not** a template to clone, and **nothing here is a requirement** for our system. Where it *differs* from ours is usually the most useful part.
> **Informs:** design thinking around R-1 (data), R-3/R-4 (indicators/suggestions), R-9 (replay), R-11 (execution).

QuantConnect is a cloud **algorithmic-trading platform** built on **LEAN**, its open-source backtesting-and-live-trading engine. One engine runs research, backtesting, optimization, and live deployment, so strategy code isn't rewritten between stages. Algorithms are Python/C# against a common `QCAlgorithm` API, developed in the browser IDE or (this manual's scope) a local VS Code + Docker setup; it ships a large managed data library and a Jupyter research environment (`QuantBook`).

**Scope of the ingested PDF:** it is the **Local Platform** manual (Docker + VS Code running LEAN locally, synced to the cloud) — installation, IDE tooling, project/data/results mechanics, deployment. It is *not* a deep algorithm-authoring API reference (the `initialize` / `on_data` model is inferred from examples and results metrics, not a tutorial).

## How it works (architecture / approach)
- **Event-model algorithm.** Subclass `QCAlgorithm`; `initialize()` runs once; the engine drives an `on_data(slice)` loop plus separate hooks — scheduled callbacks, universe-selection changes, consolidator/indicator updates, corporate-action events, and order events. Each event type is a **separately-instrumented stream**.
- **Data as subscriptions + consolidators.** A per-symbol **subscription** at a chosen `Resolution`; **consolidators** aggregate raw subscription data into the bars that feed indicators. Universe selection is declarative and distinct from manually adding a security. Historical data is zip-compressed flat files from a "Dataset Market"; third-party providers vary (some backtest-only).
- **One engine, swap the target.** The same LEAN binary runs backtest / optimize / research / live — only the deployment target (Local Docker, Cloud, self-hosted Private Cloud) and data source change. The manual is candid that **parity still breaks in practice** (e.g. stale local reference data diverges from cloud).
- **Research notebook.** Each project has a `research.ipynb` (`QuantBook`: `qb.history(...)`, `qb.indicator_history(...)`) on its own compute node; prototype/train there, persist to an Object Store, load from backtest/live.
- **Live-only mechanics.** Brokerage 2FA re-auth, best-effort auto-restart, a manual **Liquidate** kill switch; no hot-editing live (stop → edit → redeploy).

## Insights worth *considering* (not adopting)
- **Subscription ≠ indicator bar size.** Separating "subscribed resolution" from "consolidated bar feeding an indicator" is a clean market-data mental model (R-1/R-3), even without a general consolidation framework.
- **One code path for historical and live.** Reusing the *same* generation path for replay-evaluation and live avoids "worked in sim, broke in prod" drift — relevant to R-9 (evaluate suggestion quality on recorded data via the same path, not a parallel backtest-only implementation).
- **Typed, separately-instrumented event streams** (data / schedule / universe / order / corporate-action) keep a growing event loop legible — a structural pattern worth emulating (pairs with our tracing, ADR-0002).
- **Order-event vocabulary** (submit / fill / partial fill / update / cancel / exercise) is a sane reference state machine for execution tracking (R-11).
- **Standard performance stats** (Sharpe, PSR, CAGR, win rate, **expectancy**, profit/loss ratio, drawdown) — solid vocabulary for scoring suggestion quality or a paper mode (R-9).
- **Explicit overfitting warning** on parameter optimization ("may not be robust… out-of-sample") — worth carrying verbatim if we ever auto-tune thresholds.

## The key difference from ours (the most useful insight)
QuantConnect is **engine-first / autonomous**: the algorithm has full authority to submit orders directly; the "human in the loop" is a *developer iterating on code between deployments*, and the only runtime risk control shown is a post-hoc **Liquidate**. Ours is **approval-first / human-in-the-loop**: suggestions surface to a human in real time, execution follows explicit **approval** (R-11), and risk is gated **pre-suggestion** (R-5: max size, daily loss) — *stronger than a post-hoc kill switch*. Consequences:
- We likely **don't need** QC's general historical-simulation engine, universe-selection framework, or multi-tenant org/collaboration system.
- We **do need** things QC never had to build: an **approval / gate abstraction**, real-time **per-suggestion explainability**, and an **execution-state machine** for our own order tracking.

QC's in-IDE AI assistant ("Mia") helps a *developer* write LEAN code — an adjacent-but-different use of AI in trading tools than ours (helping a *trader* make and approve real-time decisions). A landmark on the spectrum, not a template.

## Section index (PDF pages, in `sources/`)
Key Concepts 5 · Installation 14 · Dev Environment 22–40 · Private Cloud 41 · Projects 44 · Datasets 57 · **Backtesting (Results 70)** 65 · Research 90 · Optimization 97 · Live Trading 102 · Object Store 106.
