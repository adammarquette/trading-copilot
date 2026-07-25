# ADR-0013: Failure & recovery model

**Status:** Accepted · **Date:** 2026-07-19 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-1` (ingestion recovery), `R-4` (suggestion lifecycle / recovery), `R-12` (execution
re-validation), `R-13` (auto-flatten — safety-critical), `R-19` (PWA), `R-20` (tenancy); engineering §2 (SignalR
resume), §9 (safety-critical); [ADR-0001](0001-event-backbone.md) (event log / clean-historical),
[ADR-0007](0007-order-execution-model.md) (execution / orphan handling), [ADR-0010](0010-progressive-web-app.md),
[ADR-0011](0011-multi-user-tenancy.md).

## Context
Things fail: the user's client drops, the backend restarts, the venue connection breaks, or — worst — the cloud tier
is unreachable near the flatten deadline. The recovery behaviour for each already exists, but **spread across
R-4 / R-12 / R-13 / ADR-0001 / ADR-0007** — no single place states the whole model, and one part (the auto-flatten
guarantee) is a known-hard open item. This ADR **consolidates the recovery model for visibility** and names the piece
that still needs engineering design. It records *what already holds*; it does not invent new mechanisms (each stays
owned by its requirement / ADR).

## Decision — a layered recovery model
- **The client is presentation-only; it loses nothing.** All state lives server-side; the PWA (ADR-0010) resumes on
  reconnect via the **SignalR outbox + monotonic-sequence + idempotent-resume** pattern (engineering §2), catching up
  missed updates. A crashed / closed / slept client is a non-event.
- **On backend restart, state is rehydrated, not replayed live.** Decision state (suggestions, orders, positions,
  rules, templates) **rehydrates from its persisted store**; market / indicator state **rebuilds from the
  clean-historical** store, *not* the short-retention event log (ADR-0001 — "rebuild = reprocess clean-historical");
  ingestion **backfills gaps on reconnect** (R-1). Rehydration **preserves per-user isolation** (R-20). An
  **explicit startup pass** now makes this concrete (**implemented gh#221**): `Api/Recovery/DecisionStateRehydrator`
  reads the whole decision surface back **inertly** — it *observes* what returned (staged orders, pending
  conditionals, hidden stops, active suggestions) and resumes **none** of it (every resumption path stays request-
  or quote-driven and re-validates against fresh truth, R-12). It reads across all owners (background plumbing
  bypasses the R-20 filter) yet **carries ownership** on every row, and if a crash mid-write left an **impossible
  cross-entity combination** — a staged order holding a venue key, a fired conditional linked to no order, a native
  (at-venue) stop with no live order, a stop plan whose owner drifted from its order's — the pure
  `Domain/Recovery/DecisionStateRehydration.Analyze` flags it and the pass **fails safe and loud**: it engages the
  **kill switch (HaltOnly — no new orders; open positions rest on their native safety stops)**, persists the durable
  lock, and alerts (`synthetic_risk`), **never silently repairing**. These are **cross-entity** invariants a
  single-row DB check cannot express — a crash between two independent writes leaves each row valid but the whole
  contradictory — and an existing operator kill-switch lock is **preserved**, never downgraded.
- **No-risk state fails safe by expiring.** A **suggestion carries no risk** (nothing at the broker until taken). On
  any recovery, suggestions apply their normal lifecycle: past the **validity window** or with broken drift / thesis
  → **stale → expired / void**; a survivor must still pass **R-12** before it can be taken; **nothing is auto-taken or
  silently resumed**; a re-formed setup is a **new** suggestion (R-4). Same for a **pending conditional order** (its
  cancel-if / expiry, R-11).
- **At-risk state is protected independently of client and app.** A live position always has a **native, exchange-held
  safety stop** (ADR-0007) — it survives *any* app / backend / connection failure. On **venue-connection loss**, in-app
  **synthetic** orders (hidden entries, un-promoted stops) go **orphaned → emergency** with an operator alert; on
  reconnect the system **re-validates and re-arms** — nothing silently resumes (ADR-0007; **implemented gh#209** —
  a connection-liveness monitor over the `IVenueConnection` seam orphans hidden working stops on a drop; and
  **gh#191** — re-arm now **re-validates each stop against venue truth first**, re-arming only a still-open position
  and **retiring** the stop of one that closed during the outage, so the invariant below genuinely holds rather than
  leaning on the venue to reject a stale promotion; the operator alert is a high-severity log carrying
  `synthetic_risk` until the Phase-4 SPA channel and the formal `AuditRecord` land).
- **The hard case — the auto-flatten guarantee (R-13).** The auto-flatten is **our system feature**, fired at a
  **configurable, per-instrument deadline** (equity-index default ~2:30 PM CT ahead of MOC; **crude / gold settle earlier**) — **earlier than any venue-forced flatten** (Topstep
  ~3:10 PM CT), and a **live brokerage has none**, so we **cannot lean on the venue** as the net. It is
  **safety-critical and must fire even if the primary tier is degraded** → a **redundant / independent trigger** (a
  watchdog separate from the main scheduler), a defined behaviour if a flatten order is *rejected* near the deadline,
  and possibly a **local fallback flatten path**. Still the **one piece to design in full**; until proven on practice
  it is the **gating risk** for live trading. See [market sessions & settlement](../wiki/pages/market-sessions-and-settlement.md).
- **The end-of-day / settlement boundary (R-13 companion).** The CME's **daily maintenance / settlement**
  (~4:00–5:00 PM CT) **re-marks** any position carried through it at the **settlement price** — the mark on return
  isn't the last trade seen. So end-of-day handling **leans on resiliency + fail-over, not on a live price**: on any
  disconnect / restart near the close, **reconcile positions / fills from the venue as source of truth** (never local
  state), stay **aware of the maintenance window**, and **reconcile the settlement re-mark** rather than presenting a
  stale pre-settlement mark as live.

**Principles (invariants).** Never present **stale data / risk state as live** (R-19); never **auto-act on rehydrated
state** (re-validate first, R-12); keep **no-risk state (expire) separate from at-risk state (protect + recover)**;
the **exchange-held stop is the physical floor**; every recovery transition is **audited** (§9, ADR-0007).

## Alternatives considered
- **Hold critical state on the client.** Rejected — the client is presentation-only and unreliable (offline, evicted,
  asleep); safety can't depend on it (ADR-0010 / ADR-0011).
- **Auto-resume suggestions / orders after an outage as-was.** Rejected — acting on unmonitored, possibly-stale state
  is unsafe; **expire + re-validate** instead.
- **Trust the venue to hold all protection.** Rejected — synthetic orders are in-app by design (to hide entries); the
  native safety stop + orphan handling cover the gap.
- **A single durable scheduler for auto-flatten.** Insufficient alone — a tier outage takes it down too, and the
  flatten fires **ahead of the venue's forced-flatten backstop** (so the venue can't cover a miss); hence a
  **redundant / independent** trigger (to be designed).

## Consequences
**Positive** — one coherent, auditable recovery story; client failures are non-events; proposals fail safe; live risk
is protected server / exchange-side; the hard problem (auto-flatten guarantee) is **named and isolated**, not buried.
**Negative / costs** — the **auto-flatten watchdog / fallback is real safety-critical engineering** (high-rigor suites,
§5 / §9) and **gates live trading**; **state rehydration needs deterministic-eval coverage** (incl. cross-user
isolation on rehydrate); the **expire-on-uncertainty bias** discards some still-valid setups after an outage
(accepted — they re-form as new suggestions).

## Follow-ups
- **The auto-flatten guarantee** (R-13): both tiers are **implemented** — the **primary scheduler** (gh#185), the
  supervised DST-aware host that fires at each instrument's **configurable pre-MOC deadline** verifying against venue
  truth, and the **redundant / independent watchdog** (gh#187) on its own **separate** loop, which backstops the
  primary's failures past a grace window, **persists** on a rejected close rather than giving up, and escalates to a
  **critical** alarm past the firing window rather than firing blind — so the flatten still fires when the primary
  tier is degraded. Respects a deliberately-disabled market (the operator's own-risk override). Remaining as
  follow-ups (not the gating spine): an **opposing-market-order** alternative close if the venue's own close
  primitive keeps rejecting, and a **client-side local fallback** (a future PWA, ADR-0010 — flattens when the whole
  cloud tier is unreachable). **Prove on practice before live.**
- **End-of-day / settlement reconciliation:** **the settlement boundary is handled (gh#193, 2026-07-25).**
  `Domain/Flatten/MarketSession` derives the per-instrument settlement / maintenance window from the instrument's
  session close on the DST-aware `MarketClock`; `Api/Recovery/PositionReconciliationService` (and `GET
  /accounts/{id}/positions`) reports positions from **venue truth** tagged with a `PositionMarkBasis` — `Live`,
  `Settlement` (a re-mark, never read as live movement, R-13), or **`Unknown`** when the venue cannot be reached
  (declared-unknown, fail-safe, never a stale live view — R-19). *Still open:* firing the reconcile automatically
  on reconnect (the connection monitor is gh#209; restart rehydration is **gh#221**), **fill**-level reconcile
  (needs the account-event seam **gh#219**), and per-position mark precision. Wiki:
  [market sessions & settlement](../wiki/pages/market-sessions-and-settlement.md).
- **State rehydration (gh#221, 2026-07-25):** the explicit startup pass is **implemented** —
  `DecisionStateRehydrator` brings the decision surface back inertly, preserves ownership (R-20), and fails safe to
  no-new-orders (kill switch, HaltOnly) + loud on any impossible cross-entity combination, never repairing. *Still
  open:* the **suggestion validity-window recompute** rides on the R-4 validity field when it lands (today the
  persisted state returns and the **take** re-gates, R-12); **restart-triggered venue reconcile** pairs with the
  settlement pass (gh#193) and the connection monitor (gh#209); **fill**-level reconcile needs the account-event
  seam (gh#219); and the **cross-user-isolation-through-restart** proof (suggestions / orders / positions /
  templates keep their owner) is the QA suite's.
- **Reconnect / backfill** verification (R-1 gap detection) and **recovery event / audit** records (ADR-0001, §9).
- Client **resume** edge cases (dedup, ordering) under the SignalR idempotent-resume pattern.
