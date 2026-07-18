# PRD: Personal Futures Trading Co-Pilot

**Working title:** MarqSpec Trading Co-Pilot (placeholder — rename freely)
**Author:** Adam
**Status:** Draft v2 (execution added)
**Date:** 2026-07-17

---

## 1. Problem Statement

A discretionary futures day trader operating under prop-firm constraints (TopstepX: mandatory flat by CME close at 3:00 PM CST, daily loss limits, trailing drawdown) must synthesize technical indicators, order-flow signals (tape, footprint/delta, volume profile), news, and soft social signals (X/Twitter, YouTube influencers) into precise trade decisions — in real time, alone, every session. Today this synthesis is manual, inconsistent across sessions, and leaves no structured record connecting *why* a trade was expected to work with *whether it actually did*. The cost is missed setups, undisciplined risk sizing, and no compounding improvement loop.

This platform is a **decision-support and execution system with a human in the loop**: it ingests all relevant feeds, generates fully specified trade suggestions (direction, entry, stop, targets, size), explains its reasoning, converses with the trader, **lets the trader place and manage those trades through the system itself**, and journals every suggestion and every trade against its actual outcome — so that both the trader and the system get measurably better over time. Making the system the execution surface is what closes the loop: intent (the suggestion) and outcome (the fill) are captured natively in one place rather than reconciled after the fact.

The trader always initiates entries. The system's only autonomous action is a **risk-reducing** one: auto-flattening open positions before the CME close so the flatten rule can never be busted.

It is also deliberately a **learning vehicle for agentic AI development**: persistent memory (the rulebook), multi-source signal synthesis, feedback loops, tool-using agents, and a verify-before-act execution gate are first-class design goals, not incidental implementation details.

## 2. Goals

1. **Positive expectancy support:** Trades taken through the system achieve a positive average R-multiple over rolling 30-day windows, measurable from the journal.
2. **Native, complete journaling:** 100% of suggestions and 100% of trades are recorded — suggestions with their parameters, market conditions, influencing factors, and stated expectation; trades with their actual fills captured natively from the execution path (no fuzzy matching).
3. **Faster sessions:** Reduce pre-market prep and in-session analysis time by consolidating charts, order flow, news, social signals, suggestions, and execution into one surface.
4. **A system that learns:** The chat-authored rulebook and the empirical journal both demonstrably influence future suggestions (traceable in each suggestion's rationale).
5. **Uncompromised discipline:** The flatten deadline and prop-rule limits are enforced by the system, not merely surfaced — zero busted flattens, zero orders sent without confirmation (auto-flatten excepted).
6. **Agentic AI mastery:** Ship working implementations of durable agent memory, multi-source synthesis, an outcome-driven feedback loop, and a safety-gated execution/verification layer.

## 3. Non-Goals (v1)

- **Autonomous entry / auto-send.** The system never opens or adds to a position on its own. Every entry requires the trader either to build the ticket manually or to explicitly take a suggestion that the system re-validates at execution time. *The sole exception is auto-flatten, which only reduces or closes exposure — never opens it.*
- **Multi-user / productization.** Single-user personal tooling. No auth beyond securing the deployment itself, no tenancy, no billing.
- **Paid market-data or paid social feeds.** v1 restricts to ProjectX, free news APIs/RSS, scraped news sites, X/Twitter, YouTube transcripts, and TradingView MCP. No exchange data subscriptions.
- **HFT-grade latency guarantees.** Order-flow analytics and execution must be timely enough for scalping decisions (seconds), not microsecond-competitive.
- **A general backtesting engine.** Suggestion/trade outcome simulation (R-9) is required; a full strategy backtester is out of scope for v1.
- **Distinguishing practice from live in the data/learning pipeline.** Practice and live trades flow through identical ingestion, journaling, and learning. They are distinguished *only* for safety display and guardrails (R-14) — never for how outcomes are recorded or learned from.
- **Financial advice for anyone but the operator.** Personal decision-support and execution tooling; nothing here is investment advice for third parties.

## 4. Users & Context

**Persona (n=1):** Senior software engineer and active futures day trader. Trades CME products through TopstepX. Both scalps (seconds–minutes) and intraday moves (minutes–hours). Hard constraint: flat by 3:00 PM CST daily. Decision inputs: technical indicators, chart reading, order flow, news, and soft signals from social sources.

**Operating context:**
- **Session-aware and self-enforcing.** Every suggestion carries time-to-close context, and the system *enforces* the flatten deadline by auto-closing open positions before 3:00 PM CST (R-13). New-entry cutoffs shrink as close approaches.
- **Prop-rule aware and enforcing.** Daily loss limit and trailing drawdown are live constraints that can block or resize an actual order (R-5), not report-time footnotes.
- **Practice-first, behavior-identical.** The trader will begin on a practice account. The system behaves identically to live in every respect except safety display and guardrails, so lessons and stats transfer directly when live trading begins.

## 5. User Stories

**Suggestions**
- As a day trader, I want fully specified trade suggestions (direction, entry, stop, target(s), size) with rationale and confidence, so I can evaluate and execute quickly without reconstructing the analysis myself.
- As a day trader, I want continuous market scanning across my watchlist with proactive alerts when a qualifying setup forms, so I don't miss opportunities while focused on another chart.
- As a day trader, I want to ask for a suggestion on demand ("what's the setup on NQ right now?") and get an answer grounded in live data.
- As a trader near the close, I want suggestions to account for time remaining until the 3:00 PM CST flatten deadline (adjusted targets, shrinking validity, entry cutoffs), so I'm never nudged into a position I can't responsibly hold.

**Execution**
- As a trader, I want to build an order ticket manually and send it through the system, so the trade is captured natively in the journal with full context.
- As a trader, I want to execute a suggestion in one click, but only after the system re-validates at that moment that it's still live — within its validity window, price still near the suggested entry, and risk limits still satisfied — so I never fire a stale ticket.
- As a trader, when a suggestion has gone stale at execution time, I want the system to tell me why and require a fresh decision rather than silently sending it.
- As a trader, I want the layered risk model to gate real orders — blocking or resizing anything that would breach my prop rules or manual caps — so the system can't help me violate my account.
- As a trader, I want a kill switch that instantly disables all outbound order capability, so I can hard-stop the system in one action if something looks wrong.

**Flatten enforcement**
- As a trader, I want the system to automatically flatten all open positions before the 3:00 PM CST close, so I can never bust the flatten rule even if I'm distracted or away from the desk.
- As a trader, I want loud escalating warnings as the flatten window approaches, and confirmation that the auto-flatten actually closed everything, so I'm never left guessing about my exposure at the close.

**Practice vs. live**
- As a trader, I want to run on a practice account with the system behaving identically to live, so my stats and habits transfer directly.
- As a trader, I want an unmistakable, always-visible indicator of whether I'm in practice or live mode, and a deliberate, hard-to-fumble switch between them, so I'm never confused about whether real money is at risk.

**Risk**
- As a funded trader, I want position sizing computed from layered constraints — Topstep daily loss/drawdown rules, fixed %-risk per trade, and my own manual caps — with the binding constraint identified, so every order is executable without violating my account rules.
- As a trader mid-drawdown, I want the system to visibly tighten sizing (or say "no trade") as I approach daily limits.

**Order flow**
- As an order-flow trader, I want tape (time & sales), footprint/bid-ask delta, and volume profile computed from ingested tick data and cited in suggestion rationales, so order-flow context is a first-class signal.

**Soft signals**
- As a trader, I want news items, tweets, and YouTube commentary relevant to my watchlist surfaced with source, timestamp, and a relevance/sentiment read.

**Chat & rulebook**
- As a trader, I want multi-turn conversations about markets, setups, and my own behavior, grounded in live platform data.
- As a trader, I want statements I make in chat ("I don't trade the first 5 minutes", "no new positions on FOMC days") to persist as durable rules that shape future suggestions.
- As a trader, I want to view, edit, disable, and delete rulebook entries, and see which rules influenced any given suggestion.

**Journal & feedback loop**
- As a trader, I want every suggestion and every trade journaled automatically — parameters, market-condition snapshot, influencing factors, expectation, and (for trades) native fills — so nothing depends on my note-taking.
- As a trader, I want expectancy, hit rate, and calibration (expected vs. actual) reports sliced by instrument, setup type, time of day, and signal source.
- As a trader, I want to exclude a trade record from the learning signal — for fat-fingers, test trades, or practice noise — while keeping an honest audit record, so I can clean the training signal without laundering my real history.

**Charts**
- As a trader, I want in-app charts with suggestion and live-position overlays (entry/stop/target, working orders, fills), order-flow visualizations, and event markers, with TradingView available as a supplemental view.

## 6. Requirements

### Must-Have (P0)

**R-1: Market data ingestion (ProjectX).** Always-on cloud service ingesting the configurable watchlist via `MarqSpec.Client.ProjectX`: OHLCV bars (multiple resolutions) and real-time tick/quote/trade streams sufficient to reconstruct tape, footprint/delta, and volume profile. Persist to time-series storage.
- [ ] Watchlist user-configurable without redeploy
- [ ] Bars and ticks persisted with gap detection and backfill on reconnect
- [ ] Ingestion uptime covers the full CME session with automatic recovery
- [ ] Account state (balance, daily P&L, drawdown headroom), working orders, and fills are ingested for risk sizing, execution, and journal resolution

**R-2: Soft-signal ingestion.** Independent services ingesting free news APIs/RSS, scraped news sites, X/Twitter, and YouTube transcripts into a normalized event store (source, timestamp, content, instruments referenced, sentiment/relevance).
- [ ] Each source type individually deployable/disableable; configurable source lists
- [ ] Deduplication across sources; queryable by instrument and time window
- [ ] Scrapers respect robots.txt and rate limits; any single source failing degrades gracefully

**R-3: Order-flow analytics.** From ingested ticks: time & sales tape, footprint / bid-ask delta (per-bar and cumulative), and volume profile (session and composite).
- [ ] Available to both the suggestion engine and the chart layer
- [ ] Fresh enough for scalp decisions (target: analytics current within ~1–2s of tape)

**R-4: Suggestion engine.** Generates fully specified suggestions: direction, entry, stop, target(s), size, plus rationale (signals cited), confidence, and a validity window.
- [ ] Triggered both on demand (via chat) and by continuous scanning with proactive alerts
- [ ] Rationale cites contributing signals: indicators, order flow, news/social events, and rulebook entries applied
- [ ] Session-clock aware: validity windows respect the 3:00 PM CST deadline; configurable no-new-entry cutoffs per trade style
- [ ] Suggestions are versioned/immutable once issued (updates issue a superseding suggestion, preserving journal integrity)

**R-5: Layered risk model (enforcing).** Position size derives from three stacked constraint layers — Topstep prop rules (daily loss limit, trailing drawdown headroom), fixed %-risk per trade, and manual limits (max contracts, per-instrument caps) — most restrictive wins. This model **gates real orders**, not just suggestions.
- [ ] Each suggestion and each order shows computed size and which layer was binding
- [ ] An order that would breach any layer is blocked or resized before it can be sent
- [ ] Emits "no trade" when constraints leave no viable size
- [ ] Live account state from ProjectX feeds the prop-rule layer intraday

**R-6: Multi-turn chat.** Conversational interface grounded in platform state (market data, order-flow analytics, soft-signal events, journal, rulebook, live positions/orders). Supports analysis, on-demand suggestions, journal queries, and rulebook management.
- [ ] Chat can invoke platform capabilities as tools (quote data, generate suggestion, query journal, edit rulebook, read positions). *Chat does not place orders — execution is an explicit UI action (R-11).*
- [ ] Conversation history persists across sessions

**R-7: Durable rulebook.** Trading practices expressed in chat persist as structured rules conditioning future suggestions.
- [ ] Rules extracted from conversation with explicit confirmation before persisting
- [ ] Viewable, editable, disableable, deletable in the UI
- [ ] Every suggestion's rationale lists the rules that influenced it (including suppressions: "no suggestion — rule X")
- [ ] Rule conflicts surfaced, not silently resolved

**R-8: Suggestion & trade journal.** Every suggestion is recorded with full parameters, a market-condition snapshot at issuance, influencing factors, and stated expectation. Every trade is recorded with its originating suggestion (if any), entry rationale, and **native fills from the execution path**.
- [ ] Journal writes occur automatically — at suggestion issuance and at order/fill events — with no user action required
- [ ] Trader can annotate entries (notes, emotional state — optional)

**R-9: Outcome resolution & feedback loop.** Journal entries resolve to actual results and the aggregate feeds future suggestions.
- [ ] Trades executed through the system resolve from native fills (no matching heuristics)
- [ ] Suggestions the trader did *not* take resolve by simulating the suggested parameters against recorded market data (fill assumptions documented and consistent)
- [ ] Reports: expectancy, hit rate, expectation-vs-actual calibration, sliced by instrument, setup/signal type, time of day, and taken-vs-passed
- [ ] Journal statistics available to the suggestion engine and cited when they materially affect a suggestion
- [ ] Records soft-deleted / excluded from learning (R-15) are omitted from the training signal; reports can be viewed with and without excluded records

**R-10: Charting.** Custom in-app charts primary: multi-resolution candlesticks with standard indicators, suggestion overlays (entry/stop/target zones), **live-position and working-order overlays, fill markers**, order-flow visualizations (delta, volume profile), and event markers (news/social, alerts). TradingView (MCP/embed) supplemental.
- [ ] Suggestion and position overlays render live and persist historically for journal review
- [ ] Chart, alerts, chat, and the order/execution panel coexist in one workspace

**R-11: Order execution (human-in-the-loop).** The system places, modifies, and cancels orders via `MarqSpec.Client.ProjectX`, but only on an explicit user action. Two entry paths:
- **(a) Manual ticket:** the trader builds an order (instrument, side, size, order type, stop/target) and sends it.
- **(b) Take a suggestion:** the trader clicks to execute an existing suggestion.
- [ ] Every entry is an explicit, deliberate user action — no order is ever auto-sent (auto-flatten in R-13 excepted)
- [ ] All orders pass through the R-5 risk gate and the R-16 sanity caps before transmission
- [ ] Order state (working, partial, filled, cancelled, rejected) is reflected in the UI in real time and written to the journal
- [ ] A single **kill switch** instantly disables all outbound order capability and cancels working orders

**R-12: Execution-time re-validation.** When the trader takes a suggestion (R-11b), the system re-checks — at the moment of execution — that the suggestion is still valid: within its validity window, current price within a configurable tolerance of the suggested entry, and R-5 constraints still satisfied.
- [ ] A still-valid suggestion is sent on confirmation
- [ ] A stale or breached suggestion is blocked; the system shows *why* (expired, price drifted, risk changed) and requires a fresh decision — never silently transmits a stale ticket
- [ ] The validity/tolerance parameters are configurable

**R-13: Auto-flatten at close (safety-critical).** The system automatically flattens all open positions before the 3:00 PM CST CME close. This is the only order action the system performs without per-trade confirmation, and it only reduces/closes exposure.
- [ ] Fires at a configurable lead time before close; escalating warnings precede it
- [ ] After firing, the system **verifies positions are actually flat** and retries/escalates loudly if any position remains
- [ ] Redundant trigger with a defined failure mode if the primary path is unavailable at close (see Open Questions Q-3)
- [ ] Auto-flatten cannot be silently disabled; disabling it is a deliberate, clearly-warned action
- [ ] Every auto-flatten action is journaled

**R-14: Practice/live parity + mode safety.** Practice and live accounts use an identical data, journaling, and learning pipeline. They differ only in safety surface.
- [ ] An always-visible, unmistakable indicator shows current mode (practice vs. live)
- [ ] Switching to live is a deliberate, confirmed action; the UI makes accidental live trading hard
- [ ] All guardrails (R-5 risk gate, R-11 confirmation, R-13 auto-flatten, R-16 caps, kill switch) apply identically in both modes
- [ ] Journal/learning treat practice and live outcomes identically (no flag that changes how they're learned from)

**R-15: Record removal — soft delete (default) + hard delete.** The trader can remove a trade record from the learning signal and default views via two operations:
- **Soft delete (default):** a `deleted` toggle that excludes the record from all suggestion-engine training and default stats and hides it from default views, while retaining the full record and an audit trail. Reversible.
- **Hard delete (explicit):** permanently erases the record. A deliberate, confirmed action and the exception — not the default — since it removes the honest-recovery path.
- [ ] Soft delete is the default removal action and is reversible (un-toggling restores the record to learning and stats)
- [ ] The soft-delete audit trail preserves the original record and the exclusion (what, when, why-optional)
- [ ] Reports can be toggled to show figures inclusive vs. exclusive of soft-deleted records, so the honest picture stays recoverable
- [ ] Hard delete requires explicit confirmation; the fact that a deletion occurred is logged even though the content is gone
- [ ] Neither operation affects broker/account records — removal is local to this platform's journal and learning signal

**R-16: Execution sanity caps.** Independent of the risk model, hard caps that block obviously wrong tickets (max contracts per order, max notional, fat-finger price bounds) as a last line of defense before transmission.
- [ ] Caps apply to both manual and take-suggestion paths, in both practice and live
- [ ] A blocked order explains which cap it hit

**R-17: Broker/venue abstraction — target multiple trading APIs.** Market-data ingestion (R-1), account state (R-1/R-5), and order execution/flatten (R-11/R-13) are defined against **venue-neutral interfaces**, so the platform can target different trading APIs — prop firms and brokerages — without re-architecting. v1 implements one adapter, **ProjectX/TopstepX** (`MarqSpec.Client.ProjectX`); adding another venue (e.g., **Tradovate**) is a new adapter behind the same interfaces, not a rewrite.
- [ ] Broker-specific code is isolated to a per-venue adapter; the suggestion engine, risk model, execution gate, journal, and UI depend only on venue-neutral abstractions
- [ ] Instruments, accounts, orders, and fills are venue-tagged end-to-end, so risk, P&L, and the journal stay correct if accounts span venues
- [ ] Venue capability differences are explicit (a venue may lack an order type or data stream); the system degrades gracefully when a capability is absent
- [ ] v1 ships the ProjectX adapter only; adding a second venue requires no change to the core, with per-venue credentials/endpoints/mode isolated server-side (engineering guide §8)

### Nice-to-Have (P1)

- **Morning briefing:** pre-market per-instrument summary — overnight action, volume-profile context, scheduled economic events, news/social digest.
- **Alert & flatten notifications beyond the UI:** push/mobile channel for proactive alerts and — critically — flatten warnings and confirmations when away from the desk.
- **Economic calendar integration:** scheduled events (FOMC, CPI, NFP) as first-class signals and rulebook conditions.
- **Source credibility scoring:** track predictive value of individual soft-signal sources from journal outcomes; weight or flag accordingly.
- **Rulebook change analysis:** replay a proposed rule against journal history before persisting.
- **Bracket/OCO order support:** submit stop and target as linked orders at entry (subject to ProjectX capability — see Q-1).
- **Session replay:** step through a recorded session (tape, chart, suggestions, orders, fills) for review.

### Future Considerations (P2)

- **Staged entry autonomy:** per-trade auto-execution of high-confidence setups within tight bounds, then bounded autonomous entry. *Architectural insurance now:* the execution gate (R-5/R-12/R-16) is already the authoritative checkpoint; autonomy would attach behind it, not around it.
- **Additional trading venues, asset classes & data vendors behind the R-17 abstraction** — e.g., Tradovate and other prop firms/brokerages as new venue adapters, options monitoring (already on the MarqSpec roadmap), and additional data vendors. R-17 makes each an adapter, not a rewrite.
- **Full strategy backtesting** on the recorded tick/bar archive.
- **Multi-account support** (multiple funded accounts with independent rule and risk layers) — potentially spanning several venues (R-17).

## 7. Success Metrics

**Evaluation points:** 30 / 60 / 90 days after v1 is live in daily use.

**Safety (hard criteria — pass/fail, not trends):**
- **Zero busted flattens.** Auto-flatten closes all positions before 3:00 PM CST in 100% of sessions; any residual position at close is a Sev-1 defect.
- **Zero unconfirmed entries.** No order is ever transmitted without explicit user confirmation (auto-flatten excepted).
- **100% risk-gate coverage.** No order reaches the broker without passing the R-5 risk gate and R-16 caps.

**Leading indicators (days–weeks):**
- Platform used in ≥ 90% of trading sessions
- 100% of suggestions and trades journaled; taken trades captured natively from fills
- On-demand suggestion latency ≤ 10s; scan-alert delivery ≤ 5s; order transmission acknowledged ≤ 2s
- Order-flow analytics freshness within ~1–2s of tape
- Rulebook grows and is exercised (cited in rationales weekly)

**Lagging indicators (30–90 days):**
- **Expectancy:** positive average R-multiple on trades over rolling 30 days; trend vs. pre-platform baseline
- **Calibration:** gap between stated confidence/expected R and realized outcomes narrows over successive windows
- **Time saved:** self-reported prep time reduced (baseline vs. 60-day check)

**Learning-goal indicators:**
- Working, inspectable implementations shipped for: durable agent memory (rulebook), multi-source synthesis (rationale), outcome-driven feedback (journal → engine), and a safety-gated execution/verification layer

## 8. Open Questions

**Blocking (resolve before/at build start):**
1. *(Engineering)* **ProjectX order API capabilities.** Which order types are supported (market, limit, stop, bracket/OCO)? Are modify and cancel exposed? What is position-query latency? This gates both R-11 execution design and R-13 auto-flatten reliability.
2. *(Engineering)* **ProjectX order-flow granularity.** Do the streams expose trade-by-trade data with aggressor side (or bid/ask context) sufficient for footprint reconstruction — or is aggregation/inference required?
3. *(Engineering — safety-critical)* **Auto-flatten guarantee & failure mode.** How is the flatten *guaranteed* to fire — redundant scheduler, independent watchdog? What happens if the cloud tier is unreachable or a flatten order is rejected at 2:59 PM? Does the local UI hold a fallback flatten path? Define the failure mode explicitly before any live trading.
4. *(Engineering)* **Practice vs. live account handling in ProjectX.** How does the API distinguish them (separate credentials/endpoints)? Can both be connected at once? How is an accidental live connection prevented?
5. *(Engineering)* **Untaken-suggestion simulation rules.** Fill assumptions (touch vs. trade-through, slippage, partial-target handling) so simulated R is consistent and honest.
6. *(Engineering)* **X/Twitter access method.** Official API tiers vs. scraping — cost, ToS exposure, reliability.

**Non-blocking (resolve during implementation):**
7. *(Engineering)* Execution-time validity tolerance (R-12): how far can price drift from a suggested entry before "stale"? Fixed ticks, ATR-relative, or per-setup?
8. *(Engineering)* YouTube transcript pipeline: channels, transcript source (captions vs. STT), latency tolerance.
9. *(Engineering)* Local UI packaging (hosted web app vs. desktop shell) — driven by charting choice and notification/kill-switch needs.
10. *(Engineering)* Cloud host and monthly cost ceiling for the always-on tier (Railway is a natural candidate).
11. *(Product)* Rulebook representation: structured DSL vs. natural-language rules interpreted at suggestion time.
12. *(Product)* Alert taxonomy and noise budget (max alerts/hour; measuring alert fatigue).
13. *(Legal/personal)* Scraping posture per news site (ToS review before adding).
14. *(Engineering)* Venue capability matrix (R-17): which trading APIs beyond ProjectX (Tradovate named), and how they differ in order types, order-flow granularity, and account model — defining what the venue-neutral interface must abstract.

## 9. Phasing

Live execution is deliberately gated: **auto-flatten and every guardrail must be proven on a practice account before live trading is enabled.**

**Phase 1 — Foundation (suggest-only):** ProjectX market + account ingestion, time-series storage, watchlist config, custom charts with indicators, on-demand suggestions via chat with layered risk sizing and session-clock awareness, automatic suggestion journaling at issuance. *Exit: daily use for on-demand analysis with every suggestion journaled.*

**Phase 2 — Execution on practice (safety first):** Order execution (manual ticket + take-suggestion) with the R-5 risk gate, R-12 execution-time re-validation, R-16 sanity caps, kill switch, native fill capture into the journal, R-13 auto-flatten, and R-14 practice/live mode plumbing — all exercised **on a practice account only**. *Exit (gate to live): auto-flatten fires reliably every session, no unconfirmed or un-gated orders, native fills captured cleanly. Only then is live mode enabled — same code, flipped mode.*

**Phase 3 — Order flow & proactivity:** tape/footprint/volume-profile analytics and visualizations; continuous scanning with proactive alerts; untaken-suggestion simulation and first expectancy/calibration reports; exclude-from-learning (R-15). *Exit: alerts trusted enough to act on; journal producing weekly review material.*

**Phase 4 — Soft signals & learning loops:** news/RSS, scrapers, X, YouTube ingestion with relevance/sentiment; rulebook persistence from chat with rationale citation; journal statistics feeding the engine; TradingView supplemental. *Exit: suggestions demonstrably shaped by both rulebook and journal history.*

**Hard constraints throughout:** the flatten deadline and prop-rule risk layering are enforced from the moment execution exists (Phase 2) — never deferred, never optional.

---

> **Companion document:** engineering practices — the AgentForge architecture mapping and the development-time knowledge wiki — live in the separate *Engineering Guide* (`trading-platform-engineering.md`), so this PRD stays focused on product requirements.

---

*Human-in-the-loop by design (v1): the trader initiates and confirms every entry; the system's only autonomous action is auto-flattening to reduce risk before the close. This is personal decision-support and execution tooling for its operator — it is not investment advice for third parties.*
