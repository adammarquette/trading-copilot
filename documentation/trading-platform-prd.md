# PRD: Personal Futures Trading Co-Pilot

**Working title:** MarqSpec Trading Co-Pilot (placeholder — rename freely)
**Author:** Adam
**Status:** Draft v2 (execution added)
**Date:** 2026-07-17

---

## 1. Problem Statement

A discretionary futures day trader operating under prop-firm constraints (TopstepX: mandatory flat by the venue's forced-flatten ~3:10 PM CT, daily loss limits, trailing drawdown) must synthesize technical indicators, order-flow signals (tape, footprint/delta, volume profile), news, and soft social signals (X/Twitter, YouTube influencers) into precise trade decisions — in real time, alone, every session. Today this synthesis is manual, inconsistent across sessions, and leaves no structured record connecting *why* a trade was expected to work with *whether it actually did*. The cost is missed setups, undisciplined risk sizing, and no compounding improvement loop.

This platform is a **decision-support and execution system with a human in the loop**: it ingests all relevant feeds, generates fully specified trade suggestions (direction, entry, stop, targets, size), explains its reasoning, converses with the trader, **lets the trader place and manage those trades through the system itself**, and journals every suggestion and every trade against its actual outcome — so that both the trader and the system get measurably better over time. Making the system the execution surface is what closes the loop: intent (the suggestion) and outcome (the fill) are captured natively in one place rather than reconciled after the fact.

The trader always initiates entries. The system's only autonomous action is a **risk-reducing** one: auto-flattening open positions at the operator's **configurable deadline (default ~2:30 PM CT, ahead of MOC — well before the CME close)** so the flatten rule can never be busted.

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
- **Billing / monetization & team roles.** The app is **self-hosted, one operator per deployment** (ADR-0015) — it authenticates because it is web-exposed, not because it serves a user base. **Paid plans, subscriptions, and billing are out of scope**, as are **team / admin role hierarchies** (the claims/policy layer stays ready for them should that ever change).
- **Paid market-data or paid social feeds.** v1 restricts to ProjectX, free news APIs/RSS, scraped news sites, X/Twitter, YouTube transcripts, and TradingView MCP. No exchange data subscriptions.
- **HFT-grade latency guarantees.** Order-flow analytics and execution must be timely enough for scalping decisions (seconds), not microsecond-competitive.
- **A general backtesting engine.** Suggestion/trade outcome simulation (R-9) is required; a full strategy backtester is out of scope for v1.
- **Distinguishing practice from live in the data/learning pipeline.** Practice and live trades flow through identical ingestion, journaling, and learning. They are distinguished *only* for safety display and guardrails (R-14) — never for how outcomes are recorded or learned from.
- **Financial advice for anyone but the operator.** Personal decision-support and execution tooling; nothing here is investment advice for third parties.

## 4. Users & Context

**Persona (the individual user):** Senior software engineer and active futures day trader. Trades CME products through TopstepX. Both scalps (seconds–minutes) and intraday moves (minutes–hours). Hard constraint: flat by the venue's forced-flatten (~3:10 PM CT) daily. Decision inputs: technical indicators, chart reading, order flow, news, and soft signals from social sources. This describes **the operator** — the person the deployment belongs to, who runs their own instance against their own broker credentials (ADR-0015).

**Operating context:**
- **Session-aware and self-enforcing.** Every suggestion carries time-to-close context, and the system *enforces* the flatten deadline by auto-closing open positions at the **configurable deadline (default ~2:30 PM CT, pre-MOC)** (R-13). New-entry cutoffs shrink as the deadline approaches.
- **Prop-rule aware and enforcing.** Daily loss limit and trailing drawdown are live constraints that can block or resize an actual order (R-5), not report-time footnotes.
- **Practice-first, behavior-identical.** The trader will begin on a practice account. The system behaves identically to live in every respect except safety display and guardrails, so lessons and stats transfer directly when live trading begins.

## 5. User Stories

**Suggestions**
- As a day trader, I want fully specified trade suggestions (direction, entry, stop, target(s), size) with rationale and confidence, so I can evaluate and execute quickly without reconstructing the analysis myself.
- As a day trader, I want continuous market scanning across my watchlist with proactive alerts when a qualifying setup forms, so I don't miss opportunities while focused on another chart.
- As a day trader, I want to ask for a suggestion on demand ("what's the setup on NQ right now?") and get an answer grounded in live data.
- As a trader near the close, I want suggestions to account for time remaining until the **flatten deadline (default ~2:30 PM CT)** (adjusted targets, shrinking validity, entry cutoffs), so I'm never nudged into a position I can't responsibly hold.

**Execution**
- As a trader, I want to build an order ticket manually and send it through the system, so the trade is captured natively in the journal with full context.
- As a trader, I want to execute a suggestion in one click, but only after the system re-validates at that moment that it's still live — within its validity window, price still near the suggested entry, and risk limits still satisfied — so I never fire a stale ticket.
- As a trader, when a suggestion has gone stale at execution time, I want the system to tell me why and require a fresh decision rather than silently sending it.
- As a trader, I want the layered risk model to gate real orders — blocking or resizing anything that would breach my prop rules or manual caps — so the system can't help me violate my account.
- As a trader, I want a kill switch that instantly hard-stops the system in one action — cancelling orders and (by default) flattening all positions, or halting-only if I've set that preference — so I can abort if something looks wrong.

**Flatten enforcement**
- As a trader, I want the system to automatically flatten all open positions at my **configurable deadline (default ~2:30 PM CT, ahead of MOC)**, so I can never bust the flatten rule even if I'm distracted or away from the desk.
- As a trader, I want loud escalating warnings as the flatten window approaches, and confirmation that the auto-flatten actually closed everything, so I'm never left guessing about my exposure at the close.

**Practice vs. live**
- As a trader, I want to run on a practice account with the system behaving identically to live, so my stats and habits transfer directly.
- As a trader, I want an unmistakable, always-visible indicator of whether I'm in practice or live mode, and a deliberate, hard-to-fumble switch between them, so I'm never confused about whether real money is at risk.
- As a trader at a prop firm, I want to declare for myself which of my firm's account stages actually put money at risk, because the platform's own "simulated" flag says where an order *executes*, not what a breach *costs* — my funded account is simulated on both counts and real on the one that matters (gh#60).
- As a trader, I want an account the system can't classify to be **untradeable until I classify it**, rather than assumed safe, so a firm adding a new stage blocks instead of quietly becoming tradable.

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

**R-1: Market data ingestion (ProjectX).** Always-on cloud service ingesting the configurable watchlist via `MarqSpec.Client.ProjectX` on two distinct paths: a **live real-time stream** (ProjectX websockets — ticks/quotes/trades sufficient to reconstruct tape, footprint/delta, and volume profile) and **clean historical data** (periodic REST backfill of OHLCV bars, multiple resolutions). The live stream is **not conflated with** the authoritative historical series — they are stored and treated separately. Persist to time-series storage.
- [ ] Watchlist user-configurable without redeploy
- [ ] Bars and ticks persisted with gap detection and backfill on reconnect
- [ ] Ingestion uptime covers the full CME session with automatic recovery
- [ ] Account state (balance, daily P&L, drawdown headroom), working orders, and fills are ingested for risk sizing, execution, and journal resolution
- [ ] Live real-time stream and clean historical series are stored as distinct paths; the historical series (periodically refreshed from REST) is the system of record for bars used in journaling and replay (R-9)
- [ ] Additional **data-only market-data sources** ingest through the **same pipeline** via the R-17 abstraction — first is **Finnhub** (equities/indices such as SPY/QQQ, websocket + REST, ~50 symbols, free tier) supplying **cross-asset context** for the traded futures (SPY ↔ ES, NASDAQ/QQQ ↔ NQ); market data only, no account/execution

**R-2: Soft-signal ingestion.** Independent services ingesting free news APIs/RSS, scraped news sites, X/Twitter, and YouTube transcripts into a normalized event store (source, timestamp, content, instruments referenced, sentiment/relevance). **News is the reference template** for these (and future) non-market feeds: each normalizes to the common model and is retrievable both **structurally** (by instrument + time) and **semantically** (embedded for suggestion/chat context — engineering §3).
- [ ] Each source type individually deployable/disableable; configurable source lists
- [ ] **News is multi-source:** **Tiingo** for company/ticker-tagged news + **Finnhub** for market/macro news — each a per-source adapter normalizing to the common model (Finnhub company-news is left off as redundant with Tiingo)
- [ ] **Cross-source deduplication:** the same wire story from multiple feeds collapses to **one canonical record** (keeping each source's provenance), matched by **canonical URL** with a **fuzzy fallback** (title similarity + published-time window + ticker overlap), **before embedding**; queryable by instrument and time window
- [ ] **Configurable relevance model:** a **mapping / topics panel** — **ticker↔instrument** maps (SPY→ES, QQQ→NQ), **per-instrument topics**, and **global (market-wide) topics** — decides what news attaches to a traded instrument; the co-pilot **suggests topics** for a ticker and the user curates; an item matches via **ticker-map or topic** (tag / keyword / semantic)
- [ ] **Sentiment scoring is deferred** (not on the free tiers): candidate enrichments are a **subagent classifier** (cheap model — ADR-0008) and/or a **user thumbs-up/down** rating (**direction**) that feeds the learning loop (R-9)
- [ ] **Importance starring → personalized weighting:** the user can **star** a news / soft-signal item as important — a per-user signal (distinct from sentiment direction) that **raises the weight / salience of similar future items** (by matched instrument / topic, source, named entity, and semantic similarity) in surfacing and in how much they inform suggestions / chat. It is a **soft salience weight, not a risk control** (enforcement stays below the model), and it is **transparent and adjustable** (un-star; a natural inverse **mute** down-weights, with a salience floor so material items aren't hidden). Feeds the personalization / learning loop (R-6 / R-9). See [ADR-0014](adr/0014-news-importance-feedback.md)
- [ ] Scrapers respect robots.txt and rate limits; any single source failing degrades gracefully

**R-3: Order-flow analytics.** From ingested ticks: time & sales tape, footprint / bid-ask delta (per-bar and cumulative), and volume profile (session and composite).
- [ ] Available to both the suggestion engine and the chart layer
- [ ] Fresh enough for scalp decisions (target: analytics current within ~1–2s of tape)

**R-4: Suggestion engine.** Generates fully specified suggestions: direction, entry, stop, target(s), size, plus rationale (signals cited), confidence, and a validity window.
- [ ] Triggered both **on demand** (via chat) and by **continuous scanning** — a **deterministic trigger layer** evaluates conditions over pre-computed indicators / order-flow and, on a fire, emits a mechanical alert or wakes an agent to review; the LLM is **not** in the scan loop ([ADR-0008](adr/0008-ai-invocation-cost-model.md))
- [ ] Rationale cites contributing signals: indicators, order flow, news/social events, and rulebook entries applied
- [ ] Session-clock aware: validity windows respect the **flatten deadline (default ~2:30 PM CT, pre-MOC)**; configurable no-new-entry cutoffs per trade style
- [ ] **Risk-headroom aware:** as **daily-drawdown headroom** (R-5) depletes, the engine **throttles** suggestions (fewer / smaller / higher-conviction only) and **suppresses** new ones once the personal daily governor is reached — a proactive, suggestion-time governor ahead of the execution gate
- [ ] Suggestions are versioned/immutable once issued (updates issue a superseding suggestion, preserving journal integrity)
- [ ] **Lifecycle & invalidation:** a suggestion runs `active → stale → expired/void`. It invalidates on **time** (validity window / session deadline), **drift** (price beyond a configurable tolerance of the entry — surfaced **stale** *before* execution, not only re-checked at take-time per R-12), or **thesis break** (its premise fails — a later refinement). A scratched setup is **not chased**: if it re-forms, the engine issues a **new superseding suggestion**
- [ ] **Recovery is fail-safe (a suggestion carries no risk).** Suggestions are **server-side state**, so a client crash / disconnect loses nothing — the client re-syncs on reconnect (SignalR outbox + idempotent-resume, engineering §2). On a **backend restart or venue-connection loss**, suggestions **rehydrate from their store** and the normal lifecycle applies: any past its **validity window**, or whose drift / thesis broke during the gap, goes **stale → expired/void**; a survivor still must pass **R-12** before it can be taken; **nothing is auto-taken or silently resumed**. A re-formed setup is a **new superseding suggestion**, not a resurrected one. (Live positions are protected separately — native safety stops at the exchange + the connection-loss orphan handling, [ADR-0007](adr/0007-order-execution-model.md); the whole model is consolidated in [ADR-0013](adr/0013-failure-recovery-model.md).)
- [ ] Each suggestion is attributed to a named **strategy / setup** (e.g. VWAP-reclaim, opening-drive) so outcomes aggregate per strategy (R-9)
- [ ] The operator's **disposition** is captured — **taken, modified, passed, or expired** (a *pass* is a **neutral decline**, not a rejection of the co-pilot); a pass may attach an **optional reason** (a structured set spanning neutral → critical — e.g. already positioned, news risk, wrong time, weak R:R, against a rule, low conviction — plus a note), feeding the learning loop (R-9) and per-strategy analytics

**R-5: Layered risk model (enforcing).** Position size derives from three stacked constraint layers — Topstep prop rules (daily loss limit, trailing drawdown headroom), fixed %-risk per trade, and manual limits (max contracts, per-instrument caps) — most restrictive wins. This model **gates real orders**, not just suggestions.
- [ ] Each suggestion and each order shows computed size and which layer was binding
- [ ] With a **safety stop** (R-11), the gate treats the **safety-stop loss as the true worst case**: it must stay within the **hard account limits** (Topstep daily/trailing, manual caps), bounding how wide the safety stop can sit — the catastrophic case can never breach the account. *(**Configurable**: per-trade %-risk sized to the actual stop or to the safety stop.)*
- [ ] A **configurable max drawdown per trade** (the per-trade hard loss cap) **sets where the safety stop sits** (R-11): the safety stop is the price at which that cap is reached — the risk limit made physical, not an arbitrary level, so **max loss per trade is deterministic** regardless of sizing basis
- [ ] A **configurable max daily drawdown** — a personal governor set **inside** the hard prop daily-loss limit — is shown as live **headroom** and, as it depletes, **throttles / filters suggestions** (R-4) before the execution gate ever fires; new entries are blocked once it's reached
- [ ] The operator's **risk tolerance** is a configurable profile — **per-trade risk %**, a **target reward:risk** (e.g. **1.5 : 1** — reward 1.5× the risk; many traders hold a fixed ratio), plus max-DD-per-trade and the daily governor — that **seeds** position sizing (R-11) and benchmarks the **R:R KPI** (R-9)
- [ ] A per-account **daily profit target** and **consistency target** — the prop-firm consistency rule that **no single day may exceed a set % of total profit** (caps vary by firm/stage: **Topstep** Combine ≤ 50% of the profit target / Express ≤ 40%; **Take Profit Trader** ≤ 50%; **Apex** ~30% — wiki: prop-firm rules). On reaching the **daily target** the governor **stands down** — new suggestions are **suppressed** (R-4), sizing tightens, and an optional **"stop for the day"** flattens and locks the account — turning the operator's *"hit $1,500 and stop"* discipline into an **enforced behavior, not a reminder**. Only some accounts carry a consistency rule; adherence is tracked against **P&L-by-day** (R-9)
- [ ] **The risk budget is headroom to the (trailing) drawdown floor — not the account size.** A $50K account with a $3K max-loss limit **fails** if balance drops below the **~$47K floor**, and that floor **trails** the peak per firm rules. Sizing, the daily governor, and max-DD-per-trade all operate **inside this headroom**, and the gate **blocks any order that could breach the floor** (= losing the account); **buying power (50K/100K) is not the amount at risk**
- [ ] The loss floor is **firm-imposed** (prop-firm rule → breach *fails the account*) **or self-imposed** (a **configurable max-loss on a live / brokerage account** — e.g. keep $50K for margin but cap loss at $10K → a **$40K floor** → breach **halts new entries + flattens**, a personal circuit breaker). **Same risk-budget dynamic** either way; only the **source** and the **breach consequence** differ
- [ ] The trailing floor has a **mode** — **EOD** (moves at close on realized balance; fixed intraday) or **intraday** (follows the intraday peak in **real time, incl. unrealized PnL**; auto-liquidate on touch). **Intraday ⇒** the gate tracks the **real-time peak + moving floor** (being up unrealized *raises* the floor). **Mode is per account** — Topstep is EOD-only; **Apex sells both** EOD & intraday (since 2026-03-01) — so key it off the account, not the firm (wiki: prop-firm rules)
- [ ] An order that would breach any layer is blocked or resized before it can be sent
- [ ] Emits "no trade" when constraints leave no viable size
- [ ] Live account state from ProjectX feeds the prop-rule layer intraday

**R-6: Multi-turn chat.** Conversational interface grounded in platform state (market data, order-flow analytics, soft-signal events, journal, rulebook, live positions/orders). Supports analysis, on-demand suggestions, journal queries, and rulebook management.
- [ ] Chat can invoke platform capabilities as tools (quote data, generate suggestion, query journal, edit rulebook, read positions). *Chat does not place orders — execution is an explicit UI action (R-11).*
- [ ] Conversation history persists across sessions
- [ ] The co-pilot can **proactively ask follow-up questions** grounded in trades + their feedback (e.g. a pattern across recent losses) to **refine strategy** — turning feedback into candidate **rulebook** changes (R-7) on explicit confirmation

**R-7: Durable rulebook.** Trading practices expressed in chat persist as structured rules conditioning future suggestions.
- [ ] Rules extracted from conversation with explicit confirmation before persisting
- [ ] Viewable, editable, disableable, deletable in the UI
- [ ] Every suggestion's rationale lists the rules that influenced it (including suppressions: "no suggestion — rule X")
- [ ] Rule conflicts surfaced, not silently resolved
- [ ] Rules can **compile to deterministic triggers** — machine-evaluable conditions the scan layer watches — so a practice becomes an always-on alert without invoking the LLM to evaluate it ([ADR-0008](adr/0008-ai-invocation-cost-model.md))
- [ ] A confirmed rule captures an **instrument-dependency snapshot** (the Instrument / RelevanceConfig metadata it resolved against). If that metadata later changes (a symbol reclassified, a topic remapped), the rule is **flagged for review / re-validation** rather than silently firing on the wrong asset — no stale-scope triggers

**R-8: Suggestion & trade journal.** Every suggestion is recorded with full parameters, a market-condition snapshot at issuance, influencing factors, and stated expectation. Every trade is recorded with its originating suggestion (if any), entry rationale, and **native fills from the execution path**.
- [ ] Journal writes occur automatically — at suggestion issuance and at order/fill events — with no user action required
- [ ] Trader can annotate entries (notes, emotional state — optional)
- [ ] The journal presents a **trade blotter** and lets the operator **drill into a day's trades** — the **current day is the default view** — each trade linked to its originating suggestion, with entry/exit, P&L / R, strategy, and its feedback
- [ ] **Post-close feedback is optional and asynchronous:** the operator can add comments / structured feedback **at close when present**, but because trades may **arm and run unattended**, a closed trade without feedback is flagged **"awaiting review"** and feedback can be added **anytime** — it is **never required** to close or record a trade
- [ ] The suggestion record includes the operator's **disposition** (taken / modified / passed / expired) and, on a pass, the **optional reason** (structured set + note) — the signal the R-9 feedback loop and per-strategy breakdown consume

**R-9: Outcome resolution & feedback loop.** Journal entries resolve to actual results and the aggregate feeds future suggestions.
- [ ] Trades executed through the system resolve from native fills (no matching heuristics)
- [ ] Suggestions the trader did *not* take resolve by simulating the suggested parameters against recorded market data (fill assumptions documented and consistent)
- [ ] A suggestion whose **entry was never reached** within its validity resolves as **no-fill (a scratch)** — **not a loss**; it is excluded from win-rate / expectancy in the R-9 and per-strategy stats. **Trigger (fill) rate** — how often setups actually reach entry — is tracked as its own suggestion-quality metric
- [ ] Reports: expectancy, hit rate, expectation-vs-actual calibration, sliced by instrument, setup/signal type, time of day, and taken-vs-passed
- [ ] **Per-strategy performance breakdown** (a first-class surface, not just a query): per strategy/setup — sample size, hit rate, **expectancy**, avg **R**-multiple, **profit factor**, max drawdown, and **confidence calibration** — computed over **taken** trades *and* **suggested / passed** (via simulation), with the **taken-vs-suggested delta** exposing whether operator discretion adds or subtracts edge; sliceable by **pass reason** (R-4)
- [ ] **P&L by day** — a **journal** review surface: a **calendar** of realized daily P&L (green/red, magnitude-shaded) with an **equity curve** and daily stats (net, green vs. red days, best / worst, average), filterable by **practice/live** (R-14) and instrument, and optionally overlaid with the **suggested (shadow) P&L** for taken-vs-suggested review
- [ ] **Reward:risk vs. target** is a **central KPI** — each trade's actual R:R against the operator's **target R:R** (R-5), tracked overall and per strategy (is the R:R discipline holding?), shown alongside expectancy and win rate
- [ ] Journal statistics available to the suggestion engine and cited when they materially affect a suggestion
- [ ] Records soft-deleted / excluded from learning (R-15) are omitted from the training signal; reports can be viewed with and without excluded records

**R-10: Charting (the central UI surface).** Custom in-app charts are the primary, central surface: multi-resolution candlesticks with standard indicators, suggestion overlays (entry/stop/target zones), **live-position and working-order overlays, fill markers**, order-flow visualizations (delta, volume profile, **DOM / liquidity heatmap — Bookmap-style**), and event markers (news/social, alerts). TradingView (MCP/embed) supplemental.
- [ ] Suggestion and position overlays render live and persist historically for journal review
- [ ] Chart, alerts, chat, and the order/execution panel coexist in one workspace

**R-11: Order execution (human-in-the-loop).** The system places, modifies, and cancels orders via `MarqSpec.Client.ProjectX`, but only on an explicit user action. Two entry paths:
- **(a) Manual ticket:** the trader builds an order (instrument, side, size, order type, stop/target) and sends it.
- **(b) Take a suggestion:** clicking **Approve** *arms* the order ticket — **pre-filled and editable** — for review; **sending is a separate, explicit action**. Taking it *as-is* or with **adjustments** (a **modified** take; deviations recorded) is the same one path. A secondary, opt-in **"send as-is"** (e.g. the Approve split-button menu) skips the manual review but **not** the R-5 gate, R-16 caps, or R-12 re-validation. **Delivered** (gh#181): the send-as-is path (`POST /accounts/{id}/orders/send-as-is`) runs the *same* compose ladder + `OrderExecutionService.SendAsync` as every other path and transmits the gate's **approved** quantity; the journal records **how** each order entered (`EntryMethod` — manual / armed / modified / send-as-is / conditional), so a reviewed take, a one-action send, and a deviation are distinguishable. *(The default-entry-action preference is gh#218; the split-button UI is gh#25.)*
- [ ] Every entry is an explicit, deliberate user action — no order is ever auto-sent (auto-flatten in R-13 excepted)
- [ ] The **default entry action is a preference** — **`Approve & arm`** (review-first, the shipped default) or **`Send as-is`** (immediate) — as the split button's primary; making `Send as-is` the default is a deliberate opt-in (still gated by R-5 / R-16 / R-12; may be restricted to practice or require confirmation to enable). **Delivered** (gh#218): `DefaultEntryAction` persists on the risk profile (`ApproveAndArm` the fail-safe default); defaulting to `Send as-is` is **practice-only** (a live / undeclared account is refused 409, naming the mode) **and** confirm-to-enable (422 without the flag), enforced server-side at `PUT /accounts/{id}/risk`. The preference selects only which action is primary — it never touches the gate. *(The split-button UI is gh#25.)*
- [ ] All orders pass through the R-5 risk gate and the R-16 sanity caps before transmission
- [ ] Editing an armed order **re-evaluates risk live**; a material change from the suggested risk (wider stop, larger size, lower R:R) requires the operator to **acknowledge before Send**, and any R-5 layer / R-16 cap breach is **blocked or resized** — never silently sent
- [ ] The order ticket **seeds size from the configured per-trade risk** (risk tolerance, R-5) as the initial value, with **manual override** (the operator's usual mode); the live risk readout — size, $ risk to stop / safety, and **R:R vs. target** — updates either way
- [ ] Two **send modes** — **send now** (immediate; a resting limit/stop becomes a **working order** at the broker) or **send when conditions are met** (a **synthetic / local conditional order** the platform holds and fires on trigger — **not shown as a standing order** at the broker until it fires, keeping the entry off the book). The trigger (price crossing the entry, or a named signal) is explicit and editable
- [ ] **Cancel a working order** (gh#250): an operator can pull a single **resting working order** from the venue via the order API (`DELETE /orders/{id}`), distinct from discarding a **staged** ticket (server-side only). A cancel is **risk-reducing**, so it is allowed **even while the kill switch is engaged** and needs neither a risk profile nor a flat account; the order's now-orphaned **stop plan is retired** with it and the cancel is **audited** ([ADR-0007](adr/0007-order-execution-model.md)). A venue rejection (already filled / gone) never forces a wrong terminal status — the account-event stream (gh#219) reconciles the true one
- [ ] **Modify a working order** (gh#259): an operator can **reprice** a resting working order in place via the order API (`PATCH /orders/{id}/price`) — keeping its queue position and its attached protective bracket, rather than a cancel/replace. Unlike a cancel, a reprice **can add risk** (a wider stop, an entry likelier to fill), so it runs the **full** send ladder and is **re-gated at the unchanged size** (R-5 / R-16 / R-12) before the venue is touched — a breach is **refused, never transmitted** — and it is **refused while the kill switch is engaged**. The entry reprice holds **size and both stops invariant**, so bracket coverage stays exactly right; the new entry is **refused before the venue** if it would cross its own stops. **Re-staging the hidden working stop landed too** (gh#267): a hidden stop is a promotion target, not a venue order, so moving it is a **local** write (no venue call), safety-bounded (every hard limit is sized at the unchanged safety stop) — it re-gates only when it **widens** under `SizingBasis.ActualStop` (the one enforced layer sized at the working stop), re-validates the strict `safety → working → entry` ordering, **creates** a plan on a previously-coincident order, and **refuses** a promoted / orphaned / terminal plan. **Moving the entry and working stop together in one request landed** (gh#278): the entry venue path threads the new working stop through the same re-gate, re-validates the full chain before the venue, and commits both atomically. **Resizing a working order landed** (gh#292) — the last piece of the family: the same `PATCH` accepts a **`Size`**, re-gated at the **new** size and modified in place at the venue, transmitting the **gate-approved** quantity (a downsize the gate binds is **honoured, never silently exceeded**, and echoed to the client). The safety stop stays invariant, and the always-native bracket — which carries **no size of its own** and is sized to the realized fill on attach — protects the resized fill **by construction** (a strong structural inference gated on one practice-account verification before live, gh#293; only a 0-filled `Working` order is resizable, closing the sole stale-bracket-size window). The reprice / re-stage / resize is **audited** (`OrderModified`), and a venue rejection never forces a wrong terminal status — the account-event stream (gh#219) reconciles it ([ADR-0007](adr/0007-order-execution-model.md))
- [ ] A **pending conditional order carries a cancel-if / expiry**: it is **auto-cancelled** if price drifts a configurable amount **against** the entry before triggering, or the validity window / session deadline passes (or the thesis breaks) — so it can't fire on a stale setup or rest indefinitely. A cancelled pending order is a **scratch** (no fill), journaled as such (R-8)
- [ ] A conditional order **re-checks the R-5 gate, R-16 caps, and R-12 validity at trigger/fire time** (not only when armed). **Reliability tradeoff is explicit:** a synthetic order needs the platform live to fire, so **protective stops default to native (exchange-held) placement** — a platform/connection outage can't leave a position unprotected; hidden synthetic stops are an explicit opt-in (engineering §9)
- [ ] **Connection-loss → synthetic orphan handling:** the system watches the venue connection; on a drop, every **synthetic / in-app** order (hidden entries, un-promoted stops, brackets) moves to an **orphaned → emergency** state and the operator is **alerted immediately** — the always-native safety stop stays the physical floor. Recovery **re-validates and re-arms** (nothing silently resumes); the connection-loss event and each transition are audited with a **`synthetic_risk`** flag ([ADR-0007](adr/0007-order-execution-model.md), engineering §9)
- [ ] **Staged stop + always-on safety net** (how a *hidden* stop stays safe): the *actual* stop is held **synthetic/hidden while price is far** and **promoted to a native working order** once price comes **within a configurable proximity** of it (ticks / ATR / fraction of the entry→stop distance — **not** % of raw price); and an **always-native *safety stop*** sits **beyond** the actual stop as catastrophic insurance, so a live position is **never without a real exchange-held stop** — covering gaps, fast moves, and platform/connection outages, not just visibility
- [ ] **Protection comes down on exit (OCO-cancel-on-exit, gh#183):** when a position goes **flat** — by a manual flatten, the promoted actual stop firing, auto-flatten, or the kill switch's flatten-all — the system **retires the synthetic stop plan and cancels the dangling native legs** (the safety stop, a promoted actual stop, a stray take-profit), so no protective order is left **resting at the exchange with no position behind it** (which the next fill would turn into an unasked-for position). A **partial** fill never triggers this — the remaining position stays protected — and protection is **never** removed while any position in the instrument is still open. Idempotent, benign under venue races, and each retired leg is audited ([ADR-0007](adr/0007-order-execution-model.md))
- [ ] **Take-profit bracket** (the profit leg): an entry may carry an **optional take-profit target**, transmitted as the profit side of a **native exchange OCO bracket** alongside the protective stop, so a favourable move is taken without the operator watching and the exchange cancels the sibling on fill. The target must sit on the **winning** side of entry — above it for a long, below for a short (the mirror of safety-beyond-actual) — and a wrong-side target is **refused before the gate**, never flipped ([ADR-0007](adr/0007-order-execution-model.md), gh#170)
- [ ] Order state (working, partial, filled, cancelled, rejected) is reflected in the UI in real time and written to the journal
- [ ] A single **kill switch** instantly disables all outbound order capability and cancels working orders; its effect on **open positions is a user preference** (`kill-switch mode`, Settings) — **flatten-all** (default: market-flatten every position via the native-first sequence, **hold-to-confirm**) or **halt-only** (leave positions on their native safety stops). Either way it blocks new orders and can lock the session

**R-12: Execution-time re-validation.** When the trader takes a suggestion (R-11b), the system re-checks — at the moment of execution — that the suggestion is still valid: within its validity window, current price within a configurable tolerance of the suggested entry, and R-5 constraints still satisfied.
- [ ] A still-valid suggestion is sent on confirmation
- [ ] A stale or breached suggestion is blocked; the system shows *why* (expired, price drifted, risk changed) and requires a fresh decision — never silently transmits a stale ticket
- [ ] The validity/tolerance parameters are configurable
- [ ] **Rehydrated state re-validates, never auto-acts** (restart, gh#221): after a restart the decision surface returns **inert** — a staged order stays **staged** (not sent), a pending conditional stays **pending** (fires only on a genuine trigger, re-gated at fire), and a survivor suggestion is re-checked at **take**, never silently resumed. A startup pass ([ADR-0013](adr/0013-failure-recovery-model.md)) detects any **impossible cross-entity combination** a crash left and **fails safe to no-new-orders + loud** (kill switch, HaltOnly), never repairing

**R-13: Auto-flatten (safety-critical).** The system automatically flattens all open positions at a **configurable deadline set _per instrument_** — different products close/settle at different times (equity-index **ES / NQ** key off the **3:00 PM CT** cash-equity EOD; **crude CL and gold GC settle earlier** in the day), so each instrument carries its own deadline (per account), defaulting from its session close. The **equity-index default is ~2:30 PM CT** — ~30 min before the EOD, ahead of **market-on-close (MOC)** volatility (well before the **4:00 PM CT CME equity-index close / settlement**). It is the only order action the system performs without per-trade confirmation, and it only reduces/closes exposure. Because it fires **ahead of any venue-forced flatten** (e.g. Topstep ~3:10 PM CT) — and a **live brokerage has none** — it must be reliable **on its own** (redundancy; see [ADR-0013](adr/0013-failure-recovery-model.md) + [market sessions & settlement](wiki/pages/market-sessions-and-settlement.md)).
- [ ] Fires at a **configurable deadline per instrument** (GC / CL / ES / NQ; defaults from each instrument's session close — equity-index ~2:30 PM CT pre-MOC, crude/gold earlier); escalating warnings precede it
- [ ] After firing, the system **verifies positions are actually flat** and retries/escalates loudly if any position remains
- [ ] **Redundant / independent trigger** with a defined failure mode if the primary path is degraded at the deadline — it fires **without leaning on the venue's later forced-flatten backstop** (see [ADR-0013](adr/0013-failure-recovery-model.md), Q-3). Both in-process tiers shipped (`gh#185` primary, `gh#187` watchdog), and an **out-of-process observer** now covers the case they share — a host that dies before the deadline, which fires nothing and, until `gh#244`, alerted nothing: the **dead-man's switch** reports each market flat to a monitor on independent infrastructure, which **pages when the report fails to arrive** ([ADR-0019](adr/0019-alerting-channel-and-thresholds.md))
- [ ] Across the **CME settlement / maintenance window** (~4:00–5:00 PM CT), positions **reconcile from the venue as source of truth** (never local state); a position carried through settlement is reconciled to the **settlement re-mark**, never shown as a stale live price (ADR-0013). **Delivered** (gh#193): `MarketSession` derives the per-instrument settlement window from the session close, and `GET /accounts/{id}/positions` reports **venue-truth** positions tagged **live / settlement re-mark / declared-unknown** (a venue that can't be reached is declared unknown, never shown as a stale live view). *Deferred:* firing the reconcile on reconnect (restart rehydration is gh#221) and fill-level reconcile (gh#219)
- [ ] Auto-flatten is **on by default, per market** (best practice) and **cannot be silently disabled**; disabling a market is a **deliberate, clearly-warned** override (at the operator's own risk — on prop the venue's forced flatten still backstops, on a live account it does not)
- [ ] Every auto-flatten action is journaled

**R-14: Practice/live parity + mode safety.** Practice and live accounts use an identical data, journaling, and learning pipeline. They differ only in safety surface.
- [ ] An always-visible, unmistakable indicator shows current mode (practice vs. live)
- [ ] Switching to live is a deliberate, confirmed action; the UI makes accidental live trading hard
- [ ] All guardrails (R-5 risk gate, R-11 confirmation, R-13 auto-flatten, R-16 caps, kill switch) apply identically in both modes
- [ ] Journal/learning treat practice and live outcomes identically (no flag that changes how they're learned from)
- [ ] **Mode guard:** an `Order` / `Suggestion` cannot be persisted with a `mode` that conflicts with its parent `Account.mode` — enforced at the **repository layer and a DB check constraint** (not only the service layer), so a live order can never land on a practice account, or vice versa, even under a bug

**R-15: Record removal — soft delete (default) + hard delete.** The trader can remove a trade record from the learning signal and default views via two operations:
- **Soft delete (default):** a `deleted` toggle that excludes the record from all suggestion-engine training and default stats and hides it from default views, while retaining the full record and an audit trail. Reversible.
- **Hard delete (explicit):** permanently erases the record. A deliberate, confirmed action and the exception — not the default — since it removes the honest-recovery path.
- [ ] Soft delete is the default removal action and is reversible (un-toggling restores the record to learning and stats)
- [ ] The soft-delete audit trail preserves the original record and the exclusion (what, when, why-optional)
- [ ] Reports can be toggled to show figures inclusive vs. exclusive of soft-deleted records, so the honest picture stays recoverable
- [ ] **Training-exclusion and display-visibility are independent controls**, not one flag: the operator can **exclude a trade from AI training while keeping it visible** in the journal (a legitimate loss worth reviewing but not learning from), or hide a record from default views without touching training. Soft-delete is the combined shortcut (both, reversible); `training_excluded` and `hidden_from_user` are separately settable (data dictionary)
- [ ] Hard delete requires explicit confirmation; the fact that a deletion occurred is logged even though the content is gone
- [ ] Neither operation affects broker/account records — removal is local to this platform's journal and learning signal

**R-16: Execution sanity caps.** Independent of the risk model, hard caps that block obviously wrong tickets (max contracts per order, max notional, fat-finger price bounds) as a last line of defense before transmission.
- [ ] Caps apply to both manual and take-suggestion paths, in both practice and live
- [ ] A blocked order explains which cap it hit

**R-17: Broker/venue abstraction — target multiple trading APIs.** Market-data ingestion (R-1), account state (R-1/R-5), and order execution/flatten (R-11/R-13) are defined against **venue-neutral interfaces**, so the platform can target different trading APIs — prop firms and brokerages — without re-architecting. v1 implements one adapter, **ProjectX/TopstepX** (`MarqSpec.Client.ProjectX`); adding another venue (e.g., **Tradovate**) is a new adapter behind the same interfaces, not a rewrite. The interfaces **decompose into market-data / account / execution**: a full **trading venue** implements all three, while a **data-only provider** (e.g., **Finnhub** — equities/indices + alternative data, no execution) implements just the market-data slice — enabling **multi-asset context** (SPY/NASDAQ alongside ES/NQ) and "more than just futures data."
- [ ] A firm login has a **lifecycle**: its credential key can be **rotated** (the key *names* a server-side credential set — no secret is stored, ADR-0015), and it can be **deactivated**, which cascades to its accounts as a **soft delete** so the journal referencing them survives (gh#210)
- [ ] Broker-specific code is isolated to a per-venue adapter; the suggestion engine, risk model, execution gate, journal, and UI depend only on venue-neutral abstractions
- [ ] Instruments, accounts, orders, and fills are venue-tagged end-to-end, so risk, P&L, and the journal stay correct if accounts span venues
- [ ] **Platform / firm / login structure:** the abstraction targets a **platform** (the trading API — ProjectX/TopstepX, Tradovate); a **firm** (Topstep, Apex) provides accounts on a platform (Topstep *is* its platform; Apex runs on Tradovate). The operator has a **login per firm** (several firms share a platform — e.g. multiple Tradovate logins for Apex / Take Profit Trader / TradeDay / …), each exposing **many trading accounts** (practice + funded/eval); **risk (R-5) is per account** and the operator **selects the active account** (the **switcher lists only active + visible** accounts — `onlyActiveAccounts` + `canTrade` + `isVisible`; passed / failed / closed and operator-**hidden** accounts stay in the full roster, each toggled back individually)
- [ ] Venue capability differences are explicit (a venue may lack an order type or data stream); the system degrades gracefully when a capability is absent
- [ ] v1 ships the ProjectX adapter only; adding a second venue requires no change to the core, with per-venue credentials/endpoints/mode isolated server-side (engineering guide §8)
- [ ] A **data-only source** (market data and/or non-market data, no execution) implements only the market-data slice of the abstraction; the first is **Finnhub** (equities/indices for cross-asset context, plus **alternative data** landing via the R-2 non-market template)

**R-18: Authentication & authorization.** The Internet-exposed API — REST and real-time — requires **authentication** on every request/connection: the operator **signs in** and the client carries a **JWT**; anonymous access is refused. This exists because the deployment is reachable from the web, not because the app serves a user base — **one deployment, one operator** (ADR-0015). Every request is still **authorized against the authenticated identity** and data access **scoped to it** (R-20), through a **claims/policy layer** (RBAC-capable) so richer roles remain an incremental add rather than a rewrite.
- [ ] The operator's account is **seeded at first start** from configuration; there is **no open self-service sign-up**. A session issues the JWT the client carries
- [ ] Invitation-only onboarding is **not** the product's story — the operator is seeded, nobody is invited. The `Invitation` mechanism (entity, endpoints, migration) is **kept dormant in the codebase** (ADR-0017 §4), the plumbing a future read-only / mentee login would reuse
- [ ] Every REST endpoint and real-time connection requires a valid token; anonymous access is refused
- [ ] Tokens are verified server-side; no secret material lives in the client
- [ ] Authorization is claims/policy-based and **scopes every request to the authenticated identity** — a forgotten scope returns nothing, not everything; richer roles remain an incremental add
- [ ] Execution, kill switch, and account endpoints sit behind the same gate — no unauthenticated path to order actions (R-11/R-16)

**R-19: Installable client (PWA).** The React SPA is deliverable as an **installable Progressive Web App** — added to the home screen and launched **standalone** (its own icon/window, no browser chrome). **Android / Chromium is the primary target** (web-app manifest + service worker → an OS-level install); **iOS / Safari is best-effort** via **Add to Home Screen** (standalone launch), accepting Safari's PWA limits. The PWA is a **presentation client only**: all safety-critical enforcement (risk gate R-5, execution R-11, auto-flatten R-13, kill switch) stays **server-side**, so a closed, backgrounded, or OS-evicted client can never bust a guardrail.
- [ ] A **web-app manifest** (name, icons at the required sizes, theme/background color, `display: standalone`, `start_url`, orientation) + a registered **service worker** over **HTTPS** (R-18) make the app **installable on Android/Chromium** and launch it **standalone**
- [ ] **iOS/Safari best-effort:** installs via **Add to Home Screen**, launches standalone with a maintained apple-touch icon + status-bar styling; where iOS lacks a capability (install prompt, reliable background, persistent storage), the app **detects and communicates** the reduced capability rather than breaking
- [ ] The service worker caches the **app shell** for fast launch and a usable **disconnected shell** (a clear "reconnecting / data stale" state) — but **never presents stale market data or risk state as live**, and **no order action is possible while disconnected** (consistent with R-12 re-validation)
- [ ] **Safety is independent of client state:** installability changes only the presentation shell; the guardrails remain **server-enforced** whether or not the PWA is open — reinforced by the away-from-desk **push channel** (P1) for flatten warnings, which an installed PWA enables (Android; iOS 16.4+ once installed)
- [ ] **Coexists with multi-screen (ADR-0006):** desktop pop-out panels stay the desktop story; the installed PWA is the **single-window mobile / tablet** (and optional installed-desktop) experience — see [ADR-0010](adr/0010-progressive-web-app.md)

**R-20: Data isolation.** Every operator-owned row — prop-firm **connections & accounts**, **trading rules**, **risk profiles**, **suggestions & orders**, **journal**, **chat**, **AI-usage** — carries an owning identity, and every query is scoped to the authenticated one. Enforced **server-side at the data layer**, not just the UI. With **one operator per deployment** (ADR-0015) this is a **default-deny safety property** rather than a multi-tenant feature: a query that forgets its scope returns *nothing* instead of *everything*, and a second login on an instance stays possible later without reworking the data layer. **Reference & market data** (instruments, venues, data providers, bars/ticks/quotes, raw news) is **shared / global**. See [ADR-0017](adr/0017-single-operator-data-isolation.md).
- [ ] Every **operator-owned** entity (connection, account, risk profile, rule/trigger, suggestion, order, trade & journal, conversation, relevance config, soft-signal feedback, AI-usage, audit) carries an **owning identity** — the column and its default-deny filter are kept as a fail-closed property, not to separate users (there is one)
- [ ] Scoping is **enforced server-side at the data layer** (row-level owner filter / query scoping) — a UI bug or a crafted request that **forgets its scope returns nothing, not everything**; the same mechanism makes a future second login a config change rather than a data-layer migration
- [ ] **Rehydration preserves the owner** (restart, gh#221): the startup rehydration pass reads across owners (background plumbing bypasses the default-deny filter) yet **carries the owning identity on every row** — after a restart operator A can neither see nor act on operator B's state, and a row whose owner drifted from its parent's is itself flagged and fails safe ([ADR-0013](adr/0013-failure-recovery-model.md))
- [ ] **Broker / prop-firm credentials are held server-side** (R-18), one set per firm for the deployment’s single operator (ADR-0017) — never shared across people, which prop firms treat as a breach of terms
- [ ] **Shared vs. owned is explicit:** reference & market data are global; all decision / account / journal data carries an owning identity and is filtered at the data layer
- [ ] Removing the operator's account tombstones or deletes their data (delete or tombstone), with the audit trail retained per policy (R-15)

**R-21: Strategy templates ("playbooks").** A named, versioned bundle that **packages a whole methodology** — the indicators / features it needs, the setups (→ triggers, R-7 / ADR-0008), how a setup becomes a suggestion (entry / stop / target / size, R-4), and its risk defaults (R-5) — so a user can **install** a template and then **personalize** it, instead of hand-building every rule. It's how a methodology like a **13/48 EMA-crossover** or **ICT** is encoded once and reused. (Methodology *knowledge* lives in the design-time wiki; a template is its machine-runnable form.)
- [ ] A **StrategyTemplate** bundles: required **indicators / features** (R-1 / R-3), **setup definitions** that compile to **triggers** (R-7 / ADR-0008; mechanical-alert or agent-review), the **suggestion shape** (how entry / stop / target / size are derived, R-4), **risk defaults** (per-trade %, target R:R, session / kill-zone limits, R-5), and plain-language **rules** (R-7) for rationale + suppression
- [ ] **Install → instantiate → personalize:** installing a template **instantiates the operator’s own** copies of its strategies / rules / triggers / defaults (R-20) that are **fully editable** — the user tweaks parameters (e.g. 13/48 → 9/30), enables / disables individual setups, overrides risk; the instance **diverges** from the template but records its **template lineage + version**
- [ ] **Sources:** **platform-curated** (a starter library — e.g. VWAP-reclaim, 13/48, an ICT model) and **user-authored** (promote your own rules into a reusable, exportable template). **Sharing (backlog, [gh#3](https://github.com/adammarquette/trading-copilot/issues/3)):** a template **exports to a portable JSON artifact** — its rules, setups, triggers, defaults and the notes attached to them — that another operator **imports on their own deployment**; transport is a gist, an email, a repo, and the platform is not in the middle (ADR-0017). The export **excludes credentials, account identifiers, journal, positions, fills, P&L and AI-usage** (gh#3's full exclusion list, applied without exception), carries a **schema version** the importer must recognise or reject, and an **import arrives inactive**: its rules are enabled **as a group** by a deliberate action, because a downloaded file must never be able to take positions
- [ ] **Template updates are opt-in:** when a template version changes, an installed instance is **flagged for optional re-sync** (same pattern as a rule's `needs_revalidation`) — never silently overwriting personalizations
- [ ] **Measured + refined per user:** each template's setups feed the **per-strategy performance breakdown** (R-9) and the **AI refinement loop** (R-6, ADR-0009 backtests), so the co-pilot can propose personalizations grounded in *your* outcomes ("your 13/48 longs do better with a 2.5R target"), confirmed before they apply
- [ ] Methodologies like **ICT** (fair-value gaps, order blocks, liquidity sweeps, market-structure shifts) need **custom feature detectors**, not just standard indicators — a template **declares the features it needs**, and unavailable ones **degrade gracefully** (that setup is disabled with a reason)

### Nice-to-Have (P1)

- **Morning briefing:** pre-market per-instrument summary — overnight action, volume-profile context, scheduled economic events, news/social digest.
- **Alert & flatten notifications beyond the UI:** push/mobile channel for proactive alerts and — critically — flatten warnings and confirmations when away from the desk. *(Channel and thresholds decided: [ADR-0019](adr/0019-alerting-channel-and-thresholds.md) — Pushover, with the flatten **warning** as a P2 notify and the **confirmation** as a silent P3 push, so both reach the desk-less operator without training them to ignore the pager.)*
- **Economic calendar integration:** scheduled events (FOMC, CPI, NFP) as first-class signals and rulebook conditions.
- **Source credibility scoring:** track predictive value of individual soft-signal sources from journal outcomes; weight or flag accordingly.
- **Rulebook change analysis:** replay a proposed rule against journal history before persisting.
- **Bracket/OCO order support:** submit stop and target as linked orders at entry (subject to ProjectX capability — see Q-1).
- **Session replay:** step through a recorded session (tape, chart, suggestions, orders, fills) for review.
- **Multi-screen workspace:** detachable **pop-out panels** spread across monitors (chart on one, order-flow/DOM on another, journal/news/positions on a third), with **layout presets** per window and every window kept live and consistent. *Architecture-aware now:* each window is just another **SignalR** client, so the real-time backbone already makes this low-cost; execution stays single-authority server-side, and the kill switch + time-to-flat countdown replicate to every window ([ADR-0006](adr/0006-multi-screen-workspace.md)).

### Future Considerations (P2)

- **Staged entry autonomy:** per-trade auto-execution of high-confidence setups within tight bounds, then bounded autonomous entry. *Architectural insurance now:* the execution gate (R-5/R-12/R-16) is already the authoritative checkpoint; autonomy would attach behind it, not around it.
- **Additional trading venues, asset classes & data vendors behind the R-17 abstraction** — e.g., Tradovate and other prop firms/brokerages as new venue adapters, options monitoring (already on the MarqSpec roadmap), and additional data vendors. R-17 makes each an adapter, not a rewrite.
- **Backtesting & historical simulation (up to ~2 years).** Replay recorded market data through the **same engine as live** (indicators → triggers → suggestion → risk gate) to generate historical suggestions and simulate their outcomes — letting the **AI test-run and refine its suggestions, strategies, rulebook, and confidence calibration** against history (the historical arm of the R-9 loop; extends R-7 rule-replay and R-4). **Engine parity** (no divergent backtest logic), **look-ahead-safe**, and **reproducible** by replaying the clean-historical store ([ADR-0001](adr/0001-event-backbone.md)). **Cost-aware:** the deterministic parts (indicators / triggers / gate) replay cheaply, but **LLM-in-the-loop over 2 years is the cost driver** ([ADR-0008](adr/0008-ai-invocation-cost-model.md)) — bound it with cheaper models / sampling. **Data caveat:** ~2-yr **price/indicator** history is available (ProjectX bars, venue-depth permitting), but **news / soft-signal history is thin** (Tiingo free ≈ 3 months) — long backtests are largely price + indicator, light on news. Decisions (cheaper-model, news-light) + the parity / look-ahead / cost rationale: [ADR-0009](adr/0009-backtesting.md).
- **Multi-account support** (multiple funded accounts with independent rule and risk layers) — potentially spanning several venues (R-17).

## 7. Success Metrics

**Evaluation points:** 30 / 60 / 90 days after v1 is live in daily use.

**Safety (hard criteria — pass/fail, not trends):**
- **Zero busted flattens.** Auto-flatten closes all positions by the **configured deadline (default ~2:30 PM CT)** in 100% of sessions; any residual position past it is a Sev-1 defect.
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
1. *(Engineering)* **ProjectX order API capabilities.** Which order types are supported (market, limit, stop, bracket/OCO)? Are modify and cancel exposed? What is position-query latency? This gates both R-11 execution design and R-13 auto-flatten reliability. **API surface answered (gh#9):** market, limit, stop, stop-limit, and trailing-stop order types (plus venue-specific `JoinBid`/`JoinAsk`), OCO stop-loss and take-profit brackets attachable at placement, and **both modify and cancel** ([wiki](wiki/pages/projectx-gateway-api.md)). **Position-query latency stays open** — it can only be measured against a live practice account, and R-13 should not be designed around an assumed figure.
2. *(Engineering)* **ProjectX order-flow granularity.** Do the streams expose trade-by-trade data with aggressor side (or bid/ask context) sufficient for footprint reconstruction — or is aggregation/inference required?
3. *(Engineering — safety-critical)* **Auto-flatten guarantee & failure mode.** How is the flatten *guaranteed* to fire — redundant scheduler, independent watchdog? What happens if the cloud tier is unreachable or a flatten order is rejected at 2:59 PM? Does the local UI hold a fallback flatten path? Define the failure mode explicitly before any live trading. Consolidated in [ADR-0013](adr/0013-failure-recovery-model.md) (the gating safety-critical item).
4. *(Engineering)* **Practice vs. live account handling in ProjectX.** How does the API distinguish them (separate credentials/endpoints)? Can both be connected at once? **Answered, then narrowed (gh#60):** what the venues expose is **execution routing** — ProjectX has no sandbox host and marks each account with a required `simulated` flag; Tradovate splits it by **host**. That is adapter-side. But routing is **not** practice-vs-live in the R-14 sense: a prop-firm *funded* account reports `simulated: true` and executes on a simulated engine while a real payout rides on it. Against a real login, deriving mode from that flag classified **all 293 accounts as practice**, funded stages included. So the core's `TradingMode` is **declared by the operator per firm × stage** (`FirmConventions`), never derived from the venue, and an unclassified account is **`Undeclared` — tradeable nowhere** ([wiki](wiki/pages/projectx-gateway-api.md)). *(An earlier answer here said ProjectX had no mode field and that the name had to be parsed; that was wrong about the field's existence, corrected against the gateway's swagger — gh#9. The name does still carry size, stage and status.)* *How is an accidental live connection prevented?* **Settled (S1, gh#9):** `TradingModePolicy` refuses a live account outside production (R-14), enforced in code rather than by configuration convention.
5. *(Engineering)* **Untaken-suggestion simulation rules.** Fill assumptions (touch vs. trade-through, slippage, partial-target handling) so simulated R is consistent and honest.
6. *(Engineering)* **X/Twitter access method.** Official API tiers vs. scraping — cost, ToS exposure, reliability.

**Non-blocking (resolve during implementation):**
7. *(Engineering)* Execution-time validity tolerance (R-12): how far can price drift from a suggested entry before "stale"? Fixed ticks, ATR-relative, or per-setup?
8. *(Engineering)* YouTube transcript pipeline: channels, transcript source (captions vs. STT), latency tolerance.
9. *(Engineering)* Local UI packaging (hosted web app vs. desktop shell) — driven by charting choice and notification/kill-switch needs.
10. *(Engineering)* Cloud host and monthly cost ceiling for the always-on tier (Railway is a natural candidate).
11. *(Product)* Rulebook representation: structured DSL vs. natural-language rules interpreted at suggestion time.
12. ~~*(Product)* Alert taxonomy and noise budget (max alerts/hour; measuring alert fatigue).~~ **Answered
    2026-07-25 — [ADR-0019](adr/0019-alerting-channel-and-thresholds.md) (`gh#242`).** Taxonomy: **P1 page**
    (Pushover Emergency — repeats until acknowledged, bypasses Do Not Disturb, never quiet-hour suppressed),
    **P2 notify** (single push, suppressed outside 06:00–17:00 CT), **P3 quiet push** (delivered, no sound — the
    flatten *confirmation* P1 asks for), and dashboard-only for everything else. Budget: **≤ 3 pushes/hour under
    any single sustained fault, and 0 on a clean session.** Fatigue is *measured* — pushes per session by
    severity, on the dashboard — because a rule that pages on a healthy day is a defect in the rule, not noise to
    tolerate.
13. *(Legal/personal)* **Ingestion posture per source — ToS review before adding.** Originally scoped to news sites (R-2), but it applies to **every** ingested source, vendor documentation included: copyright is not the only constraint, and a site's Terms can restrict automated access or redistribution regardless of what fair use allows. Two sources already returned **403** to a direct fetch and were grounded by web search instead (Apex; Take Profit Trader's Zendesk) — a decision taken implicitly that should be taken deliberately. Posture is now recorded per page in the wiki's `Access:` header (gh#53); the one wholesale third-party document has been removed (gh#52).
14. *(Engineering)* Venue capability matrix (R-17): which trading APIs beyond ProjectX (Tradovate named), and how they differ in order types, order-flow granularity, and account model — defining what the venue-neutral interface must abstract. **Partly settled (S1, gh#9):** the interface and the capability model now exist in code — three slices (market-data / account / execution), venue-tagged identifiers, and explicit `VenueCapability` flags each adapter declares, with an unsupported capability failing loudly at the seam. What remains is populating the per-venue rows as each adapter lands.

## 9. Phasing

Live execution is deliberately gated: **auto-flatten and every guardrail must be proven on a practice account before live trading is enabled.**

**Phase 1 — Foundation (suggest-only):** ProjectX market + account ingestion, time-series storage, watchlist config, custom charts with indicators, on-demand suggestions via chat with layered risk sizing and session-clock awareness, automatic suggestion journaling at issuance. *Exit: daily use for on-demand analysis with every suggestion journaled.*

**Phase 2 — Execution on practice (safety first):** Order execution (manual ticket + take-suggestion) with the R-5 risk gate, R-12 execution-time re-validation, R-16 sanity caps, kill switch, native fill capture into the journal, R-13 auto-flatten, and R-14 practice/live mode plumbing — all exercised **on a practice account only**. *Exit (gate to live): auto-flatten fires reliably every session, no unconfirmed or un-gated orders, native fills captured cleanly, and the **in-place order modify's venue-side bracket behaviour is verified on practice** — that a modify **preserves** the attached protective bracket (gh#269) and that a **resize** leaves it sized to the realized fill (gh#293). Only then is live mode enabled — same code, flipped mode.*

**Phase 3 — Order flow & proactivity:** tape/footprint/volume-profile analytics and visualizations; continuous scanning with proactive alerts; untaken-suggestion simulation and first expectancy/calibration reports; exclude-from-learning (R-15). *Exit: alerts trusted enough to act on; journal producing weekly review material.*

**Phase 4 — Soft signals & learning loops:** news/RSS, scrapers, X, YouTube ingestion with relevance/sentiment; rulebook persistence from chat with rationale citation; journal statistics feeding the engine; TradingView supplemental. *Exit: suggestions demonstrably shaped by both rulebook and journal history.*

**Hard constraints throughout:** the flatten deadline and prop-rule risk layering are enforced from the moment execution exists (Phase 2) — never deferred, never optional.

---

> **Companion document:** engineering practices — the architecture patterns and the development-time knowledge wiki — live in the separate *Engineering Guide* (`trading-platform-engineering.md`), so this PRD stays focused on product requirements.

---

*Human-in-the-loop by design (v1): the trader initiates and confirms every entry; the system's only autonomous action is auto-flattening to reduce risk before the close. This is personal decision-support and execution tooling for its operator — it is not investment advice for third parties.*
