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
- **Size is held invariant, and the whitelist is stricter than a send's.** `ModifyAsync` transmits only on
  `Allowed` at the **exact requested (unchanged) size**; a `Resized` decision — which a *send* honours by downsizing
  — **refuses** the modify. The always-native safety-stop bracket leg has **no addressable size** (attached-on-fill,
  implicitly sized to the parent fill), so a silent downsize would strand protection — a naked position on increase,
  an oversized stop on decrease. With size fixed, the bracket's quantity coverage is always exactly right: the
  sharpest hazard of the feature, sidestepped by construction. (A resize that *also* re-sizes the bracket is a
  separate increment, gated on verifying the gateway's attached-bracket resize behaviour — "uncertainty resolves to
  safe", engineering §9.)
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
  a `GateDecisionRecord`. Only a **resize** (which must also re-size the always-native safety bracket) remains deferred.

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
- Decide **defaults** (per environment): sizing basis and proximity metric. *(The **default entry action** is settled and built — gh#218; see the update below.)*
- Define the **risk-gate interface** — inputs (live account state, layers, safety stop), outputs (size, binding
  layer, block / resize / acknowledge) — R-5.
- Wire the **governor → R-4** throttle policy (thresholds, throttle modes).
- Confirm **ProjectX** native bracket / OCO / stop-type capabilities (Q-1); the synthetic layer covers gaps (R-17).
- Stand up the **high-rigor test suites** for the risk gate, execution, staged stops, kill switch, and auto-flatten
  (engineering §9).
