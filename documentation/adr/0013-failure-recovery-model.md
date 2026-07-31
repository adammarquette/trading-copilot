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
  leaning on the venue to reject a stale promotion; and **gh#220** — each transition is now written to the immutable
  **`AuditRecord`** carrying `synthetic_risk` (a secondary write that never fails the safety action), leaving only the
  **real-time** operator alert deferred: the interim alert is a high-severity log carrying `synthetic_risk` until the
  Phase-4 SPA channel lands, gh#222).
- **The hard case — the auto-flatten guarantee (R-13).** The auto-flatten is **our system feature**, fired at a
  **configurable, per-instrument deadline** (equity-index default ~2:30 PM CT ahead of MOC; **crude / gold settle earlier**) — **earlier than any venue-forced flatten** (Topstep
  ~3:10 PM CT), and a **live brokerage has none**, so we **cannot lean on the venue** as the net. It is
  **safety-critical and must fire even if the primary tier is degraded** → a **redundant / independent trigger** (a
  watchdog separate from the main scheduler), a defined behaviour if a flatten order is *rejected* near the deadline,
  and possibly a **local fallback flatten path**. **Both tiers are implemented** — the primary scheduler
  (**gh#185**) and the independent watchdog on its own separate loop (**gh#187**); see *Follow-ups* for what each
  does and what remains (an opposing-market-order alternative close, and a client-side fallback). It is **still the
  gating risk for live trading**: implemented is not proven, and the exit criterion is that it fires reliably on a
  **practice** account every session (PRD §9). See [market sessions & settlement](../wiki/pages/market-sessions-and-settlement.md).
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
- **The blind spot both tiers shared is closed (gh#244, 2026-07-25, [ADR-0019](0019-alerting-channel-and-thresholds.md)).**
  This ADR's own argument against a single scheduler — *"a tier outage takes it down too"* — applied just as much to
  the **alerting**: the primary, the watchdog and any in-stack rule engine all die with the host, so a host that died
  before a deadline flattened nothing **and said nothing**. The worst failure produced perfect silence. A
  **dead-man's switch** now inverts it: `Api/Flatten/DeadMansSwitchHost` reports each instrument flat to a monitor on
  **independent infrastructure** once its deadline has passed and venue truth confirms no exposure, plus an
  unconditional liveness heartbeat — and the monitor pages when a report **fails to arrive**. `FlattenCheckIn.Decide`
  withholds the report whenever exposure remains, so **silence is the alarm** exactly when it should be. A flat
  session still reports (otherwise every quiet day pages), and a deliberately-disabled market reports *not
  applicable* rather than an all-clear it is not entitled to give. **It makes the failure visible, not self-healing**
  — a page still needs a human with a phone, which is what the client-side local fallback above would change.
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
  open:* the **suggestion validity-window recompute** — its blocker is **cleared**: the R-4 validity field landed as
  `Suggestion.ExpiresAt` (gh#544), stamped by a pure policy and clamped to the market's auto-flatten deadline so a
  suggestion can never outlive the flatten. The **recompute itself** (the sweep that acts on it, on both the steady
  state and the startup path, so recovery and normal operation cannot diverge) is **gh#545**, which closes this item;
  until it lands the persisted state still returns and the **take** re-gates (R-12); **restart-triggered venue reconcile** pairs with the
  settlement pass (gh#193) and the connection monitor (gh#209); **fill**-level reconcile needs the account-event
  seam (gh#219); and the **cross-user-isolation-through-restart** proof (suggestions / orders / positions /
  templates keep their owner) is the QA suite's.
- **Reconnect / backfill** verification (R-1 gap detection) and **recovery event / audit** records (ADR-0001, §9).
- Client **resume** edge cases (dedup, ordering) under the SignalR idempotent-resume pattern.

## Update (2026-07-28) — the venue-truth read family gains a resting-orders sibling (gh#381)

This ADR's venue-as-truth reconcile had one HTTP read: `GET /accounts/{id}/positions` (gh#193). It now has two.
`GET /accounts/{id}/orders` reports the **working orders resting at the venue**, including the attached
protective bracket and its size, under the **same discipline**:

- the same **Live / Settlement / Unknown** basis vocabulary, so a caller has one way to judge how far to trust
  either payload;
- **declared-unknown on unreachable**, with the payload withheld rather than returned empty;
- the same **ADR-0015 credential-key guard** — a connection this process holds no credentials for is unknown, and
  the venue is not even asked;
- the same **R-20 default-deny**: an account not owned by the caller is *not found*, never "found but empty".

**One nuance worth stating rather than inheriting silently.** For positions the basis describes a *price mark* —
a settlement re-mark must never read as live movement. An order has no mark, so for this read `Settlement` means
the view was taken **inside the maintenance window**, when the venue's own book may be mid-transition. Same
vocabulary, and deliberately so; a second enum would be a second thing to keep straight for no gain.

**Why declared-unknown matters more here than anywhere.** The question this read answers is *"is protection
standing?"* — and for that question, **"we could not ask" and "nothing is there" are opposite answers**. Returning
an empty list for an unreachable venue would be the single most dangerous shape this endpoint could take.
