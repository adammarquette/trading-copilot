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

## Decision log

This ADR's decision has been **extended by increment** rather than rewritten, so the dated updates below are the
authoritative record of what the execution path does today — the *Decision* above is the original frame, and where a
later update supersedes part of it that update says so inline. They run **oldest first**. Nothing here is deleted when
it is superseded: an ADR is the reasoning trail, and knowing *why* a decision was replaced is the point.

This index exists because the trail is long. Skim it to find the increment you need, then read that entry.

*(Housekeeping, gh#492: the entries below were **reordered into date order** and this index added — previously
`Consequences` sat in the middle of the trail and the earliest entry (2026-07-20) sat near the end, so a reader could
not tell current from historical. **No entry was removed or reworded**; the only edits were to two "still deferred"
lists whose items have since landed, which now say so and name the update that closed each.)*

*(Housekeeping, gh#580: the trail re-drifted — five later updates (gh#530 / #529 / #532 / #531 / #577) had
accumulated **after** `## Follow-ups`, so a reader again met a "final" list that was not. `## Follow-ups` is moved
back to the end and the dated trail is contiguous once more. **No entry was changed** — a pure re-ordering; the
index above already listed all thirty-one entries in date order, and still does. That it recurred is the argument
for validating heading-order/index against the trail in CI rather than by hand — filed as gh#600.)*

| Date | Update |
|---|---|
| 2026-07-20 | the risk-gate interface is defined (S2, gh#10) |
| 2026-07-22 | per-trade risk basis confirmed; the send path composed |
| 2026-07-24 | the staged-stop plan (increment 4) |
| 2026-07-24 | the promotion watcher landed (gh#153) |
| 2026-07-24 | the take-profit bracket (gh#170) |
| 2026-07-25 | the take-profit wiring (gh#173) |
| 2026-07-25 | the conditional order, model + persistence (gh#176) |
| 2026-07-25 | the firing watcher landed (gh#198) |
| 2026-07-25 | connection-loss orphan handling landed (gh#209) |
| 2026-07-25 | per-position re-validation on re-arm landed (gh#191) |
| 2026-07-25 | the kill switch landed (gh#189) |
| 2026-07-25 | the account-event streaming seam (gh#219) |
| 2026-07-25 | app-level OCO-cancel-on-exit (gh#183) |
| 2026-07-25 | the AuditRecord landed (gh#220) |
| 2026-07-25 | the send-as-is fast path (gh#181) |
| 2026-07-25 | cancel a working order via the order API (gh#250) |
| 2026-07-25 | modify a working order via the order API (gh#259) |
| 2026-07-25 | the default entry action preference (gh#218) |
| 2026-07-25 | the promotion race fenced (gh#183 follow-up) |
| 2026-07-26 | reprice a working order's working stop (gh#267) |
| 2026-07-26 | move the entry and working stop together (gh#278) |
| 2026-07-26 | resize a working order (gh#292) |
| 2026-07-26 | the promoted stop is sized to the live remaining (gh#277) |
| 2026-07-27 | the ATR band is live, and the caller resolves it (gh#311) |
| 2026-07-28 | resting orders are readable through the app (gh#381) |
| 2026-07-28 | the consistency target binds, and its posture is per-account (gh#380) |
| 2026-07-30 | the take path is claimed before it reaches the venue (gh#530) |
| 2026-07-30 | the kill switch survives a venue that says no (gh#529) |
| 2026-08-02 | conditional firing commits per record (gh#532) |
| 2026-08-02 | the direct-send path serializes per account against send-vs-send stacking (gh#531) |
| 2026-08-02 | the transmit→journal window closes with a durable pre-transmit intent (gh#577) |

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
better no trade than an unprotected one. *Deferred at the time to later increments:* the **staged/synthetic actual
stop** and its **proximity promotion**, take-profit brackets, OCO-cancel-on-exit, and the connection-liveness
orphan handling — increment 3 lands the catastrophic-insurance floor, not yet the hidden-then-promoted working
stop. *(**All four have since landed**, each with its own update below: the staged stop + promotion — increment 4
and gh#153; take-profit brackets — gh#170 / gh#173; OCO-cancel-on-exit — gh#183; orphan handling — gh#209 / gh#191.
Kept as written because the increment ordering is the reasoning trail.)*

## Update (2026-07-24) — the staged-stop plan (increment 4)
The **hidden actual stop** now has its model and its persistence: `Domain/Execution/StopPlan` holds entry, the
working stop, the safety stop beyond it, and the promotion band, starting at `StopStaging.Hidden`;
`ShouldPromote(price)` is the deterministic decision a watcher will consult, and `Promote()` is one-way and
idempotent so a retrying watcher cannot re-transmit. The band is **ticks** or a **fraction of the entry→stop
distance** — the ADR's "not % of raw price" made structural — and **ATR is refused outright** (`NotSupportedException`)
until the indicator pipeline (R-22) can measure it, rather than silently mis-measuring.
*(That refusal has since been lifted: the pipeline landed gh#310 and the band moved to the caller, gh#311 — see the
update below. `ShouldPromote` now takes a resolved distance rather than deriving one.)*

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

*Then deferred, since landed:* the **app-level OCO-cancel-on-exit** — cancelling a *synthetic/hidden* leg, or the
safety stop, when a position exits by manual flatten or by the promoted actual stop — needed the account-streaming
/ fill-event prerequisite `VenueCapability.AccountStreaming` (**gh#219**) and **landed gh#183** (see the Updates
below). *(Connection-liveness orphan handling, once listed deferred here, has also landed: gh#209, gh#191.)*

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

*Deferred at the time:* the **firing watcher** (next) *(landed gh#198, immediately below)*; **connection-loss orphan
handling** (a pending synthetic order → orphaned → emergency; overlaps S4) *(landed gh#209 / gh#191)*; and
**named-signal triggers** (they need the derived-signal pipeline — order-flow (R-3) and/or the indicator pipeline
(R-22) — price-cross only for now) — **still open**, the one item of the three that has not landed. This lands the
model half of the "spec the synthetic/conditional engine" item below.

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

*Still deferred:* **named-signal triggers** (they need the derived-signal pipeline — order-flow (R-3) and/or the indicator pipeline (R-22) — price-cross only for now).

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

*Deferred within orphan handling:* the **real-time operator alert** (gh#222) — the orphan is a **high-severity
log** carrying `synthetic_risk` until the Phase-4 SPA/SignalR channel lands. *(The formal `AuditRecord` +
`synthetic_risk` flag landed in gh#220 — see the update below.)*

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

## Update (2026-07-25) — the account-event streaming seam (gh#219)
The platform is no longer **blind after transmission**. `VenueCapability.AccountStreaming` was declared but reached
nothing — no `Fill` row was ever written and an order stopped at `Working`, never reaching `Filled` /
`PartiallyFilled` / `Rejected`. The seam now closes that gap end to end:

- **A venue-neutral seam** — `Domain/Venue/IAccountEventStream` carrying `OrderStateEvent` / `FillEvent` /
  `PositionEvent`. Like `IVenueConnection` (gh#209), the user hub is **process-wide** (one credential set, ADR-0015),
  so it is a **singleton** off the scoped `ITradingVenue`. `ProjectXAccountEventStream` implements it over the
  gateway user hub, translating the vendor payloads at the adapter boundary (no vendor type crosses into the core),
  and `ProjectXVenue` now **advertises** `AccountStreaming`.
- **A consumer that persists venue truth** — `AccountEventIngestionService` writes a `Fill` (the entity's first
  producer) and advances the order: `Filled` / `PartiallyFilled` from the persisted fill total, `Rejected` /
  `Cancelled` from order-state events (a rejected working order never stays `Working`, R-11). It discovers the owning
  account across owners (background bypasses the R-20 filter) then writes in a DbContext **scoped to that owner**, so
  an event for another operator never crosses the R-20 boundary, and an unknown / foreign order is logged and
  ignored, never fatal. **Idempotency is by construction** — the `{ order, venueFillKey }` unique index rejects a
  replayed trade id (proven against live Postgres), not an inspecting `SELECT`.
- **A supervised host** — `AccountEventStreamHost` runs the subscription with the drop-vs-stop reconnect discipline
  of the quote stream (a drop re-subscribes after a delay; cancellation is a clean stop) and the fresh-scope-per-event
  teardown shape; the seam and venue resolve **lazily** inside the run (eager injection needs credentials a test host
  lacks, the gh#212 lesson), and the capability is `Require`d **at the call** (R-17) — a venue that cannot stream is
  refused, not discovered mid-stream.

*Still deferred:* backfilling fills missed while disconnected (venue-truth reconcile, gh#193). `MarketDepth` /
`TrailingStops` stay declared-but-unreached. **(OCO-cancel-on-exit — the seam's first real consumer — landed
gh#183; cancelling a working order landed gh#250; modifying one — reaching `ModifyOrder` — landed gh#259; see the
Updates below.)**

## Update (2026-07-25) — app-level OCO-cancel-on-exit (gh#183)
The **last deferred piece of the execution model** is done. When a position exits, whatever protection was
standing for it now comes down; before this, a synthetic/hidden actual-stop and the always-native **safety stop**
both survived the position they protected — and a dangling safety stop is not merely untidy, it is a **live
resting order at the exchange with no position behind it**, which on the next fill opens a position the operator
never asked for.

- **The trigger is the account-event seam** (gh#219): a `PositionEvent` reporting the contract **flat**
  (`NetQuantity == 0`). So **every** exit route reaches it the same way — a manual flatten, the promoted actual
  stop firing (the safety stop is the leg left behind), auto-flatten, and the kill switch's flatten-all — because
  each ends in a venue position update. The **catastrophic** case (cancelling protection while the position is
  still open) is structurally impossible: a **partial** fill leaves the net non-zero and is a no-op, and a flat
  contract has by definition no open position to leave unprotected (R-11).
- **`Api/Accounts/OcoExitService`** retires the **synthetic** stop plans (a `Hidden` / `Native` / `Orphaned`
  record → `Retired`, terminal, so the promotion watcher never re-arms it) and cancels the **native** legs resting
  at the venue — the safety bracket, a promoted actual stop, a dangling take-profit. The legs are found by the
  pure `Domain/Execution/OcoExitSelection`: a venue-resting order on the flat contract that is **not** one of the
  operator's journaled entries. That distinction is **by construction, not a heuristic** — every protective leg
  the venue spawns is unjournaled, while every operator entry is journaled — so a resting entry is never mistaken
  for dangling protection. The venue's working orders are read through a new fail-closed `IOrderExecutor.GetWorkingOrdersAsync` (R-17).
- **Idempotent, benign under races, R-20-scoped.** A replayed exit finds the plans already `Retired` and the legs
  already gone; a cancel the venue rejects (already filled, or the venue's own OCO won the race) is logged and
  never retried (no storm); the account is resolved to its owner and every read/write/cancel runs in that owner's
  context, so an exit never crosses the R-20 boundary. Each retired plan is **audited** — an immutable `AuditRecord`
  (gh#220, `AuditAction.PositionExit`), a secondary write that never fails the retire; not `synthetic_risk` (the
  position is flat, so no live exposure rested on platform-held protection, unlike the orphan guard's records).

*Still deferred:* venue-native OCO linkage (letting the broker pair the legs) — not applicable, since a synthetic
leg is never on the book for the venue to pair, which is exactly why the cancel is app-level.

## Update (2026-07-25) — the AuditRecord landed (gh#220)
The `synthetic_risk` audit deferred by gh#209 is now a real entity. `AuditRecord` (table `AuditRecords`) is an
**operator-owned** (`IUserOwned`, R-20), **append-only** row carrying `action` (`connection-loss` this increment),
`placement` (`native` / `synthetic`), the **`synthetic_risk`** flag, `before → after`, a **soft** `stopPlanId`
reference (no FK, so the immutable trail outlives the stop), and a timestamp — both enums fail-closed-zero, DB-checked
`≠ unknown`. The `OrphanGuardService` writes one row per transition it already performs: `Hidden → Orphaned` on a
drop, and `Orphaned → Hidden` (re-arm) or `Orphaned → Retired` on reconnect — each `synthetic_risk = true` and owned
by the affected stop's operator, so the exposure window is reconstructable from the table alone. A stop **left
orphaned** (unverifiable) transitions to nothing and records nothing.

The audit is a **secondary write, subordinate to the safety action**: the guard commits the staging change *first*,
then writes the audit in its own unit of work through an `IAuditLog` seam; a failure there is **logged and
swallowed, never propagated** — a covering test makes the audit write throw and asserts the orphan / re-arm still
completes. This is the general rule for the coming order / guardrail / kill / flatten write sites too: the record of
a safety action must never be able to *prevent* it. *Still deferred within the audit:* those broader write sites, and
the **real-time operator alert** (gh#222) — the high-severity log remains the interim alert.

## Update (2026-07-25) — the send-as-is fast path (gh#181)
The opt-in **`Send as-is`** from R-11(b) — the Approve split-button's menu item — now exists as
`POST /accounts/{id}/orders/send-as-is`. An operator who has already decided collapses **arm → take** into one
action; it **skips the manual review, never the gate**. The handler shares the *same* `ComposeAsync` ladder and the
*same* `OrderExecutionService.SendAsync` as the direct send (a single extracted `TransmitAsync` tail — no second
copy of the guard ladder, the gh#148 drift lesson), so the kill switch, R-14 mode × environment, the mismatch and
order-type refusals, the flat-account and credential-key (ADR-0015) preconditions, the R-5 gate and R-16 caps all
apply unchanged, and the transmitted quantity is the gate's **approved** quantity — never the requested one. A
refused send-as-is journals no order row; a *sized* attempt always leaves its `GateDecisionRecord`.

The journal now records **how** each order entered, via a nullable `Order.EntryMethod`
(`OrderEntryMethod`: `Manual`, `ArmedTake`, `ModifiedTake`, `SendAsIs`, `Conditional`; the sentinel `Unknown` is
DB-refused, and NULL admits rows journaled before the field existed). This is the taxonomy this ADR named at the
top — *"Manual ticket, take-a-suggestion, send-as-is, a modified take, and a conditional"* — made durable: an
armed ticket stages `ArmedTake`, an edit before take reclassifies it `ModifiedTake` (R-11 records deviations), a
one-action send is `SendAsIs`, a fired conditional is `Conditional`. *Out of scope (their own increments):* the
**default entry-action preference** and its practice-only / confirm-to-enable guard (gh#218), and the split-button
UI (gh#25).

## Update (2026-07-25) — cancel a working order via the order API (gh#250)
The venue seam has always cancelled orders (`IOrderExecutor.CancelOrderAsync`, used by the kill switch), but an
operator had no way to pull a **single** resting order without engaging the whole kill switch. The `DELETE
/orders/{id}` handler — previously a staged-ticket discard — is now **polymorphic**:

- A **`Staged`** ticket is server-side only, so it is discarded in place (unchanged). A **`Working`** order is
  cancelled at the **venue** and reconciled to `Cancelled`; a terminal order is refused.
- A cancel is **risk-reducing**, so it deliberately bypasses the send ladder — **no risk profile, no flat-account
  check, no kill-switch gate** (the kill switch refuses *new* orders, not cancels). It keeps only the R-20 scope
  and the one-credential-set process guard (ADR-0015); the venue is resolved **lightly**, not through `ComposeAsync`.
- The order's now-orphaned **stop plan is retired** with it (`Hidden`/`Native`/`Orphaned` → `Retired`), so the
  promotion watcher cannot later promote a native stop for a **cancelled** entry (the gh#183 Finding-4 hazard in a
  new guise). The cancel is **audited** (`AuditAction.OrderCancelled`, gh#220) as a secondary, failure-tolerant
  write; not `synthetic_risk` — a resting entry never filled, so no live position rested on the protection.
- A venue **rejection** (the order already filled or gone) **never forces a terminal status**: guessing `Cancelled`
  would mislabel a *filled* order. The journal is left for the account-event stream (gh#219) — the authoritative
  venue-truth reconciler — to advance, and the operator is told why (proven by a red-provable test).

*Then deferred, since landed:* **modifying** a working order (`VenueCapability.ModifyOrder`) — the other #219
follow-up — **landed gh#259** (see the Update below). Discarding a `Staged` order is already covered here.

## Update (2026-07-25) — modify a working order via the order API (gh#259)
The sibling of the cancel (gh#250): an operator can **reprice** a resting working order **in place**, keeping its
queue position and its attached protective bracket — rather than a cancel/replace, which would surrender both and
open a naked window between the pull and the re-send. `PATCH /orders/{id}/price` — a new verb on the order group,
distinct from the staged-ticket edit (`PUT /orders/{id}`) and the cancel (`DELETE /orders/{id}`).

- A new venue seam, `IOrderExecutor.ModifyOrderAsync` (**default-throwing** like `GetWorkingOrdersAsync`, so a venue
  that cannot modify degrades **loudly**, R-17), reaches the gateway's in-place modify; the ProjectX adapter now
  advertises `VenueCapability.ModifyOrder`.
- **A reprice re-gates — it is not the cancel's gate-exempt cousin.** Unlike a cancel (risk-reducing), a reprice can
  *add* risk — a wider stop raises the per-contract loss; an entry moved toward the market is likelier to fill — so
  it runs the **full** `ComposeAsync` ladder and a new `OrderExecutionService.ModifyAsync`: the **kill switch refuses
  it** (outbound, like a send), and the gate must approve the new price. Enforcement stays below the model.
- **Size is held invariant on a pure reprice, and the whitelist is stricter than a send's.** For a *reprice*,
  `ModifyAsync` transmits only on `Allowed` at the **exact requested (unchanged) size**; a `Resized` decision — which
  a *send* honours by downsizing — **refuses** the reprice. The always-native safety-stop bracket leg has **no
  addressable size** through a modify, so silently downsizing a *reprice* would strand protection. With size fixed on
  a reprice, the bracket's quantity coverage is always exactly right. (Resizing was then deferred as a separate
  increment, gated on verifying the gateway's attached-bracket resize behaviour — "uncertainty resolves to safe",
  engineering §9. That gate was settled and the resize **landed gh#292**: the bracket carries *no size of its own* in
  any authoritative source, so it is sized to the realized fill on attach — there is nothing to desync. See the
  gh#292 Update below.)
- **Entry-only: both stops are untouched.** A reprice moves the **entry** on the order and the (`Hidden`) stop
  plan's entry basis in **one commit**; the working stop, the safety stop, and its native bracket leg all stay at
  their absolute levels, which the re-gate re-validates are still protective relative to the new entry (or the modify
  is blocked). The entry is refused **before the venue** if it would cross its own stops — otherwise a crossed entry
  would trip the `StopPlan` safety-beyond-actual DB CHECK *after* the venue already repriced, desyncing the two.
  Moving the working stop is a separate increment (it can separate a coincident working/safety pair, needing a *new*
  plan, or diverge from an already-promoted native stop) — **landed gh#267** (see the Update below).
- **A venue rejection never forces a status**, and a fill/cancel landing mid-flight **aborts the price write** (a
  fresh `Working` re-check before commit — the gh#183 re-open-race discipline): the account-event stream (gh#219) is
  the authoritative reconciler, exactly as for the cancel. The reprice is **audited** (`AuditAction.OrderModified`,
  gh#220) as a secondary, failure-tolerant write; **not** `synthetic_risk` — a never-filled resting entry, nothing
  live rested on it.

## Update (2026-07-25) — the default entry action preference (gh#218)
The open follow-up — *"decide the default entry action"* — is settled: **practice-only AND confirm-to-enable**.
`DefaultEntryAction` (`ApproveAndArm` | `SendAsIs`) persists on the risk profile (`RiskProfileRecord`, where the
data dictionary homes operator preferences). `ApproveAndArm` is the **zero value** — the `KillSwitchMode.FlattenAll`
fail-safe pattern, not the refusable-`Unknown` one — so an unset or legacy row resolves review-first by
construction, and there is deliberately **no `<> 0` check** (it would refuse the safe default). The column is
non-`required` with a `NOT NULL DEFAULT 0` migration; legacy rows read `ApproveAndArm`.

Two guards, server-side at the `PUT /accounts/{id}/risk` boundary — never merely hidden in the UI:
- **Practice-only:** defaulting to `SendAsIs` on a **live** or **undeclared** account is refused **409**, naming the
  mode. The rule lives in `TradingModePolicy.SendAsIsDefaultAllowed(mode)` (= `mode == Practice`) — the home of mode
  rules, reused rather than re-hand-rolled — and is **environment-independent** (a live account is refused even in
  production, because this guards the *default preference*, not the send path).
- **Confirm-to-enable:** a request that sets `SendAsIs` without `confirmSendAsIsDefault = true` is refused **422**
  (the kill switch's hold-to-confirm shape). Setting `ApproveAndArm` back is always free.

The preference is a **UX hint only** — it selects the split button's primary; it never reaches the enforcing gate,
so a breaching ticket is blocked or resized identically under either value (a covering test sends the same
resizable ticket under both and asserts the same approved quantity reaches the venue). *Out of scope:* the
split-button UI and Settings surface (gh#25), and the other open per-environment defaults (sizing basis, proximity
metric).

## Update (2026-07-25) — the promotion race fenced (gh#183 follow-up)
The gh#183 review (Finding 4, MEDIUM) surfaced a race the OCO-exit landing itself could not close: `StopPromotionService`
promoted a `Hidden` stop purely on `Staging == Hidden` **and** `ShouldPromote(price)`, with **no position-awareness**
and no reconciliation of a concurrent exit. A promotion in flight when a position was flattened could (a) place a
native protective stop for an **already-flat** position — the very dangling-leg hazard gh#183 exists to remove — and
(b) **clobber** an OCO-exit's `Retired` back to `Native`, leaving the journal asserting live protection for a position
that no longer exists. It is fenced on the **promotion side** now — deliberately, so the fix touches only the racing
watcher and never the exit path it races:

- **Position-awareness (venue truth).** Before it places, the watcher reads `GetPositionsAsync` for the contract and
  promotes **only** when the venue still reports net exposure on the entered side (the `OrphanGuardService` /
  `OcoExitService` re-validation pattern). Flat, reversed, or **unconfirmable** all fail closed to **not** promoting —
  the always-native safety stop remains the floor, and OCO-exit retires the plan on the exit event. Uncertainty
  resolves to the safe state (§9).
- **Re-check-then-record, and self-cancel the dangling leg.** After transmitting (and per record, so one stop's
  outcome never rolls back another's), the watcher **re-reads the plan's staging** before recording `Native`. If it
  observes the plan moved off `Hidden` — an OCO-exit retire, an orphan-guard on a drop, or an order-cancel (gh#250) —
  it does **not** record `Native`; instead it **cancels the native stop it just placed** (it holds the venue handle),
  since that stop now
  rests for a position that is gone. A cancel that fails is the `synthetic_risk` case — logged loudly for manual
  intervention, never thrown. This closes the residual the earlier draft left open: the promoter cleans up its **own**
  leg rather than relying on the exit's leg-sweep, which may have already enumerated working orders before the transmit
  landed. It is a re-read, **not an atomic CAS** — a retire committing in the sub-millisecond window between the
  re-read and the save is still recorded as a stale `Native`, but that is **benign**: nothing re-acts on a `Native`
  plan, and OCO-exit's leg-sweep (which runs after its own retire) still cancels the physical leg, so the residue is a
  rare journal/audit blemish, not a live dangling stop. (A true CAS would need a token — rejected below — or provider
  `ExecuteUpdate`, which the in-memory test provider lacks.)
- **Why not an optimistic-concurrency token.** A first pass added an `xmin` token to `StopPlanRecord`. It was
  **rejected on review**: the token is symmetric across **every** `StopPlans` writer, so it would turn a lost race in
  OCO-exit's *own* retire into an unhandled `DbUpdateConcurrencyException` that skips its `CancelNativeLegs` cleanup and
  drops the account-event stream — reintroducing the exact hazard on a different path. The promotion-side re-check needs
  no token, no migration, and leaves the exit and orphan paths untouched.

## Update (2026-07-26) — reprice a working order's working stop (gh#267)
The gh#259 deferral lands: an operator can now re-stage the **hidden working stop** on a resting working order, alone
(the entry-only path is unchanged; moving the **entry and working stop together** in one request **landed gh#278** —
see the Update below). `PATCH /orders/{id}/price` gains an optional `WorkingStopPrice`. The key realisation — validated by
a design pass — is that a hidden working stop is **not at the venue**: it is a *promotion target*, transmitted as a
native order only on promotion, which gh#263 gated on an **open position** — so an unfilled working order's stop is
never promoted, and moving it is a **local** write with **no venue call**.

- **Local, and safety-bounded — with one exception that re-gates.** Every **hard** limit (max-DD-per-trade, the
  drawdown floor, the daily loss / governor) is sized at the **safety** stop, which never moves, so `Q × safetyLoss`
  is unchanged and those limits are provably preserved. A move therefore runs **no** gate / kill-switch / flat check —
  *except* one case: the **PerTradeRisk** layer is sized at the **working** stop under `SizingBasis.ActualStop`, so a
  **widen** there can exceed it at the resting size. That single case re-gates via `Evaluate` (**no transmission** —
  the arm precedent) and is refused unless the gate still allows the resting size. A **tighten**, or any move under
  `SizingBasis.SafetyStop` (the working stop is absent from the gate's math), or **creating** a plan (always a
  tightening vs the prior coincident pair) stays purely local.
- **The ordering invariant is the backstop.** The new working stop is re-validated **strictly** `safety → working →
  entry` (per side) **before** any commit — the only guard for the Order row (which has no CHECK) and the pre-empt for
  `CK_StopPlans_SafetyBeyondActual` on the plan row, which the risk gate does *not* enforce (it checks stops-below-
  entry only, the gh#259 finding). Moving the working stop **onto** the safety stop is *removing* it — a distinct,
  out-of-scope action, refused explicitly.
- **Create, and refuse the promoted / emergency plans.** A coincident-stop order (no plan) gains a `Hidden`
  `StopPlanRecord` when a distinct working stop is installed (reusing the placement-time `AddStopPlan`). The plan is
  loaded **without** a staging filter and a **`Native`** (promoted — moot for a working order per gh#263, refused
  defensively), **`Orphaned`** (connection down — re-arms on reconnect), or **`Retired`** plan is refused; only a
  `Hidden` plan (or none, for create) re-stages, so a raced promotion's native venue stop can never diverge from the
  journal. For a stop-type order the journal `StopPrice` moves in lockstep with the working stop (the `ApplyProposal`
  convention). Re-staged locally in one commit, audited (`AuditAction.OrderModified`); size and the safety stop are
  never written.

## Update (2026-07-26) — move the entry and working stop together (gh#278)
The last modify follow-up (bar a resize): a single `PATCH /orders/{id}/price` may now carry **both** `EntryPrice`
and `WorkingStopPrice`. It is the gh#259 **entry venue path** (`ComposeAsync` → `ModifyAsync` re-gates and reprices
the **entry** at the venue) with the new working stop threaded through — the handler `RepriceEntryAsync` gained an
optional working stop, so entry-only and combined share one path; a working-stop-only move stays on the gh#267 local
re-stage. Only the entry ever reaches the venue (the working stop is a hidden local plan).

- **The gate re-validates the moved working stop for free.** `BuildRequestAsync` is fed the **new** working stop, so
  a risk-increasing widen is caught by the same `ModifyAsync` re-gate the entry already runs — no separate widen
  branch is needed. The kill switch refuses the combined move (the entry reprice is outbound), like any entry reprice.
- **Full-chain ordering, pre-venue — the gh#259 desync lesson applied to both prices.** The **effective** geometry
  (`safety → working → entry`, strict, per side) is re-validated **before** the venue call, so a combined move that
  crosses the chain is refused before the entry reprices — otherwise the working-stop leg would trip
  `CK_StopPlans_SafetyBeyondActual` at commit *after* the venue already moved the entry, desyncing the two. `working
  == safety` (removal) stays out of scope.
- **Atomic, and the gh#267 plan rules carry over.** The entry (+ reference + limit), the working stop (+ `StopPrice`
  lockstep for a stop-type order), and the `Hidden` plan's `EntryPrice` **and** `ActualStopPrice` commit in one
  `SaveChanges`; a venue rejection on the entry leaves **all** of them untouched. A `Native` / `Orphaned` / `Retired`
  plan is refused; a coincident-stop order gains a `Hidden` plan (`AddStopPlan`). Audited; a sized gate attempt leaves
  a `GateDecisionRecord`. Only a **resize** — then *assumed* to also require re-sizing the always-native safety
  bracket — remained deferred; it **landed gh#292**, which found that assumption false (the bracket has no size of
  its own). See the Update below.

## Update (2026-07-26) — resize a working order (gh#292)
The **final** modify follow-up: `PATCH /orders/{id}/price` may now carry a **`Size`**, changing a working order's
contract quantity in place. It reuses the gh#259 venue path — the handler `RepriceEntryAsync` was generalised (and
renamed **`ModifyAtVenueAsync`**) once more so entry, working stop, and size are each optional, and a **size-only**
resize routes there too (a size change must reach the gateway and re-gate; only a working-stop-*only* move with no
size change stays on the gh#267 local re-stage). It composes with an entry reprice and a working-stop move in one
atomic commit.

- **The size-invariant deferral is lifted, because the premise did not hold.** gh#259 refused a `Resized` decision on
  a modify for fear of desyncing the attached safety bracket. But the ProjectX `stopLossBracket` carries **no size
  field** in any authoritative source (the vendored `swagger.json` `PlaceOrderBracket = {ticks, type}`; the live
  vendor order-place docs — *"size is defined only at the parent order level"*; the wiki; the C# `OrderBracket`
  model), and the gateway attaches it **on fill**, sized to the realized fill. There is no stored bracket quantity to
  desync: a resize-up is protected at the larger fill, a resize-down at the smaller. So `ModifyAsync` gained a
  `resize` flag: a **resize honours the gate like a send** (`Allowed` **or** `Resized`) and transmits the
  **gate-approved** quantity (never the asked one — a downsize the gate binds is honoured, echoed in the response,
  never silent); a **reprice keeps the strict whitelist** and its `size: null`.
- **The `Working`-only guard closes the one real window.** The only case where a *stale* bracket size could exist is a
  **partially-filled** parent; the handler already refuses any non-`Working` order, so a resize only ever acts on a
  0-filled order whose bracket materialises fresh at the whole new fill.
- **Enforcement stays below the model.** A resize re-gates at the **new** size (hard limits at the safety stop; the
  per-trade layer at the working stop under `ActualStop`); `Block` / zero refuses and transmits nothing; the kill
  switch refuses the outbound modify (a downsize is *not* kill-switch-exempt this increment — the operator retains
  `DELETE` to reduce risk; a downsize-as-cancel exemption is a deferred refinement). Only `Size` (the gate-approved
  quantity) is written — the **safety stop is invariant**; the `StopPlan` is untouched by a pure resize (a hidden plan
  has no size); the TOCTOU `Working` re-read aborts the size write if a fill lands mid-flight.
- **Practice-gated, with a live-verification prerequisite.** The sizes-to-fill conclusion is a strong *structural
  inference* the vendor docs imply but never state, so — matching how gh#259 shipped behind gh#269 — the whitelist
  relaxation is gated on a practice-account check (**gh#293**, on the Phase-2 pre-live checklist): resize up, fill,
  read that the native protective-stop quantity equals the new fill; the reverse for a downsize; and a partial fill
  in the window sizes the bracket to the *realized* fill. *(Also flagged there: a pre-existing client-model /
  swagger mismatch — the C# `OrderBracket` uses `stopPrice`, the authoritative swagger uses `ticks` — untangled
  separately.)*

## Update (2026-07-26) — the promoted stop is sized to the live remaining (gh#277)
A hazard the gh#263 review flagged as pre-existing (and correctly out of its scope): the promotion watcher placed the
native stop at **`Order.Size`** — the original entry quantity — while gating only on the position's **sign**. A
**partial scale-out** on the connection-up path (`Order.Size` is reconciled to the remaining only on the *reconnect*
path, gh#191) left the promoted stop **over-sized**, so on fire it would close more than is held and **reverse** the
position into an unwanted one — the "next fill opens a position you didn't ask for" class this area exists to remove.
The fix reuses the live `NetQuantity` gh#263 already reads: the promoted stop is sized to
**`min(Order.Size, |NetQuantity|)`** — the venue's reported open quantity, capped at this plan's order size (a larger
net is other entries' concern, not this plan's to double-protect). It mirrors `OrphanGuardService`'s existing
partial-close reconciliation. The **safety-stop bracket** (placed on entry, exchange-managed OCO) and the
**conditional-firing** path (which promotes through this same watcher) need no separate change.

## Update (2026-07-27) — the ATR band is live, and the caller resolves it (gh#311)

This ADR has named the promotion band as *"ticks / ATR / fraction of the entry→stop distance"* since it was
written, and ATR has been the one it could not honour: gh#153 refused the metric outright rather than mis-measure
it. gh#310 made ATR measurable, so **the refusal is gone** and all three metrics the ADR names are now real.

**Where a metric becomes a number moved.** `StopPlan` no longer derives a distance from its own band — it takes
one. `StopProximity.ResolveDistance(...)` turns a metric into an absolute price distance, the **promotion watcher**
calls it, and `ShouldPromote(price, bandDistance)` compares. Two alternatives were rejected:

- **Resolve ATR at construction.** The band would be frozen at the moment the plan was built and would go
  **stale as ATR moved** — a stop whose promotion distance silently drifted from its intent, which is the failure
  this model exists to prevent rather than commit.
- **Inject an indicator source into `StopPlan`.** A pure, immutable value object *reconstructed from the
  database* is the wrong place for an I/O-shaped dependency, and this one sits on a safety-critical path.

The gain beyond ATR: **a future metric is a new arm in the resolver, not a change to the domain type**, and the
whitelist-refusal pattern stops being the only way to keep the domain honest about what it can measure.

**An unmeasurable band does not promote.** `ResolveDistance` returns *no distance* when ATR is unavailable —
insufficient history, a projection behind, a band resolution the backfill is not archiving — and the watcher
skips the plan. It deliberately does **not** fall back to a default: a substituted distance is exactly the silent
mis-measurement the original refusal existed to prevent. Note this is a real distinction from a **zero** band,
which is a legitimate configuration meaning *promote on touch* — collapsing an unmeasurable band to zero would
promote, not abstain, so the old `BandDistance()` zero-collapse arm was removed rather than reused.

Fail-closed here means the working stop **stays hidden**, which is a genuine cost, not a free choice: it leaves
that stop dependent on platform liveness for longer. It is accepted because **the always-native safety stop is
the physical floor throughout** (this ADR's central provision), and because promoting at a distance nobody chose
is the worse of the two errors. The condition is **logged at warning** naming the instrument, the period, and the
resolution — the symptom is otherwise invisible, since "never promoted" looks identical to "price never came
close." Alerting on it belongs to the observability increment (gh#26 / gh#245, ADR-0019).

**Which ATR a band means** is `Indicators:AtrPeriod` × `Indicators:BandResolutionMinutes` — 2 × ATR on 1-minute
bars is a very different distance from 2 × ATR on 15-minute bars, and this ADR does not say which, so it is
configuration rather than an assumption buried in code. The resolution **must be one the backfill is archiving**
(`Backfill:ResolutionMinutes`); if it is not, there is no value and ATR-banded stops do not promote — hence the
warning naming that setting.

**No migration.** `CK_StopPlans_ProximityMetric_NotUnknown` only ever rejected zero, so an ATR plan already
persisted fine; the domain rebuild was the only gate, and it is now open. The `StopPlanPersistenceIntegrationTests`
pin reserved for this path was flipped into real round-trip coverage in the same PR.

## Update (2026-07-28) — resting orders are readable through the app (gh#381)

`IOrderExecutor.GetWorkingOrdersAsync` was added for exactly one consumer: OCO-cancel-on-exit finding the
protective legs still standing on a contract that has gone flat (gh#183). It now has a second — an HTTP read,
`GET /accounts/{id}/orders` — and the venue-neutral view it returns gained a **size**.

**Why the size was missing, and why that mattered.** `WorkingOrder` was deliberately narrow: *"what a caller needs
to identify a leg and audit it, not the whole order model."* Cancelling a leg needs its handle, not its quantity,
so size was left off — a correct scoping call for the only consumer that existed. But *how much of a position a
protective leg actually covers* is not an ornament: **a bracket sized to less than the position leaves the
remainder unprotected**, and nothing through the app could see that. This was never a venue limitation; the
ProjectX gateway order has always carried `Size` and only the final projection dropped it.

**Why it is a read in the `Recovery` family, not in the order command group.** `/accounts/{id}/orders` already
exists as a **POST** command surface (journal-backed). This is a **GET** on venue truth, and it belongs beside
`GET /accounts/{id}/positions` — the app's venue-truth read family (ADR-0013) — because that is what it is. The
two registrations do not collide, and keeping them apart keeps the boundary honest: one records intent, the other
reports what the exchange says is true.

**Read-only, and the gate is untouched.** Nothing here is execution-shaped — no writes, no cancels, no
re-validation. The risk / execution gate is unaffected by design; a read that could move an order would be a
different and much larger change.

**The bypass this removes.** The gh#269 / gh#293 pre-live bracket gates (PR #374) had to talk to the ProjectX
gateway **directly**, around the app, to witness a resting protective leg and its size — because the app had no
such read. Every future venue-truth gate would have repeated that, coupling test and observability code to the
gateway instead of the app boundary. Those gates remain as they are; what changes is that the next one need not.

## Update (2026-07-28) — the consistency target binds, and its posture is per-account (gh#380)

The last unshipped S2 risk rule. `MaxBestDayFraction` had been persisted, range-checked and settable over the
risk API since the risk profile landed, but it was **never mapped into `AccountRiskRules`** — so the gate never
saw it. The rule was configurable without being enforced, which is the worst of both: an operator could set a
consistency target, see it saved, and trade an evaluation it never once measured.

**It is measured as _best day ÷ cumulative realized profit_**, not *today ÷ total*. The firm's rule caps the
largest single day, so an evaluation already disqualified by an outsized day last week must stay disqualified —
a today-relative reading would clear itself every midnight. Days are bucketed in **US Central** via
`MarketClock`, the same boundary the auto-flatten uses: a UTC date splits a CME session and halves a day's
apparent share, which is the direction that hides a breach. Only **closed** trades count, because a consistency
target is a realized-profit rule and counting open positions would make the fraction move with the market
rather than with what happened.

**Enforcement is per-account (`ConsistencyEnforcement`: `Advisory` | `Block`), not a property of the gate.**
Every other layer here protects **capital** — breach the drawdown floor and the account is gone, so refusing is
the only defensible answer. A consistency target protects **payout eligibility**: breaching it does not blow the
account, it silently disqualifies one that is otherwise passing. Refusing an order for it therefore costs real
trading on a day that is going well, while permitting it costs an evaluation — and which of those is worse
belongs to the account, not to this ADR. A funded prop account wants `Block`; a personal account with no payout
rules wants `Advisory`, or no target at all.

**`Advisory` is the default, including for every pre-existing row.** Those rows have been storing a target that
nothing enforced; backfilling them as `Block` would let a *migration* start refusing orders on accounts whose
operator never asked for it. Turning the refusal on is an explicit act.

**The advisory rides alongside the decision rather than becoming a `GateOutcome`.** A fourth "allowed but
concerned" outcome would silently change the meaning of every existing exhaustive `switch` on a safety-critical
path; `GateDecision.Advisories` leaves callers that ignore it behaving exactly as before. It is raised whenever
a target is configured — not only on breach — because an operator who first hears about the constraint when an
order is refused has already spent the evaluation it was protecting.

**Completed by `gh#407` (2026-07-28).** The clause above — "leaves callers that ignore it behaving exactly as
before" — turned out to describe *every* caller: the advisory was built, attached, and then **dropped at the API
boundary**. `SendOrderResponse` had no advisory member and `GateDecisionRecord` persisted none, so the warning
existed only in memory. That made the rule inert precisely where it mattered most: `Advisory` is the migration
default, so on every pre-existing account the consistency target neither refused (by design) nor warned (the
defect) — configurable, persisted, validated, evaluated on every order, and invisible. Found by QA `gh#394`,
which pinned it rather than blessing it.

The advisories now ride out on `SendOrderResponse` and `StagedOrderResponse` — **on every outcome, not only
refusals**, since an advisory account never refuses and this is its only signal — and are journalled on the gate
decision as `jsonb`, so *"was the operator warned before the payout was disqualified?"* is answerable after the
fact. The column is **nullable with no default**: null means "no advisory", which is exactly what every row
written before it existed means, so old and new rows read alike rather than the migration claiming historical
decisions were known to carry none. A `Block`-posture refusal is unchanged — it already surfaced through
`BindingLayer` and `Reason`.

## Update (2026-07-30) — the take path is claimed before it reaches the venue (gh#530)

`POST /orders/{id}/take` read the staged row, checked `Status == Staged`, and then spent **four venue round-trips**
composing and sending. Two concurrent takes each passed that check against their own change tracker — two requests
share none — so **both transmitted**, and both then wrote the same row. One live venue order ended up recorded on
**no `Order` row**: invisible to cancel, to the kill-switch sweep and to the orphan guard, every one of which
resolves by row id. A double-click, or two ADR-0006 pop-out windows on one ticket, is enough.

**The fix is a claim, not a re-read.** A new `OrderStatus.Taking` is taken by a database-evaluated conditional
UPDATE before anything touches the venue, so the loser refuses immediately. A post-venue re-read would be too late
by construction: by then the second order exists. The claim is handed back on every path that does not reach the
venue, so a refused take leaves the ticket exactly as takeable as it was.

**Why it is behind a seam** (`IStagedOrderClaim`). Only the database can arbitrate between two requests, which
means `ExecuteUpdate` or raw SQL — and neither is supported by the EF in-memory provider. Written inline it made
the take endpoint unrunnable at the unit tier and took six existing take guards with it. The seam keeps those
guards running against a faithful double and puts the real compare-and-swap where it can be *proven*: the
container-backed Postgres tier. The double reproduces the observable contract (claim only from `Staged`, release
only from `Taking`); what it cannot reproduce is atomicity under real concurrency, and that is stated where it
lives rather than implied.

**This does not reopen the rejected concurrency token.** That decision was about a table-wide token on `Order`,
which is symmetric — it would fault the seam's, the cancel's and the take's own writes. This is a single
conditional UPDATE on one column, taken deliberately by one path.

**A second race closes with it.** A cancel landing mid-take used to write `Cancelled` over a row the take then
overwrote back to `Working` — a live order resting on a ticket the operator had just cancelled. `Taking` makes
that state visible, and cancel now refuses it: the outcome is seconds away, and the resulting working order is
cancellable once it resolves.

Still open, filed separately: **gh#531** — two concurrent *sends* do not orphan anything (each mints its own row)
but do evaluate the risk gate against one snapshot, admitting up to 2× the approved risk. Different path, different
blast radius, and the same claim mechanism may or may not be the right answer there.

## Update (2026-07-30) — the kill switch survives a venue that says no (gh#529)

Three faults on the engage path, all on the control an operator reaches for **when something has already gone
wrong**. The primary safety property was never at risk — outbound is blocked and the durable `KillSwitchState` row
is committed in its own `SaveChanges` *before* the account loop — which is why this was P1 and not P0.

**1. A venue refusal abandoned every remaining account.** `ProjectXVenue.ClosePositionAsync` throws on an ordinary
refusal — a refusal is an exception on this path, not a return value — and nothing above caught it. One account's
refusal unwound both loops: its remaining contracts, every other account, and every other connection were never
touched. The operator got a 500 naming no account. Now isolated per account *and* per contract: a contract that
refuses stays outstanding and is retried, an account that cannot be reached is named and the sweep presses on. This
is the invariant `CancelWorkingOrdersAsync` already stated one call away — *"a single cancel failing must not abort
the kill — log it and press on with the rest."*

**2. The cancelled statuses and the engagement event went with it.** The same unwind skipped `SaveChangesAsync`, so
tracked `Cancelled` rows were discarded with the request scope, and skipped the `killswitch.engaged` append — which
this ADR states unconditionally is journaled. Both are now reachable on the fault path, and the entry names the
accounts that could not be completed, because those are precisely the ones the operator must now handle by hand.

**3. `FlattenedPositions` was provably wrong on two paths.** The escalate branch returned
`outstanding.Count(p => p.IsFlat)` — structurally **always 0**, since `outstanding` is only ever assigned from
`.Where(p => !p.IsFlat)` — and the success path returned the *last attempt's* count, so three positions closed
across two attempts reported one. Only the single-attempt case was right, and it was the only case any test
covered. Positions are now counted **as they close**.

**A new `killswitch.escalated` event.** Previously a venue that kept reporting a position open produced `200 OK`
with `FlattenedPositions: 0` — byte-identical to halt-only and to *nothing was open*, with a `LogError` as the only
trace. Its auto-flatten twin has journalled, paged and metered that same verdict since gh#455; the kill switch
could do none of it. The journal entry lands here; **paging and metering do not**, because `KillSwitchService` takes
neither `IExecutionMetrics` nor a notification seam, and threading them in is a wider change than this fix — filed
rather than smuggled.

## Update (2026-08-02) — conditional firing commits per record (gh#532)

`ConditionalFiringService.ProcessQuoteAsync` fired every pending conditional a quote crossed inside a per-record
loop but performed **one** `SaveChangesAsync` for the whole quote's batch. So a fault on a *later* record — a
routine venue rejection on the compose/send path, made *more* likely because an earlier fire just consumed margin —
discarded an *earlier* record's already-transmitted order: its `Order` row, `StopPlan`, `GateDecision` and `Fired`
transition were tracked in memory only, and the escaping exception disposed the owner context before the single
save ran. One live venue order recorded on **no `Order` row** — invisible to cancel, the kill-switch sweep and the
orphan guard, every one of which resolves by row id — and, because `ShouldFire` is a pure level test with no
crossing memory, **re-fired on the next quote** (a duplicate for a resting Limit/Stop entry; an orphaned position
for a Market entry that fills first).

This is the **same shape as the kill switch's second fault (gh#529)** — an unwind that "skipped `SaveChangesAsync`,
so tracked rows were discarded with the scope" — and it diverges silently from the discipline this ADR already
states for the sibling watcher: stop promotion reconciles **each plan independently, its own reload + save, so one
plan's lost race never affects another's** (asserted by `StopPromotionConcurrencyIntegrationTests`). The conditional
watcher batched instead.

**The fix is a unit of work per record.** Each pending conditional is now processed in its **own owner-scoped
`DbContext`** that commits on its **own `SaveChangesAsync`, before any sibling is touched** — so a peer's fault can
never unwind an order the venue has already accepted. A fault on one record is **contained**: logged, the
conditional left `Pending`, the pass pressing on to the rest — so one poison record neither discards a committed
peer nor starves the others, and it re-decides on the next quote (ADR-0013's safe "did not fire" direction) rather
than retry-storming the same event once a second. This is the invariant the kill-switch fix and the stop-promotion
watcher already hold; the conditional path now holds it too.

**Still open, filed separately.** The per-record commit narrows the window but does not close it: an order
**accepted at the venue** whose journal commit then fails — a DB fault, or cancellation at host shutdown — still
leaves a live order on a `Pending` conditional a later quote can re-fire. That is **gh#577**, which wants a durable
pre-transmit intent or the `customTag` idempotency handle `PlaceOrderRequest` exposes and the adapter never sets —
the theme it shares with the take-claim (gh#530) and the concurrent-send (gh#531). The independent real-Postgres
proof — two conditionals on one contract with a later transmit thrown via `AdversarialTestTradingVenue.OnPlaceOrder`,
a shape the firing suite's per-test-instrument isolation excludes today — is **gh#578**. And this path still
composes without `IExecutionMetrics`, so the fire-time gate decisions and order-acks land in `NullExecutionMetrics`
and the SLIs do not yet witness a firing-path anomaly — a known observability gap, noted rather than smuggled into a
safety fix.
## Update (2026-08-02) — the direct-send path serializes per account against send-vs-send stacking (gh#531)

The gh#530 note above left this open: two concurrent **sends** (`POST /accounts/{id}/orders` and its `send-as-is`
twin) do not orphan anything — each mints its own `Order` row — but both evaluate the R-5 gate against **one flat-account
snapshot** and both transmit, admitting up to **2× the approved risk**. `ComposeAsync` sizes off `venueAccount.Balance`
with `UnrealizedPnL = 0` and reserves **nothing** for an outstanding working order (a resting entry changes neither the
venue's positions nor its balance until it fills), so the second request re-reads the same headroom the first did. A
gate that can be raced admits more than the operator authorised — the one thing the enforcing layer must never do
(R-5 / R-11 / R-16, and the "enforcement below the model" frame above).

**Serialization alone is not enough — the second send must also *see* the first's order.** The fix is two parts, and
needs both:

- **A no-stacking precondition.** Before it transmits, a send refuses (409) when the account already holds an
  outstanding **entry** with real venue exposure — an `Order` in `Working` or `PartiallyFilled`. This is the honest
  extension of the increment-1 flat-account rule: a resting entry *will* fill, and the flat-account gate cannot size
  against exposure it is assuming away. It does **not** count `Staged` (server-side only, unsent), terminal orders
  (`Filled` is already in the balance `ComposeAsync` reads; `Cancelled` / `Rejected` never rested), or **`Taking`** — a
  take in flight. **Taking is deliberately excluded:** gh#531 is send-vs-**send** only (the take path does not take this
  guard, so counting `Taking` would not close send-vs-take anyway), and a take that **strands** in `Taking` — a venue
  timeout or a client disconnect mid-take, for which there is no in-app recovery today — would otherwise dead-lock the
  *whole account's* send path, not just that ticket (a defect this increment must not introduce; the adversarial review
  caught it). So single-send behaviour on a genuinely clear account is unchanged.
- **A per-account lock.** The check and the place are made atomic per account by a Postgres **session advisory lock**,
  held on a pinned connection across the whole transmit behind a new `IAccountEntryGuard` seam
  (`RunExclusiveAsync(accountId, transmit)`). A second concurrent send for the same account blocks until the first
  commits its `Working` row, then the check (which runs *inside* the lock) sees it and refuses — **without ever
  reaching the venue**. Different accounts hash (`hashtext`) to different keys and never block each other.

**Not the gh#530 per-order claim, and not a concurrency token.** The take race contends two requests over **one shared
staged row**, which a conditional `Status` CAS arbitrates; a direct send creates a **distinct new row** each time, so
there is no shared row to CAS — the contention is on the *account*, hence a per-account lock. The table-wide
concurrency token this ADR rejected twice (symmetric across every writer) is still the wrong tool for the same reason;
the advisory lock touches only the send path. A **session** lock (not `pg_advisory_xact_lock`) opens no enclosing
transaction, so the journal keeps its single-`SaveChanges` auto-commit, and a dropped connection auto-releases the lock.

**Two costs of holding the lock across the venue call, both accepted for a single-operator tool.** The lock is a
*session* lock, which requires the connection stay **pinned** for the callback — including across the venue
round-trip. (1) **Liveness:** `pg_advisory_lock` blocks with no timeout, so a slow/hanging venue holds the connection
and the lock for the whole round-trip (bounded by the ProjectX client's HTTP timeout), and a concurrent *same-account*
send waits that long; different accounts are unaffected and one operator bounds the blast radius. (2) **An enlarged
place-then-journal window:** pre-fix, the journaling `SaveChanges` drew a fresh, pool-validated connection *after* the
venue call; now the pinned connection sits idle across that call, so a mid-call backend drop (an idle-timeout, a
PgBouncer/managed-PG cap, a NAT reset — Npgsql keepalives are off by default) would fail the journaling write and leave
a placed order un-journaled. This is the pre-existing place-then-journal orphan window at **higher probability**, not a
new failure class; it is low on a default self-hosted Postgres (`idle_session_timeout = 0`), the operator sees the
error (a 500, not a silent loss), and the account-event stream is the venue-truth reconciler — though note it currently
**logs-and-ignores** an order with no journal row rather than recovering it, so the durable mitigations are enabling
Npgsql keepalives and QA asserting exactly-one-journaled under an induced mid-callback connection drop. *(An earlier
draft of this note wrongly claimed "no new orphan window"; the adversarial review corrected it.)*

**Behind a seam for the same reason gh#530 is** (`IStagedOrderClaim`): the EF in-memory provider runs no advisory lock
or raw SQL, so the unit tier fakes the guard and drives the deterministic no-stacking check (a unit test also pins that
the check + place run *inside* the callback), and the *real* serialization is proven where it can be — the
container-backed Postgres tier (QA). Enforcement stays below the model: this is a DB lock + a status predicate, never
prompt text.

**Scope — send-vs-send only.** This closes two concurrent *sends*. The sibling account-level race the same flat
snapshot allows across the *other* transmit paths — two **takes** of different staged orders, a send racing a **take**,
or a send racing a **conditional fire** (`ConditionalFiringService` places via `SendAsync` with no guard) — is real but
out of this increment's blast radius (it touches the just-stabilised take path), as is recovering a stranded `Taking`
row. All are filed as **gh#589**, which reuses this `IAccountEntryGuard` seam (with a self-excluding check, so `Taking`
can safely re-enter the counted set once the dead-lock hazard is removed). OCO-exit is *not* affected: it places exits
after a fill, when the account is non-flat, so `ComposeAsync`'s flat check already refuses a racing send.

## Update (2026-08-02) — the transmit→journal window closes with a durable pre-transmit intent (gh#577)

The window the gh#532 fix named above — an order **accepted at the venue** whose journal commit then fails (a DB
fault, or cancellation at host shutdown), leaving a live order on a `Pending` conditional the next quote re-fires — is
now closed on the DB side. `ConditionalFiringService` commits a **durable pre-transmit intent** — a new
`ConditionalStatus.Firing` — **before** it touches the venue. A fault anywhere from there to the journal commit
therefore leaves the conditional **Firing**, not `Pending`; discovery and `ShouldFire` are Pending-only, so it can
**never blind-re-fire** a live-but-unjournaled order. It is reconciled / surfaced against venue truth on replay — the
ADR-0013 discipline (*"never auto-act on rehydrated state; re-validate first"*) — rather than acted on blindly.

- **A venue rejection *before* acceptance reverts the intent to `Pending`** and re-decides on the next quote — the
  gh#532 containment, preserved (a routine rejection is not stranded). The intent is committed *before* the send, so
  **any** fault before the fire resolves (to `Fired`, or back to `Pending`) leaves it `Firing`. The dangerous ones —
  an **accepted-then-unjournaled** fire, or a **shutdown mid-send** whose outcome is unknown — may have a live order
  behind them; a fault on a **pre-transmit** path (a crash between the intent commit and a gate-refusal revert, a
  compose fault) leaves a *harmless* `Firing` the reconcile clears on finding no venue order. Either way it fails
  safe: `Firing` never re-fires, and uncertainty resolves to the safe state (§9).
- **A `Firing` conditional found persisting across a restart is an impossible combination.**
  `DecisionStateRehydration.Analyze` now flags it (`ConditionalMidFiring`), so the rehydration pass (gh#221) fails
  **safe and loud** — the kill switch (HaltOnly) + a `synthetic_risk` alert — exactly as it does for a fired
  conditional linked to no order. Firing is transient at runtime; at rest it is a contradiction, never silently repaired.
- **The venue-side counterpart is a `customTag` correlation handle.** Each fire stamps the conditional's own id as the
  order's `customTag` — the field `PlaceOrderRequest` has always exposed and the adapter never set — threaded through
  the neutral `OrderRequest` onto the wire payload, carried on the acknowledgement (`PlacedOrder`, the value we sent —
  the place-response does not re-report it) and **echoed back by the venue on the resting-orders read** (`WorkingOrder`,
  the surface a reconcile reads). So a replay can recognise its **own** already-placed order (matching on the
  conditional's id) rather than transmit a blind duplicate. The venue does **not** dedup on it: it is a *correlation*
  handle, not a venue idempotency key.

**No migration.** `Firing` is admitted by the existing `"Status" <> 0` check (the gh#209 precedent for adding a
lifecycle value), and the tag is a transient request field, not a stored column.

**Scope — of the two seams gh#577 left open.** This lands the durable intent **and** the correlation handle. The
**automated venue-truth reconcile** — a pass that reads the venue and recovers a `Firing` record without the operator
— is **deliberately deferred**: it would *auto-act on rehydrated state*, which this subsystem resists in favour of
surfacing to the operator, and the resting-orders read must carry side/type before its match is more than heuristic.
The one residual the intent does not cover — a **transport fault that in fact landed** (indistinguishable from a
definitive rejection without a venue-seam refusal *outcome*, so reverted to `Pending`) — and the independent
real-Postgres proof of the whole window are **gh#578**.

## Follow-ups
*Most of the original follow-ups have since landed; each is annotated inline. The dated updates above are the
authoritative record — this list is kept only as a decision-provenance changelog.*
- ~~Define the **order-state machine** + per-transition **journal / event records** (R-8/R-9, ADR-0001).~~ **Landed** —
  the order lifecycle + `AuditRecord` / event-log journaling ship across the execution suites.
- ~~Spec the **synthetic / conditional engine**: trigger types, promotion-band metric + **default**, OCO
  coordination, gap/latency handling, and an availability target.~~ **Landed** — the conditional firing engine
  (gh#180/#198) + OCO-cancel-on-exit (gh#183/#184) + stop promotion.
- ~~Define **connection-loss detection** (heartbeat / timeout thresholds), the **orphan → emergency** transition +
  operator alert, and the **recovery re-arm** path — each carrying a `synthetic_risk` audit flag.~~ **Landed** — the
  venue-connection monitor + orphan handling / re-arm (gh#191/#192/#209); consolidated in ADR-0013.
- Decide **defaults** (per environment): sizing basis and proximity metric. *(The **default entry action** is settled and built — gh#218; see the update above.)*
- ~~Define the **risk-gate interface** — inputs (live account state, layers, safety stop), outputs (size, binding
  layer, block / resize / acknowledge) — R-5.~~ **Landed** — `RiskGate` with the layered decision (see the risk-gate
  update above).
- Wire the **governor → R-4** throttle policy (thresholds, throttle modes). *(The **daily/consistency governor** landed
  (gh#380); the R-4 suggestion-**throttle** modes are still open.)*
- ~~Confirm **ProjectX** native bracket / OCO / stop-type capabilities (Q-1); the synthetic layer covers gaps (R-17).~~
  **Confirmed / built** — native bracket preserve + resize (gh#259/#292), practice-gated on staging.
- ~~Stand up the **high-rigor test suites** for the risk gate, execution, staged stops, kill switch, and auto-flatten
  (engineering §9).~~ **Landed** — the QA integration suites (see `integration-test-audit.md`).
