# ADR-0019: Alerting — Pushover behind a channel seam, three layers, and a dead-man's switch

**Status:** Accepted · **Date:** 2026-07-25 · **Deciders:** Adam (operator)
**Extends:** [ADR-0002](0002-observability.md) (observability) — closes its open follow-up, *"Alert channel +
thresholds for the on-call-of-one (auto-flatten reliability must page)"*. ADR-0002's collection decisions all stand.
**Relates to:** [ADR-0013](0013-failure-recovery-model.md) (failure & recovery),
[ADR-0015](0015-distribution-licensing-governance.md) (integrations behind swappable seams),
[ADR-0010](0010-progressive-web-app.md) (web push, previously blocked on this decision); engineering §7, §9;
PRD **R-13**, **R-11**, **R-19**, §7 hard criteria, P1, **Q-12**. Issues: `gh#242`, `gh#244`, `gh#243`, `gh#245`.

## Context

Engineering §7 asserts that *"auto-flatten reliability is a monitored, **alertable** metric — it must page"*, and
then carries the open **`Decide:`** this ADR closes: *the alert channel for an on-call-of-one, and thresholds*.
Nothing defined what "page" meant, and three issues (`gh#231`, `gh#232`, and — via ADR-0010 — web push) named the
unmade decision as their blocker.

Three facts shape the answer:

1. **There is no outbound notification path of any kind today.** No SMTP, no SignalR hub, no webhook, no push.
   Every alert is an `ILogger` line to stdout plus a row in an `Events` table with sub-24-hour retention. R-11
   requires the operator be *"alerted immediately"* on a connection drop; R-13 requires flatten warnings and
   confirmations to reach an operator *away from the desk*. Neither was possible.
2. **The reaction budget is short, and shortest where there is no backstop.** ES/NQ flatten at ~14:30 CT with
   Topstep's forced flatten at ~15:10 — 40 minutes, and only on a prop account; a live brokerage has none. **CL
   (13:15 → ~13:30 settlement) and GC (12:15 → ~12:30) have ~15 minutes and no backstop at all.**
3. **The signals repeat.** While exposure persists the primary scheduler re-emits roughly every 15 s
   (`PollIntervalSeconds`) and the watchdog every 20 s. A 30-minute outage produces ~120 primary events alone, so
   deduplication is a correctness requirement, not a refinement. PRD **Q-12** asks exactly this — *"alert taxonomy
   and noise budget (max alerts/hour; measuring alert fatigue)"*.

## Decision

### 1. The channel: **Pushover**, behind `INotificationChannel`

An on-call-of-one needs a **pager**, not a notification. Pushover's **Emergency priority (2)** repeats until
*acknowledged* and bypasses silent mode / Do Not Disturb — the entire point of PRD P1's "away from the desk". It
has native receivers in Grafana/Alertmanager *and* in external cron-monitors, so no glue code, and costs a
one-time ~$5 per platform with no subscription, consistent with ADR-0015's operator-supplies-their-own-keys model.

ADR-0015 requires that *"new integrations go behind an interface, never inline"*, so this lands as an
**`INotificationChannel` seam with a Pushover adapter** — the seam name `gh#100` already proposes for Discord.
**This inverts that dependency:** `gh#100` sits behind an unbuilt suggestion engine, so alerting establishes the
seam and Discord becomes a second adapter, as does ADR-0010's web push. Credentials via the Options pattern from
environment, never source.

### 2. Three layers, because a system cannot alert on its own death

| Layer | Mechanism | Covers |
|---|---|---|
| **1. App direct push** | `INotificationChannel` → Pushover | The app knows *now*. Fastest — a Prometheus scrape plus a rule evaluation costs tens of seconds, and CL/GC have ~15 minutes total. |
| **2. Rule engine** | Alertmanager/Grafana on the LGTM stack | Conditions the app cannot self-report (alive but stuck), and a **backstop** with a longer `for:` so the direct push wins the race. |
| **3. Dead-man's switch** | App **checks in** to an external monitor on separate infrastructure; it pages when a check-in **fails to arrive** | **The app being dead.** |

Layer 3 is the load-bearing one. The worst R-13 failure is the host dying before the deadline: the flatten never
fires **and nothing alerts**, because Alertmanager and the in-process watchdog die with it. ADR-0013 already made
this argument about schedulers — *"a single durable scheduler … insufficient alone — a tier outage takes it down
too"* — and it applies unchanged to the thing meant to report that the scheduler failed. **Layer 3 inverts the
failure mode: silence becomes the alarm.**

A check-in asserts *"the deadline has passed and venue truth reports no exposure in this instrument"* — **not**
"I closed something". Two consequences follow, and both matter:

- **A flat session still checks in.** Otherwise every quiet day pages, the operator disables the monitor, and the
  most important safety net in the system is off.
- **No exchange calendar is required.** A holiday has no exposure, so the market is trivially flat and checks in.
  *Which* days a check-in is expected is the monitor's schedule to hold, not this system's.

A market with auto-flatten **disabled** (R-13's deliberate, warned override) reports *not applicable* rather than
flat: the switch declines to give an all-clear it is not entitled to give, and the operator pauses that market's
monitor check.

### 3. Thresholds

Two of these contradict the obvious design, and both come from the timing above:

- **Do not page on `flatten.missed`.** It fires at `deadline + FiringWindow` (60 min) = 15:30 for ES — **20
  minutes *after*** Topstep's ~15:10 backstop would already have closed a prop account. The actionable signal is
  **`flatten.escalated`**, emitted at deadline + seconds once the primary exhausts `MaxFlattenAttempts`.
- **Do not wait out the watchdog's 2-minute grace before paging.** If the primary escalated, three closes failed
  against the venue — and the watchdog uses the same venue. On CL/GC that grace is 13% of the entire margin, spent
  on a retry unlikely to differ. Page immediately; a watchdog save 2 minutes later **resolves** the alert. An
  alert that resolves itself is a good outcome; a page that arrives two minutes late is not.

**P1 — page** (Pushover Emergency, `retry=60s`, `expire=1800s`, bypasses DND, never quiet-hour suppressed):
exposure remaining after the primary's close attempts (`flatten.escalated`) · exposure past the firing window
(`flatten.missed` / `flatten.watchdog.critical`) · **the daily check-in absent by `deadline + 5 min`** · **the
liveness heartbeat missed ≥ 3 intervals during market hours with a position open** · an open position with no
native safety stop (impossible by construction under ADR-0007's fail-closed rule — if observed, the invariant
itself has broken, and PRD §7 calls a residual position past the deadline the system's only **Sev-1**).

**P2 — notify** (Pushover normal, single push, suppressed outside 06:00–17:00 CT): `flatten.watchdog.saved` (a
tier is broken — fix before the next session) · `flatten.watchdog.rejected` · `flatten.disabled` with exposure ·
`flatten.unconfigured` with exposure · `flatten.warning` (**the flatten warning PRD P1 asks for**) · connection
lost > 2 min with a position open · `killswitch.engaged` · a retention gap (`gh#227`) · `time_to_flat` p95 > 60 s.

**P3 — quiet push** (Pushover priority −1: delivered, no sound): `flatten.executed` — **the flatten confirmation
PRD P1 asks for**, at zero fatigue cost.

**Dashboard only, never pushed:** gate pass/block/resize counts, order-ack latency, pipeline lag, runtime and RED
metrics.

### 4. The noise budget (answers PRD Q-12)

**Alertmanager sends a P1 once per incident; the repeat-until-acknowledged nagging is Pushover's job.** Both
repeating is double-nagging, and a double-nagging pager gets muted — at which point it is worse than no pager,
because it produces false confidence. `group_wait` 30 s, `group_interval` 5 m, `repeat_interval` 4 h; P2
deduplicated to at most one push per instrument per session. **Budget: ≤ 3 pushes/hour under any single sustained
fault, and 0 pushes on a clean session** (P3 confirmations excepted — silent by design). **Alert fatigue is
measured**: pushes per session, by severity, on the dashboard. A rule that pages on a clean session is a defect in
the rule, not noise to tolerate.

## Alternatives considered

- **Telegram.** Free, reliable delivery, trivial bot setup. Rejected: ordinary notifications, with no
  repeat-until-acknowledge and no dependable DND bypass — it fails the wake-me-at-14:30 requirement that is the
  whole point of P1.
- **Discord** (reusing `gh#100`). Rejected as the *pager*: easily muted, no acknowledgement loop, no DND bypass.
  Fine for P2/P3 later, as a second adapter behind the same seam.
- **ntfy, self-hosted.** Best fit for the fork-first ethos and genuinely tempting. Rejected for now: hosting the
  pager on the infrastructure it monitors reintroduces the shared-failure problem Layer 3 exists to remove, and
  iOS support is weaker. Remains the natural swap for a fork — which is what the seam is for.
- **PagerDuty / Opsgenie.** Real paging with real escalation policies, and disproportionate for one person.
- **In-system alerting only (no Layer 3).** Rejected: it cannot alert on its own death, which is precisely the
  R-13 failure that matters. This was the decision's crux.
- **Per-instrument alert thresholds tuned to each margin.** Considered, because CL/GC have ~15 min against ES's
  40. Rejected as unnecessary: paging on `flatten.escalated` fires at deadline + seconds for every instrument, so
  the tightest margin is already served without a per-market knob to get wrong.

## Consequences

**Positive**
- The operator is reachable away from the desk, which R-13 and R-11 both require and neither previously had.
- **The failure that produces silence now produces a page.** Layer 3 is the only mechanism in the system that
  survives the host dying.
- `gh#231`, `gh#232` and ADR-0010's web-push follow-up are unblocked.
- The channel seam is established early, so `gh#100`'s Discord adapter and web push become implementations rather
  than parallel paths.

**Negative / costs**
- **A third-party dependency on a safety path.** Mitigated: alerting is failure-tolerant and off the hot path — a
  channel failure is logged and never fails or delays a trading action.
- **An external account and a second piece of infrastructure** the operator must provision, and which a fork must
  provision too. It is in the deploy checklist because a deployment without it is silently missing its most
  important safety net.
- **Check-in URLs are capability URLs** — secrets, environment-only, never logged.
- **This is visibility, not self-healing.** A page still needs a human with a phone. ADR-0013's remaining
  follow-up — a client-side local fallback flatten when the cloud tier is unreachable — is the only thing that
  closes that gap, and it is Phase 4.

## Follow-ups

- **Operator, once — now written up in the [runbook](../deployment-runbook.md) §*The dead-man's switch*:** create the
  Pushover application and the external monitor checks, on **independent infrastructure**; supply both via
  environment; **pause the monitor check for any market whose auto-flatten is deliberately disabled**, or its absent
  check-in will page; and **verify it pages** by stopping the app before a deadline — an unverified dead-man's switch
  is an assumption, not a safety net.
- **`gh#244` — Layer 3 landed with this ADR.** `Domain/Flatten/FlattenCheckIn` decides when the system has earned the
  right to report flat (withholding while exposed, reporting on a quiet day, declining for a disabled market);
  `Api/Flatten/DeadMansSwitchHost` drives the daily per-instrument check-in and the liveness heartbeat;
  `HttpDeadMansSwitch` pings the monitor and is failure-tolerant by construction — a transport fault returns false
  rather than throwing, because a report that did not arrive is exactly what should page. **No exchange calendar was
  needed** after all: a holiday has no exposure, so the market is trivially flat and checks in.
- **`gh#243` — Layer 1 landed, and has been rebuilt three times since.** The seam is
  `Domain/Notifications/INotificationChannel` — transport-free (severity / title / body / incident key; no priority
  numbers, no thread semantics) — with `PushoverNotificationChannel` mapping **Page → Emergency (2)**, Notify → 0,
  Quiet → −1, and cancelling an outstanding page via its receipt when the incident resolves.
  `NullNotificationChannel` keeps an unconfigured deployment booting while logging a Page as an **error**, so
  unmonitored is never silent. The three P1 flatten conditions (`flatten.escalated`, `flatten.missed`,
  `flatten.watchdog.critical`) raise; a successful flatten, a watchdog save, or any pass that observes the
  instrument flat **resolves**.

  **The chain today is `outbox → relay → queue → dedup → transport`**, with two ways in that want opposite
  things. `SendAsync` commits through **its own scope**, so an unrelated failure in a producer's unit of work cannot be
  swallowed by the never-throw contract and silently eat a page. `Enlist` stages the row in the **caller's**
  `DbContext`, so intent and state change commit atomically and the commit→enqueue gap does not exist at all; it
  checks for an already-owed incident before staging, because a row that failed the constraint would fail the
  *producer's* save. Both auto-flatten tiers take the second path.

  **Five invariants survive every rebuild**, and are what the composition guards exist to protect:

  - **`SendAsync` returning true means *accepted for delivery*, never *delivered*.** A caller on the R-13 path
    cannot wait for delivery without reintroducing the latency defect the queue was built to remove.
  - **The guarantee is *no dropped page*, not exactly-once.** Delivery is attempted first and the row stamped
    after, so a crash between the transport accepting and the stamp landing **re-delivers** rather than loses.
    Stamping first would close that duplicate window and open a worse one — a page marked sent that never went.
    The direction is chosen, not accidental: a repeated page is an annoyance, a missed one is the failure R-13
    exists to prevent. Across passes a **still-owed** incident is idempotent — not because the dedup key is the
    row's key (it is **not**; the key is a surrogate `Id`), but because the `DedupKey` unique index is **filtered to
    `DeliveredAt IS NULL`**, so a re-raise while a page is outstanding collides and is refused. Once delivered the
    row releases its slot and the same incident can be recorded again, which is the point of gh#458 below.
  - **Re-arm is unconditional; only the resolve is retried.** Re-arming is local state whose failure mode is a
    duplicate page (safe), where withholding it suppresses the *next* genuine incident as a stale duplicate
    (silent). A failed *send* is already re-driven by dedup declining to record an incident it could not report,
    so retrying it here too would double-page. After `QueuedNotificationChannel.MaxResolveAttempts` (3) a cancel
    is abandoned and the page left to expire — unbounded retry would starve a single-reader pump, including a
    page for a *live* incident, which is far worse than a stale nag.
  - **The protection lives in the binding, not in the producers.** `AutoFlattenService` absorbs a channel that
    *throws*, not one that *hangs* — so binding a transport directly to the seam silently returns an unbounded
    await to the R-13 hot path with every existing test still green. Hence
    `NotificationRegistration.AddTradingCopilotNotifications` and its assertions: the seam resolves to the outbox,
    the relay and pump are registered (a queue nobody drains accepts every page and delivers none), and the dedup
    chain is a **singleton** (a scoped one forgets the open incident and re-pages every poll).
  - **The relay must drain into the chain *below* the outbox, never into the seam itself.** The fifth invariant, and
    the one learned the hard way (`gh#459`, below): with the outbox the only `INotificationChannel` registration, a
    plain `AddScoped<NotificationOutboxRelay>()` hands the relay *the thing it drains* — it then drains the outbox
    into itself and stamps every page delivered having reached nothing. So `delivery` is bound to the **concrete**
    `QueuedNotificationChannel`, and a composition guard
    (`ShouldGiveTheRelayTheChainBelowTheOutbox_NotTheSeamItDrains`) asserts it — a relay built by hand in a unit test
    cannot see this, because the defect is purely in the wiring.

  **One amendment to this ADR's own decision:** dedup was specified "in the adapter"; it landed one layer out as
  `DedupingNotificationChannel`, so every future adapter (Discord `gh#100`, web push ADR-0010) inherits it rather
  than reimplementing it, and it is unit-testable without a transport. The requirement is unchanged — one push per
  incident, re-armed on resolve.

  **Changelog.** Every entry is a defect this design has already paid for; the detail lives in the issue, and in
  the dated Update below where one exists.

  | | |
  |---|---|
  | `gh#289` | an inline send put a slow channel's full latency on an R-13 flatten pass (5 s channel → 5.15 s pass) → **queue → dedup → transport** |
  | `gh#320` | that fix bounded nothing on its own — the guarantee is a property of *what is bound to the seam*, so the binding was extracted and asserted |
  | `gh#300` | the receipt was surrendered *before* the cancel was confirmed, so a failed cancel kept nagging about an already-flat position and nothing could retry it |
  | `gh#400` / `gh#437` | *accepted into memory* was lost outright on a crash, after the caller had been told it succeeded → the **outbox** and its relay |
  | `gh#459` | **P0** — and the sharpest illustration of why this list exists. gh#437's relay was registered with a plain `AddScoped<NotificationOutboxRelay>()`, so DI resolved its `INotificationChannel delivery` from the only such registration: **the outbox seam it drains**. The relay drained the outbox *into itself* — `Enlist` short-circuited on the row it had just loaded (same scope, same `DbContext`), returned `true`, and **every page was stamped `DeliveredAt` having reached nothing**, the R-13 auto-flatten escalation included. Silent from every side: no throw, no backlog, just a table filling with pages nobody received. Fixed by binding `delivery` to the concrete `QueuedNotificationChannel`, guarded at the composition root |
  | `gh#452` | `SendAsync` sharing the caller's `DbContext` let a producer's unrelated failure be swallowed and eat a page |
  | `gh#455` | the safety-critical producers **enlist** rather than send — Update below |
  | `gh#458` | the dedup key outlived its incident at the **outbox** — Update below |
  | `gh#497` | …and at the **decorator** too: resolve fired only on the flatten's own success, so an exposure ended by any other hand left the key armed forever — Update below |
- **`gh#245`** Alertmanager rules and routing (Layer 2) · **`gh#246`** the QA suite.
- **Thresholds are recorded but not yet enforced.** The P1/P2/P3 tables above describe what `gh#245` must build; only
  the dead-man's switch's own rules (check-in absent by deadline + 5 min, heartbeat missed ≥ 3 intervals) are live
  today, and they live in the **monitor's** configuration rather than in this repo.
- **Confirm the deadlines against the CME rulebook.** The times here inherit the flatten schedule's own caveat
  that they are operator-provided reference times, not verified exchange data.
- **Escalation beyond one person.** Out of scope while the operation is an on-call-of-one; revisit if that changes.

## Update (2026-07-28) — Layer 1 is live: Alertmanager + Pushover (gh#245)

The thresholds above stopped being "recorded but not yet enforced". The in-system rule engine is
**Alertmanager**, on the gh#231 stack, behind the opt-in `observability` compose profile.

**Alertmanager rather than Grafana unified alerting** (gh#245 asked for the choice and its reason): this ADR's
noise budget is written in Alertmanager's own vocabulary — `group_wait`, `group_interval`, `repeat_interval`,
inhibition — so implementing it there is a transcription rather than a translation; rules stay **plain files** a
PR can review and `promtool test rules` can **execute**; and rule evaluation sits next to the data Prometheus
already holds. The cost, stated: one more container. Accepted, because it is the component whose absence would
make every other guarantee here untrue.

**The noise budget is enforced, not aspirational.** `group_wait` 30 s, `group_interval` 5 m, `repeat_interval`
4 h. P2 is delivered only inside 06:00–17:00 CT via an `active_time_intervals` window on `America/Chicago` (so it
follows DST rather than drifting twice a year); **P1 carries no time restriction at all** — a 03:00 page is the
entire point. Three inhibitions keep one incident to one page: P1 suppresses same-component P2, a silent
telemetry pipeline suppresses the flatten rules it has blinded, and `FlattenEscalated` suppresses the
`FlattenMissed` that follows it 60 minutes later.

**The backstop contract with the direct push** (gh#242) is implemented as a `for: 2m` on every flatten P1 —
longer than the direct push's latency, so the fast path wins the race and this layer fires only when the app was
alive but not self-reporting.

**A clean session pages nobody, and that is a test.** `observability/rules/tests/` holds `promtool` rule tests,
and the first fixture is an ordinary day — idle evaluations, a normal flatten — asserting **zero** P1 and zero P2.
That criterion is now executable rather than argued, and — since CI runs `promtool check rules` + `test rules` on every PR (gh#585, `scripts/check-alert-rules.sh`) — **enforced**, not merely hoped, as rules are added: a rule with no test, or a test drifted from its rule, fails the PR.

**What was deliberately NOT built.** Several conditions this ADR names have no instrument behind them yet:
*open position with no native safety stop*, `flatten.watchdog.rejected`, `flatten.unconfigured` distinct from
`disabled`, `flatten.warning`, and *connection lost > 2 min with a position open*. Writing rules against invented
metric names would produce rules that never fire and read as healthy — **the exact failure this ADR exists to
close**. They are listed in the rules file and tracked as a follow-up for the Coding Agent; the instrumentation
comes first.

**Credentials are files, not `${VAR}`.** Alertmanager has **no environment-variable expansion in its config** —
a trap, because nearly every other component in this stack does. `${PUSHOVER_API_TOKEN}` written there is sent to
Pushover as that literal string: the config loads, the stack looks healthy, and every page fails authentication.
The receivers therefore use `token_file` / `user_key_file` / `url_file`, and compose materialises those files from
`.env` values through a `secrets:` block with an `environment:` source — one place for the operator to configure,
nothing secret in the repo.

*Still open:* the conditions listed above, once instrumented. *(The gh#306 backfill **shortfall**, also listed here
originally, landed in gh#482 — see the update below.)*

## Update (2026-07-28) — every named condition is now instrumented and ruled (gh#370)

The gh#245 update above closed with a list of conditions this ADR names that **no instrument emitted**, and said
they were listed in the rules file rather than written as dead rules. That list is now empty.

**What was added, and why each was missing.** Three were already *journalled* and simply never metered —
`flatten.warning`, `flatten.unconfigured`, and the watchdog's rejected close — so the events existed and only the
signal did not. The other two needed new measurement:

- **`trading.positions.unprotected`** — the P1 this ADR names first, and the one with no near-equivalent.
  `trading.stops.orphaned` was the closest existing metric and counts a *different* thing: a stop that **was**
  venue-held and was orphaned on a drop, which is a known and handled transition. This counts exposure with
  nothing behind it **at all**, including cases nothing ever recorded. A new `ProtectionMonitorService`
  reconciles venue positions against venue working orders every 30 s to produce it.
- **`trading.venue.connected`** — a **gauge, re-stated every pass**, not a transition event. That is what lets
  *"connection lost > 2 min"* live in the rule's `for:` rather than as elapsed-time bookkeeping in application
  code; a gauge written only on change is indistinguishable from one that stopped being written.

**Venue truth on both sides, deliberately.** The protection census consults no local state. Every local record of
protection — a `StopPlan`'s staging, an order row — is a *belief* about the venue, and this measurement exists to
catch a wrong belief; reconciling against our own records would make it agree with itself and detect nothing.

**Unknown is not zero.** If the venue cannot be read, the census is not published at all. A stale or invented zero
reads as *"nothing unprotected"* — an outage becoming a false all-clear on the one metric this ADR pages on. An
unwritten gauge goes stale visibly, and the P1 on a silent telemetry pipeline covers that case.

**`unconfigured` split from `disabled`.** Deliberately switched off and never configured are different operator
errors and only the second is a surprise; folding them together meant a live position in an unconfigured product
could not be alerted on at all. Likewise the watchdog's **rejected** close is now distinct from **escalated** —
escalation means attempts *exhausted*, rejection means one attempt *bounced*, and conflated they made a single
bad close look like the tier giving up.

**A rule whose name lied is fixed.** `FlattenDisabledWithExposure` promised *"with exposure"* and checked no such
thing; the new open-position gauge makes the name true, so a disabled deadline on a flat book — a configuration
choice, not an incident — no longer notifies.

**The clean-session fixture still asserts zero P1 and zero P2**, now with the new series present and the new
rules included. That criterion has held across both increments, which is the only evidence worth having that the
noise budget above is real.

**The pages now lead somewhere.** gh#245's `runbook` annotations pointed at deployment-runbook anchors that did
not exist — a page linking nowhere is worse than one with no link. The four incident sections they name are
written (*when a page arrives*).

*Still open:* nothing in Layer 1 — the gh#306 backfill **shortfall**, the last item named here, landed in gh#482
(see the update below). The **daily check-in** (gh#244) stays deliberately outside this stack — Layer 2 is
external, and a dead-man's switch that ran here would die with the thing it watches.

## Update (2026-07-29) — dedup suppression expires with the incident (gh#458)

§4's noise budget is stated as *"once per incident"*, and the durable outbox (gh#400 / gh#437) enforced it in the
database — correctly in kind, wrongly in **lifetime**. `NotificationOutboxRecord.DedupKey` was the **primary key**,
while the relay marks delivery by stamping `DeliveredAt` and *keeping* the row. So a delivered row held its key
forever: the second occurrence of any incident could never be inserted, `SendAsync` logged *"it will NOT be
delivered"* and returned `false`, and nothing paged. **A repeat auto-flatten failure was unreportable precisely
because the first one had been reported successfully** — an alert path silently consumed by its own history, and
one that decays as the deployment ages rather than failing on day one.

**The fix separates identity from suppression.** A surrogate `Id` (time-ordered `Guid.CreateVersion7`, so the
relay's oldest-first read stays index-friendly) is the key; `DedupKey` carries a unique index **filtered to
`"DeliveredAt" IS NULL`**. Idempotence stays where this ADR wanted it — in the database, not in the relay
remembering to check — but scoped to the **open** incident:

| | before | after |
|---|---|---|
| re-raise while a page is **owed** | suppressed (PK collision, reported as failure) | suppressed (filtered index), and reported as **success** — it *is* recorded |
| re-raise after the page was **delivered** | **impossible to record — never paged** | recorded and delivered as the new incident it is |

**"Once per incident" means once per *open* incident.** The version this replaces read it as once per incident key
for the lifetime of the database, which is not a noise budget — a condition that recurs next week is a new
incident, and §4's own justification (a muted pager is worse than no pager) argues only against *simultaneous*
duplicates. `SendAsync` now also checks the store for an owed row before inserting, so the common cross-scope
re-fire returns success without a failed round trip; the index remains the backstop, and `Enlist` keeps its
synchronous `Local`-only check because it must not do I/O inside the caller's transaction.

**Verified against real Postgres, both directions** — including the migration over pre-existing alert history,
where each existing row is backfilled with a distinct id. The scaffolded migration would have defaulted every row
to `Guid.Empty` and failed `AddPrimaryKey`: green on an empty dev database, broken on the one deployment with
history. Its `Down` can legitimately fail once the outbox holds what the fix permits, and that is the intended
behaviour — refusing to roll back beats deleting delivered pages to make room for the old key.

## Update (2026-07-29) — the safety-critical producers commit their page with the state it reports (gh#455)

`Enlist` had existed since gh#400 and **nothing called it**. Both auto-flatten tiers still went through
`SendAsync`, which commits through a scope of its own — so the page was durable within *seconds* of the
escalation, but not *with* it. A crash in that window left `flatten.escalated` on the journal and the operator
never told, which is the one outcome the R-13 alert exists to prevent. Both tiers now enlist: the page joins the
pass's own unit of work and the journal append's `SaveChangesAsync` commits **both or neither**.

**A domain seam, not the concrete channel.** `Domain/Notifications/INotificationEnlister` carries the one method;
`OutboxNotificationChannel` implements it alongside `INotificationChannel`. The composition root registers the
concrete type **once** and aliases both interfaces onto it, which is load-bearing rather than tidy: `Enlist`
stages into the `DbContext` of whichever instance it is called on, so a second registration would stage the page
into a context nobody saves — and every test would still pass.

### What this cost to get right, and what it teaches about shared transactions

Sharing a transaction cuts both ways, and the first cut drew blood. Staged rows are the producer's problem at
commit time: an escalation on one pass left the incident owed, the next pass staged the same dedup key, and
gh#458's filtered unique index rejected it — **on the auto-flatten's journal write**, aborting the pass and losing
the `flatten.missed` entry entirely. The alert had become capable of destroying the record it was reporting on.
The existing flatten integration suite caught it; no new test was needed to find it.

So the rule this ADR now states: **nothing an enlister stages may be able to fail the caller's save.** The seam
checks for an already-owed incident before staging rather than leaving the index to catch it. That check is a
*read*, and the caller's change tracker cannot answer it — an earlier pass committed that row in a scope long gone
— so the seam is asynchronous. "Enlist does no I/O" was the wrong invariant; **"enlist never writes and never
commits"** is the right one.

The corollary is that the index's role differs by path. For `SendAsync` it is a sound backstop: the channel
catches its own failure and returns. For `Enlist` it is catastrophic, because the exception surfaces somewhere
else entirely. A constraint is only a safe backstop for whoever owns the transaction it fails in.

**Scope.** The two auto-flatten tiers, which are the R-13 path. `TriggerEvaluationService` deliberately keeps
`SendAsync` — its advisories are not safety-critical, and `SendAsync` remains the default for everything else.
The **orphan / synthetic-risk guard named in gh#455 was not converted, because it raises no notification at all**:
it publishes `trading.stops.orphaned` and the Layer 1 rules page on the metric. That is a sound design and this
ADR's §2 says so; the issue's assumption that it called the channel was simply wrong.

**One unrelated gap closed while here.** The flatten's `ResolveAsync` — the success path's incident cancel — had
never been wrapped in the never-throw belt its sibling send got in gh#243, so a throwing resolve aborted a pass
*after* the position had already closed. Found by repointing the existing "the channel throws" guard, which had
been asserting against a call the flatten no longer makes.

## Update (2026-07-30) — the backfill shortfall is a signal now, not a log line to go and find (gh#482)

The last item this ADR listed as open. gh#306 recovers a consumer's blind window from the clean-historical bar
store, and it is **best-effort by nature** — the store may simply not have bars for part of the window. That
failure was reported only as a `LogError` in one host, so nothing could alert on it, and this file's own
`EventLogRetentionGap` annotation ended with *"check the logs for a reported shortfall"*: an operator who has
just been woken, sent somewhere else to find out what actually happened. That sentence is deleted.

**`trading.stops.backfill_shortfall`** — a histogram, `ms`, labelled by contract. The `_count` is the "did it
happen" signal a rule reads; the distribution is how much of the window was uncovered. Named under `stops.`
rather than a data namespace deliberately: what the operator must act on is **stop protection**, and it belongs
beside `trading.stops.orphaned` on the dashboard. `BackfillShortfall` fires on
`increase(trading_stops_backfill_shortfall_milliseconds_count[15m]) > 0`.

**Emitted from `GapBackfillService`, not from the host that logs it.** The service is where the shortfall is
known, so every consumer of the backfill gets the signal rather than only the one host that remembered — and it
is unit-testable, which the `BackgroundService` is not.

**P2, argued rather than assumed.** A shortfall means hidden stops on that contract may have crossed their
promotion band unobserved and were not promoted — but the **native safety stop is still resting at the exchange**,
so this is a *degraded floor*, not an unprotected position. It is also discovered after the fact: no amount of
speed changes what already happened. §*Thresholds* reserves P1 for exposure with nothing behind it, and this has
something behind it. The runbook section says so explicitly, because "may be unprotected" in a summary line
invites the reader to assume the worst tier.

**Silent on a healthy recovery**, by construction: the emission loop runs over `Shortfalls`, which is empty when
coverage is complete. §4's rule — a rule that pages on a clean session is a defect in the rule — is satisfied by
the metric never existing rather than by a threshold chosen to hide it.

## Update (2026-07-30) — an incident ends when the exposure does, whoever ended it (gh#497)

The third and last instance of one bug: **suppression outliving the incident it describes.** gh#458 fixed it at the
outbox (a delivered row held its dedup key forever); this fixes it at the layer below, and the shape is worth
naming because it recurred twice.

`DedupingNotificationChannel` re-arms a key only through `ResolveAsync`, and `AutoFlattenService` called that on
exactly one path — `FlattenVerdict.Flat`, its **own** successful close. An escalation is by construction the path
where the close did *not* succeed, so after any escalation the exposure gets ended by someone else: the operator by
hand, or a prop firm's forced flatten. Neither re-armed anything. **Every subsequent escalation for that
account+instrument was then suppressed as a stale duplicate** — silently, with no TTL and no eviction, for the life
of the process. On a self-hosted daemon that is weeks, and whether tomorrow's page arrives depends on whether the
host happened to restart.

**The fix: being flat is the incident-over signal, whoever produced it.** A pass that observes an instrument with
no open position resolves its incident key — both when the account is entirely flat, and when it still holds other
positions but *this* instrument is clear (the partial case, which never reached the "nothing open" branch at all).
No extra venue round trips: the flat path needs no contract resolution, and the partial path already has the
product-root map.

**Why the fix is at the producer and not in the channel.** Two occurrences of one key with no resolve between them
are, at the channel, indistinguishable from a *continuing* incident — the flatten re-emits every ~15 s while
exposure persists, and since gh#458 the outbox admits each of those, so the dedup channel is the only thing
collapsing them. Releasing the key there on delivery would page ~120 times for one 30-minute exposure: §4's noise
budget, destroyed. The channel's suppression is correct; what was missing was anyone telling it the incident had
ended.

The gh#481 QA suite found this, and its pinned assertion **stays pinned** — deliberately. It raises the same key
twice with no resolve, which is the continuing-incident shape, so collapsing to one page is the intended answer
there. Its annotation now says so rather than calling it a defect.

## Update (2026-08-14) — a held account the venue roster does not report is metered, not paged (gh#527)

The primary auto-flatten tier gained a disposition (gh#527, [ADR-0013](0013-failure-recovery-model.md)): a held
account the venue's **live roster does not report** is recorded rather than skipped in silence — `flatten.unrostered`
on the journal, `outcome="unrostered"` on `trading.flatten.deadlines`. It belongs in the **"dashboard only, never
pushed"** tier above, **deliberately not paged**: the pass cannot read the account's exposure, so it cannot claim
risk, and paging on every ~15 s roster hiccup would spend §4's noise budget on a maybe-nothing. The alertable
signal is the metric — a *persistent* gap is a Grafana rule the operator writes, not a built-in page. (Only the
primary tier emits it so far; the redundant watchdog and the dead-man's switch are gh#850.)
