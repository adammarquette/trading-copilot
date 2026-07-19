# ADR-0007: Order execution & risk-gate model

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-4` (suggestions), `R-5` (layered risk model), `R-11` (execution), `R-12` (re-validation),
`R-13` (auto-flatten), `R-16` (sanity caps), `R-8`/`R-9` (journal / feedback), `R-17` (venue capability);
engineering §9 (safety-critical), §1; [ADR-0001](0001-event-backbone.md) (event log),
[ADR-0003](0003-authentication.md) (auth on execution endpoints). Wireframes: [`../design/`](../design/)
(arm/edit/send, send modes, stop protection, risk governor).

## Context
The co-pilot is **human-in-the-loop and safety-critical**: the LLM *proposes* trades, but a human approves and an
**enforcing gate — not prompt text — holds every risk limit** (engineering §1). Orders reach a real broker
(ProjectX v1, practice or live); a mishandled order or an unprotected position is real money. Across the design
pass the execution flow accumulated several interlocking decisions — how a suggestion becomes an order, how orders
are sent (immediately vs. on trigger, visible vs. hidden), how stops stay both hidden **and** reliable, and how risk
is enforced *before* and *at* transmission. This ADR consolidates them into one model so the **why** lives in one
place; the requirements (R-4/R-5/R-11/R-12/R-13/R-16) carry the **what**.

Constraints:
- **Enforcement below the model.** The risk/execution gate is the single authoritative checkpoint; the LLM never
  holds a limit.
- **No silent auto-send.** Every entry is an explicit human action (auto-flatten R-13 the sole exception).
- **Never unprotected.** A live position always has a real, exchange-held stop.
- **Trader-configurable**, single-operator, and low-latency enough for scalping.
- **Venue-neutral** (R-17): capabilities differ per venue; the model degrades / fills gracefully.

## Decision
- **One enforcing gate, every path.** Manual ticket, take-a-suggestion, send-as-is, a modified take, and a
  conditional order *firing* all funnel through the **same R-5 risk gate + R-16 sanity caps** before transmission
  (plus **R-12 re-validation** for anything derived from a suggestion). The gate computes size, shows the **binding
  layer**, and **blocks or resizes** a breach — it can return **"no trade."** Nothing the LLM emits reaches the
  broker unchecked.
- **Arm → (edit) → send, with a configurable fast path.** Taking a suggestion (R-11b) **arms** an editable,
  pre-filled ticket for review; **send is a separate, explicit action**. Approve is a **split button**: primary
  **`Approve & arm`** (review-first, shipped default) with an opt-in **`Send as-is`** in the menu — and **which is
  the default is a preference** (making `Send as-is` the default is a deliberate opt-in, possibly practice-only /
  confirm-to-enable). Send-as-is skips the *manual review*, never the gate. **Editing an armed order re-evaluates
  risk live**: a material-but-allowed increase requires **acknowledge-before-send**; a breach is blocked/resized. A
  take with edits is journaled as a **modified** take.
- **Two send modes — native vs. synthetic.** **Send now** transmits immediately (a resting limit/stop becomes a
  **native working order** at the broker — on the book, exchange-held). **Send when conditions are met** is a
  **synthetic / local conditional order** the platform holds and fires on trigger — **not visible as a standing
  order** until it fires, keeping entries off the book (no order anticipation / stop-hunting). A conditional order
  **re-checks the gate, caps, and validity at fire time**, not only when armed.
- **Staged stops + an always-native safety stop.** A synthetic order needs the platform live to fire, so protective
  stops don't rely on it alone: the **actual stop** is held synthetic/hidden while price is far and **promoted to a
  native working order** once price is within a **configurable proximity** (ticks / ATR / fraction of the entry→stop
  distance — *not* % of raw price); and an **always-native safety stop** rests **beyond** it as catastrophic
  insurance. A live position is therefore **never without a real exchange-held stop** (covering gaps, fast moves,
  and outages). The safety stop is placed at the **configurable max-drawdown-per-trade** — the per-trade hard cap
  made physical, so **max loss per trade is deterministic** regardless of sizing basis.
- **Layered, enforcing risk model (R-5).** Size is the **most-restrictive** of stacked layers — prop rules (daily
  loss, trailing drawdown), fixed %-risk per trade, and manual limits (max contracts, per-instrument caps,
  max-DD-per-trade). The **worst-case exit (safety stop) must fit the hard account limits**; the per-trade sizing
  basis (actual stop vs. safety stop) is **configurable**.
- **Two-tier daily risk control.** A **personal daily-drawdown governor** sits **inside** the hard prop daily limit.
  As its headroom depletes it **throttles / filters suggestions** (R-4) at *suggestion-time* — fewer, smaller,
  higher-conviction, then suppress — so risk is managed **proactively** before the execution gate blocks orders
  **reactively**.
- **Overriding controls.** The **kill switch** instantly disables outbound orders and cancels working orders, and —
  per a **user preference** (`kill-switch mode`) — **flattens all open positions by default** (the same
  **native-first flatten sequence** as auto-flatten, gated by a **hold-to-confirm**) or, in **halt-only** mode,
  leaves them on their native safety stops. **Auto-flatten (R-13)** remains the only order action **without**
  per-trade confirmation (reduce/close only, before the CME close) — the kill switch's flatten is *confirmed* by the
  hold. Both sit above the normal flow.
- **Connection-liveness monitoring + orphan handling.** The platform continuously watches the venue connection. On a
  **drop**, every **synthetic / in-app** order (conditional entries, un-promoted hidden stops, brackets) moves to an
  **orphaned → emergency** state, the operator is **alerted immediately**, and the **always-native safety stop**
  remains the physical floor. Recovery **re-validates and re-arms** — nothing silently resumes. The connection-loss
  event and each transition are journaled with a **`synthetic_risk`** flag (schema-aware audit) so the exposure is
  queryable after the fact. This is the **active** complement to the *passive* native-safety-stop mitigation below.
- **Order lifecycle (state machine).** `suggestion → armed → (edited) → sent {now | on-trigger} → working (native)
  or synthetic-pending → filled | cancelled | rejected | expired`, with the safety stop attached from fill and
  **OCO-cancelled on exit**; **kill-switch** and **auto-flatten** are transitions available from any live state.
  Every transition is journaled (R-8/R-9) on the append-only event log ([ADR-0001](0001-event-backbone.md)).

## Alternatives considered
- **Auto-execution (no human approve).** Rejected for v1 — the product is human-in-the-loop; bounded autonomy would
  attach **behind** this same gate later (PRD P2), not replace it.
- **All-native orders only.** Simple and reliable, but can't hide entries (an explicit operator need). Rejected as
  the *only* mode.
- **All-synthetic orders.** Hides everything, but a platform/connection outage would strand positions —
  unacceptable for protective stops. Rejected; hence the **native safety stop** + staged promotion.
- **Prompt / LLM-enforced limits.** Rejected outright — limits must be deterministic and **below** the model
  (engineering §1).
- **Single (hard) daily limit only.** You'd hit the wall reactively; the **soft governor** filters suggestions
  before that. Kept both.
- **Fixed sizing / defaults.** Rejected — traders differ; sizing basis, proximity metric, and default entry action
  are configurable.

## Consequences
**Positive**
- **One auditable checkpoint** for all order flow — easier to reason about, test, and trust; the LLM can't move
  money.
- **Hidden entries without unreliable stops** — the native safety stop + staged promotion give both.
- **Deterministic max loss per trade** (safety stop = max-DD-per-trade) and **proactive** daily-risk throttling.
- **Configurable to trader style**, and **venue-neutral** — the synthetic layer can *fill* a capability a venue
  lacks (R-17 optional-capability pattern).
- Clean separation from the ingest/analytics path — this is the **safety-critical path** (engineering §9).

**Negative / costs**
- The **conditional-order + staged-stop engine is real, safety-critical engineering** — it must be **highly
  available**, handle **gaps/latency** (a fast gap can jump the promotion band → the **safety stop catches it, by
  design**), and coordinate **OCO** cancellation. It carries the **high-rigor test suites** (engineering §5/§9).
- **More states to verify** (arm/edit/send × now/on-trigger × native/synthetic × modified) — a deliberate but real
  test burden.
- **Synthetic orders depend on platform liveness** — mitigated, not eliminated, by the native safety stop,
  auto-flatten, and **active connection-loss detection** (orphan → emergency + operator alert, `synthetic_risk` audit).
- **Gate / re-validation latency is on the hot path** — must stay low for scalping.
- A **config surface** (limits, defaults, proximity, governor) to design and validate-on-start.

## Follow-ups
- Define the **order-state machine** + per-transition **journal / event records** (R-8/R-9, ADR-0001).
- Spec the **synthetic / conditional engine**: trigger types, promotion-band metric + **default**, OCO
  coordination, gap/latency handling, and an availability target.
- Define **connection-loss detection** (heartbeat / timeout thresholds), the **orphan → emergency** transition +
  operator alert, and the **recovery re-arm** path — each carrying a `synthetic_risk` audit flag.
- Decide **defaults** (per environment): sizing basis, proximity metric, and the default entry action.
- Define the **risk-gate interface** — inputs (live account state, layers, safety stop), outputs (size, binding
  layer, block / resize / acknowledge) — R-5.
- Wire the **governor → R-4** throttle policy (thresholds, throttle modes).
- Confirm **ProjectX** native bracket / OCO / stop-type capabilities (Q-1); the synthetic layer covers gaps (R-17).
- Stand up the **high-rigor test suites** for the risk gate, execution, staged stops, kill switch, and auto-flatten
  (engineering §9).
