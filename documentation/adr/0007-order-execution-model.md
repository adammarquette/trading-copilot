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
  per-trade confirmation (reduce/close only, at the configured flatten deadline) — the kill switch's flatten is *confirmed* by the
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

## Update (2026-07-22) — per-trade risk basis confirmed; the send path composed
The flagged **per-trade risk % basis** is confirmed by the operator (gh#10): `PerTradeRiskFraction` is a fraction
of **headroom to the drawdown floor** — not account size — realistic values ~0.10–0.25. The gate sizes from it
as implemented. The send path is now composed at the API (gh#11 increment 1): declared risk rules are the gate's
fail-closed input, every sized attempt persists its `GateDecision`, and placed orders journal under the R-14 DB
mode guard. Increment-1 sends require a **flat** account (venue P&L is not reported; flat makes unrealized = 0 a
fact); the floor starts from the declared starting balance (high-water tracking deferred). **Increment 2**
adds **arm → edit → take** (`OrderStatus.Staged`): `OrderExecutionService.Evaluate` runs the entire ladder from
the same code as `SendAsync` but stops short of transmission, so an armed ticket is judged exactly as a sent
one; the proposal persists whole on the order row so **take re-validates everything against fresh venue truth
(R-12)** — a ticket that passed at arm and fails the fresh gate stays staged, having transmitted nothing.
Every arm/edit/take leaves its own `GateDecision`. A live *successful* take is a real placement and stays
deferred to the operator's explicit go (PRAC only). **Increment 3** wires the **always-native safety stop**:
every transmitted entry carries its safety stop as an **exchange-held stop-loss bracket** (`OrderRequest.ProtectiveStop`
→ the ProjectX `stopLossBracket`), so the venue attaches a real protective stop **on fill** — a live position is
never unprotected. It is **fail-closed**: if the venue cannot hold a native protective stop
(`VenueCapability.BracketOrders`), the entry is **not sent** (`ExecutionOutcome.RefusedByUnprotectableStop`) —
better no trade than an unprotected one. *Still deferred to later increments:* the **staged/synthetic actual
stop** and its **proximity promotion**, take-profit brackets, OCO-cancel-on-exit, and the connection-liveness
orphan handling — increment 3 lands the catastrophic-insurance floor, not yet the hidden-then-promoted working
stop.

## Update (2026-07-24) — the staged-stop plan (increment 4)
The **hidden actual stop** now has its model and its persistence: `Domain/Execution/StopPlan` holds entry, the
working stop, the safety stop beyond it, and the promotion band, starting at `StopStaging.Hidden`;
`ShouldPromote(price)` is the deterministic decision a watcher will consult, and `Promote()` is one-way and
idempotent so a retrying watcher cannot re-transmit. The band is **ticks** or a **fraction of the entry→stop
distance** — the ADR's "not % of raw price" made structural — and **ATR is refused outright** (`NotSupportedException`)
until the indicator pipeline (R-3) can measure it, rather than silently mis-measuring.

The invariant the type exists to hold is **safety-beyond-actual**: the catastrophic floor must rest *further*
from entry than the working stop, or it fires first and the deterministic worst case is neither. It is enforced
in `StopPlan.Create` **and** by a side-dependent cross-column DB CHECK (`CK_StopPlans_SafetyBeyondActual`) —
proven rejecting against live Postgres. A plan is recorded per transmitted entry, and skipped when the two stops
coincide (nothing to stage — the single stop already rests natively).

## Update (2026-07-24) — the promotion watcher landed (gh#153)
With R-1 quotes flowing, `StopPromotionHost` is the event log's **first consumer**: it reads `market.quote`
events from its own cursor (an ADR-0001 consumer group) and, per hidden stop whose side-appropriate price (bid
for a long, ask for a short) is within its band, transmits the actual stop as a native working order and records
it `Native`. **Transmit-then-record**: a venue rejection propagates and the plan stays `Hidden`, never claiming
an exchange-held stop that does not exist. Because promotion is idempotent (an already-`Native` stop is skipped),
the consumer commits its cursor per batch and at-least-once redelivery on restart is safe. The watcher reads
across the R-20 boundary deliberately — it is background plumbing acting for the deployment, not a request-user.

## Update (2026-07-24) — the take-profit bracket (gh#170)
The **third leg** now lands: `OrderProposal.Target` / `OrderRequest.ProfitTarget` carry an optional take-profit,
and `OrderExecutionService` transmits it as the profit side of a **native OCO bracket** alongside the
always-native protective stop (ProjectX `takeProfitBracket` — a limit-type bracket at the target). It holds the
**mirror of safety-beyond-actual**: a take-profit must sit on the **winning** side of entry — above it for a
long, below it for a short — and a wrong-side target is refused **before the gate**
(`ExecutionOutcome.RefusedByInvalidTarget`), for arm and send alike, rather than dropped or flipped into
something the operator did not ask for. A `null` target stays valid: the entry rests with its protective stop
alone (the two-leg bracket unchanged). Because the bracket is native, the venue gives **exchange-managed OCO**
for free — target and stop cancel one another on fill.

*Still deferred:* the **app-level OCO-cancel-on-exit** — cancelling a *synthetic/hidden* leg, or the safety
stop, when a position exits by manual flatten or by the promoted actual stop (it needs account-streaming / fill
events, `VenueCapability.AccountStreaming`). *(Connection-liveness orphan handling — listed deferred here — has since landed: gh#209, gh#191.)*

## Update (2026-07-25) — the take-profit wiring (gh#173)
The operator can now set one: `SendOrderRequest.Target` flows through `BuildRequestAsync` into
`OrderProposal.Target` on **send / arm / edit**, and the `Order` row persists it as `TakeProfitPrice` so
**arm → take** re-builds and re-transmits the target the operator armed rather than dropping it — the same
round-trip the working stop gets (gh#134). `StagedOrderResponse` surfaces the staged target for review/edit.
Defence-in-depth below the domain guard: a side-dependent DB CHECK `CK_Orders_TakeProfit_WinningSide` (the
mirror of `CK_StopPlans_SafetyBeyondActual`) refuses a persisted wrong-side target — **proven rejecting against
live Postgres** for both sides, `NULL` (no profit leg) passing.

## Update (2026-07-25) — the conditional order, model + persistence (gh#176)
The **second send mode** begins — "send *when conditions met*". `Domain/Execution/ConditionalOrder` is a
synthetic entry the platform holds and fires when its `ConditionalTrigger` crosses: a trigger price plus a
**cross direction** (`RisesTo` = fire at/above; `FallsTo` = fire at/below — covers breakout and pullback entries
venue-neutrally). It self-cancels on an **adverse drift** past a **cancel band** on the *stale side* of the
trigger, or when its **validity window** passes. The decisions — `ShouldFire(price)`, `ShouldCancel(price, now)`
— are deterministic and **Pending-only**, and `Fire()`/`Cancel()`/`Expire()` are one-way + idempotent, exactly
the `StopPlan` discipline, so a retrying watcher never re-fires a resolved order. `now` is passed in, never read
from a clock, so the decision is pure. Distances are price units — **never % of raw price** (the ADR rule).

Persisted as `ConditionalOrderRecord` (table `ConditionalOrders`), keeping the **proposal whole** (as the order
row does) so the entry is re-built and **re-gated at fire time (R-12/R-5/R-16)** — creation transmits nothing.
Created via `POST /accounts/{id}/orders/conditional`: the same compose ladder + `Evaluate` as arm gives the
operator immediate feedback, but the order rests **`Pending`**, unseen at the broker. Side-dependent DB CHECKs
mirror the domain (direction declared, cancel band on the stale side) — **proven rejecting against live
Postgres**.

*Still deferred:* the **firing watcher** (next); **connection-loss orphan handling** (a pending synthetic order
→ orphaned → emergency; overlaps S4); and **named-signal triggers** (they need the R-3 signal pipeline —
price-cross only for now). This lands the model half of the "spec the synthetic/conditional engine" item below.

## Update (2026-07-25) — the firing watcher landed (gh#198)
The conditional order is now **operational**. `ConditionalOrderHost` is the event log's **second consumer**
(its own `conditional-order` cursor, ADR-0001), the hardened per-pass-scope shape of the stop-promotion host
(gh#169). On each `market.quote`, `ConditionalFiringService` **cancels/expires** the stale pending orders
(drift past the band → `Cancelled`, validity window passed → `Expired`) and **fires** the triggered ones. Firing
is a new entry, so — unlike the stop-promotion watcher's bare transmit — it runs the **authoritative fire-time
re-gate (R-12 / R-5 / R-16)** through the *same* `OrderExecutionService` and compose ladder the operator's take
runs: a placed order journals its `Order` + `StopPlan` (so the stop-promotion watcher then protects it) and the
conditional records `Fired` + its `FiredOrderId`; a **gate-refused** fire stays `Pending` and re-decides on the
next quote (nothing lost). It is idempotent (a resolved order never re-fires).

To reuse the compose/gate/journal without duplicating a safety guard (the gh#148 drift lesson), the watcher —
which has no request user — **discovers** pending orders with `IgnoreQueryFilters`, then does each owner's work
in a DbContext **scoped to that owner**, so `ComposeAsync` stays R-20-correct unchanged.

*Still deferred:* **named-signal triggers** (they need the R-3 signal pipeline — price-cross only for now).

## Update (2026-07-25) — connection-loss orphan handling landed (gh#209)
The at-risk safety net (ADR-0013). A new R-17 seam `IVenueConnection` (a **process-wide singleton**, one
credential set per process, ADR-0015) surfaces venue liveness — the ProjectX adapter reads its **market-hub**
state (the hub whose drop stops quotes flowing, so a hidden stop can no longer promote). `VenueConnectionMonitorHost`
polls it, and on a **drop** the `OrphanGuardService` marks every `Hidden` working stop **`StopStaging.Orphaned`**
(a new staging; no migration — the `Staging` column is `int` and the `staging <> 0` CHECK already accepts it, and
the promotion watcher already acts only on `Hidden`); on **reconnect** it **re-arms** them to `Hidden`. The
**native safety stop stays the physical floor** throughout — this degrades only the operator's *tighter*
synthetic protection, loudly. A **pending conditional is no-risk** and needs no orphaning: it simply does not
fire without quotes, and its cancel-if/expiry stands (ADR-0013). The guard runs as background plumbing
(`IgnoreQueryFilters`, ownership preserved).

*Deferred within orphan handling:* the **formal `AuditRecord` + `synthetic_risk` flag** (gh#220) and a
**real-time operator alert** (gh#222) — the orphan is a **high-severity log** carrying `synthetic_risk` until the
Phase-4 SPA/SignalR channel and the audit entity land.

## Update (2026-07-25) — per-position re-validation on re-arm landed (gh#191)
Re-arm is no longer unconditional. On reconnect the `OrphanGuardService` re-validates **each** orphaned stop
against **venue truth** before re-arming: a plan whose position is still open re-arms to `Hidden`; one whose
position **closed or reversed** during the outage is **`StopStaging.Retired`** (terminal — never promoted, never
re-armed), because a stop for a position that no longer exists must not act (ADR-0013's "never auto-act on
rehydrated state — re-validate first"); a **partial close** reconciles the protected quantity (`Order.Size`)
first; and a plan that **cannot be re-validated** — the venue is unreachable, or it belongs to another process's
credential set — stays orphaned and is **retried on a later pass** (the monitor keeps retrying while connected).
Uncertainty resolves to the safe state: nothing re-arms on an unverified assumption (§9).

## Update (2026-07-25) — the kill switch landed (gh#189)
The **kill switch** is now the operator's process-wide override, enforced at a **single choke point**: the
enforcing send path (`OrderExecutionService.SendAsync`) reads an `IKillSwitch` and, while engaged, refuses
**every** transmission — manual send, take, or a conditional firing — as `RefusedByKillSwitch`, before the order is
even sized. Reducing / protective actions (auto-flatten's close, stop promotion) do **not** pass through this path,
so a killed system can still be flattened and stays protected.

Engaging (`POST /kill-switch`, **hold-to-confirm** required for **both** modes) disables outbound **first** (a
thread-safe runtime flag backed by a durable `KillSwitchState` row), then **cancels working orders** — the first
`CancelOrderAsync` path; working `Order` rows are resting entries, so the protective brackets a halt leaves
standing are untouched — then per `kill-switch mode` either **flattens all** open positions (the native-first
close/verify, no deadline) or **halts only**. The lock **persists across a restart** — rehydrated into the runtime
flag at startup — so a crash or redeploy never silently re-enables trading (ADR-0013). Every transition is
journaled (`killswitch.engaged` / `killswitch.disengaged`).

*Still deferred:* the per-account **`RiskProfile.KillSwitchMode`** preference (the mode rides the engage request
for now; the persisted Settings preference lands with the UI, gh#25); an **opposing-market-order** fallback close
if the venue's own close keeps rejecting; and **voiding pending conditionals** on a kill (the send guard already
stops them firing, so it is cleanliness, not safety).

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

## Update (2026-07-20) — the risk-gate interface is defined (S2, gh#10)

The follow-up *"define the risk-gate interface — inputs, outputs"* below is closed. It lives in
`MarqSpec.TradingCopilot.Domain/Risk/`:

- **In:** `OrderProposal` (instrument spec, side, requested size, entry, working stop, **safety stop**, reference
  price) plus `RiskContext` (live `AccountRiskState`, the account's `TrailingDrawdown`, its hard
  `AccountRiskRules`, and the operator's `RiskProfile`, `ManualCaps`, `SanityCaps`).
- **Out:** `GateDecision` — outcome (allowed / resized / **blocked**), the approved quantity, the **binding
  `RiskLayer`**, and a reason string that is always populated.
- **How:** each layer is sized independently and the **most restrictive wins**. The hard account limits (drawdown
  floor, daily loss limit, governor) are all measured at the **safety stop**, so it is the catastrophic case —
  not the expected one — that can never breach the account.

Two decisions worth recording:

- **Per-trade risk % is a fraction of _headroom to the floor_, not of account size.** R-5 is explicit that "the
  risk budget is headroom to the (trailing) drawdown floor — not the account size," so the percentage is applied
  there. This gives sizing a useful property — it tightens by itself as the floor is approached — but it also
  means realistic values are **~10–25%**, not the ~1% of a traditional account-size rule. *Flagged for operator
  confirmation.*
- **The `acknowledge` outcome is deferred.** This ADR lists "block / resize / acknowledge" as gate outputs, but
  acknowledgement is about *editing an armed order* relative to an already-approved baseline — state that belongs
  to the execution flow (S3), not to a stateless evaluation. `GateOutcome` therefore ships with three values.

Not in this slice: **R-12 re-validation** (validity window, price-drift tolerance) rides with the
take-a-suggestion path in S3, and the **consistency %** rule needs P&L-by-day history from the journal (R-9).

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
