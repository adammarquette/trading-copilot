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

## Update (2026-08-15) — the same fact is dashboard-only in two tiers and a **page** in the third (gh#850)

The **watchdog** now emits the same disposition as the primary — `flatten.watchdog.unrostered` on the journal,
`outcome="unrostered"` metered under its own tier — and it sits in the same **"dashboard only, never pushed"** tier,
for the same reason: it cannot read the account, so it cannot claim risk.

The **dead-man's switch is deliberately the exception, and it pages.** Not by adding an alert — by **staying
silent**, which in this tier *is* the alarm (§*the inversion*). A check-in pass that cannot read every held account
now vouches for nothing at all, because the alternative it used to take was worse than silence: it reported the
instrument **flat** on an aggregate that excluded the unreadable account, telling the monitor everything was fine
while it could not see one of its own accounts.

So the same underlying fact — a held account missing from the roster — is **dashboard-only** where the tier merely
could not act on it, and a **page** where the tier would otherwise have made a claim it could not support. That
asymmetry is the point rather than an inconsistency: this tier's output is a promise to something outside the
system, and the noise budget in §4 does not buy the right to make a false one. The operator's lever if a stale row
pages nightly is to rediscover or remove it — the primary and watchdog journals name which account it is.

## Update (2026-09-02) — a venue socket that stops delivering is a **P2**, and both edges have hysteresis (gh#1051)

The Tradovate socket hosts had the shape this ADR exists to remove, one layer down. A connect that keeps failing,
or a `user/syncrequest` that keeps failing, backed off to its ceiling and logged at that cadence **forever**, with
no operator-facing signal at all — and the trading socket reports itself `Connected` throughout, because Tradovate
pushes `props` entity frames only to a socket that has *synced*. So a feed that was dead and a feed that was quiet
were indistinguishable to everything above, including the account-event stream that now carries real fills into the
ledger (gh#1069). "Visible rather than silent" again meant a `LogWarning` an engineer would have to go and find —
the same finding as gh#1045, on a different path.

**The new class: `tradovate.socket.degraded:{market-data|trading}` — P2, notify.** Raised by
`TradovateSocketConnectionHost` (shared, so both sockets and every future venue socket host inherit it) once the
socket has gone **two minutes without delivering**, and resolved once it has delivered continuously for two minutes.

- **Why P2 and not P1.** It sits with *connection lost > 2 min with a position open* — protection degraded, not
  lost. This host cannot read exposure, so it cannot claim risk, which is the same reason `flatten.unrostered` is
  not paged (§*Update 2026-08-14*). And the "false promise" argument that makes the dead-man's switch the exception
  (§*Update 2026-08-15*) does **not** apply, because the promise is withdrawn structurally rather than by alerting:
  `TradovateTradingSocketSync.IsSynced` is a readable fact, and `TradovateAccountEventStream` reports when it never
  rode a synced socket instead of letting its silence pass as a quiet account.
- **Two minutes, wall-clock — deliberately not a pass count.** The gap between passes grows with the backoff, so
  "three consecutive failed passes" is about fifteen seconds at the start of an outage and about three minutes at
  the ceiling: one rule meaning two different things, and only one of them is the number that decides whether an
  alert is noise.
- **The number is borrowed from §3, and borrowed short — mark it unverified.** §3's comparable entry is *connection
  lost > 2 min **with a position open***, and this host cannot read exposure, so it takes the figure without the
  qualifier that keeps that entry off a clean session. Whether two minutes clears Tradovate's own maintenance and
  disconnect behaviour is **not a fact this repository has established** — there are no credentials to observe it —
  and §4 is blunt that a rule firing on a clean session is a defect in the rule. It is a staging observation to make
  once credentials exist. The hysteresis argument below justifies a grace of this order without depending on it.
- **The all-clear has the same grace, and that is what keeps a flapping socket inside §4's budget.** Resolving on
  the first healthy pass lets a socket that recovers and fails every twenty seconds produce advise → resolve →
  advise indefinitely — a push per flap, however good the dedup is. Requiring sustained health makes a flapping
  socket **one continuing incident**, reported once, which is what it is. The cost is an all-clear two minutes late;
  for a P2 that is the cheap side.
- **The resolve is the load-bearing half.** `DedupingNotificationChannel` is a process-lifetime singleton that
  releases a key only through `ResolveAsync`, so a producer that never resolves turns *one notification per outage*
  into *one per process lifetime* — the first outage delivers and every later, independent one is suppressed as a
  duplicate. That is this ADR's own failure mode reproduced by a caller, and it is pinned by the shared host
  contract for both sockets rather than trusted.
- **And a resolve that returned `true` is not proof the key was released, so the close is provisional.** A producer
  sits above the whole chain — outbox → queue → dedup → transport — and `QueuedNotificationChannel` is a bounded
  channel with `BoundedChannelFullMode.DropWrite`. **Under any `Drop*` mode `TryWrite` discards the item and returns
  `true`**, so that class's own *"queue is full — dropped the resolve"* branch is unreachable, nothing is logged,
  and the producer is told the resolve was accepted. The key then stays armed for the process lifetime and the
  failure above happens anyway, with every layer reporting success. The socket hosts therefore treat a close as
  provisional and **re-arm the key once, before the first advisory of the next outage** — never on the retries
  within one outage, which would cost a push per pass. A redundant resolve is free: the decorator forwards
  unconditionally and a transport holding no receipt no-ops. *(The unreachable drop-logging in
  `QueuedNotificationChannel` is a defect in its own right — the queue is fullest exactly when a transport is
  wedged, which is when the flatten escalation is also enqueuing — and is gh#1077, not fixed here. It is fixed in the
  update below, which makes this producer-side belt redundant rather than wrong.)*
- **What counts as "not delivering" is everything that is not delivering.** A failed connect, an unmet obligation, a
  socket mid-attempt, an unrecognised state, a pass that threw. The first cut of this exempted the mid-attempt case,
  which meant a socket reconnecting faster than the grace — the venue closing shortly after `authorize`, or the
  client's silence-timeout loop — never accumulated an outage at all: precisely the reported-to-nobody state this
  class exists for. It does not pre-empt gh#1052, which is about getting a wedged socket *out* of that state.
- **What it deliberately does not report:** a socket that keeps delivering intermittently. Data is reaching the
  platform, and paging on a stuttering-but-live feed spends §4's budget on something the operator cannot act on.

**Quiet hours.** P2 is documented above as *suppressed outside 06:00–17:00 CT*, and this feed runs the overnight
Globex session. No quiet-hour suppression is implemented on the Layer-1 push today, so nothing is lost yet; when it
is built, **this class must be exempt** — a trading socket that stops delivering overnight is exactly when nobody
is watching a dashboard.

**Budget.** One push per outage per socket, zero on a clean session, and nothing at all in a deployment where
Tradovate is unconfigured — the host stands down before it can escalate. It **reports and never acts**: no socket
is torn down, re-authenticated or disabled on the host's own judgement (the propose-and-confirm posture ratified
in gh#722).

## Update (2026-09-02) — the delivery queue **refuses**; it never drops, and a refused resolve still frees the key (gh#1077)

`QueuedNotificationChannel` — the queue between every producer and this channel — was
`Channel.CreateBounded(256)` with `BoundedChannelFullMode.DropWrite` and a comment promising that *"a drop is
logged — silence is what this whole area is about"*. **Under any `Drop*` mode `TryWrite` discards the item and
returns `true`**, so both of its *"queue is full — dropped"* branches were unreachable: nothing was logged and
the caller was told the notification landed. Every path to the operator runs through this queue —
`AutoFlattenService` and its watchdog (via the outbox), `TriggerEvaluationService`, the venue socket hosts — and
a send reporting success while dropping the page is the worst shape a notification defect can take, because every
layer above then reports healthy. The update above worked around it producer-side for the socket hosts; the brace
belongs here.

**Where each half of the remedy lands, traced through the real chain rather than assumed.** The producers hold
`OutboxNotificationChannel`, three layers above this queue, and the two verbs reach it very differently:

- A **send** never travels from a producer to the queue at all. `OutboxNotificationChannel.SendAsync` writes a row
  and returns — and returns `true` for an already-owed row too — so the queue's send comes from
  `NotificationOutboxRelay`, and the relay is who receives the refusal. That is the right recipient: the relay owns
  the ledger, and it already treats `false` as *"the row stays owed and is retried next pass"*. The producer was
  told `true` meaning *durably recorded*, which was and remains true, so there is nothing for it to do. What the
  **operator** needs is carried by the metric and the P1 below, not by a return value.
- A **resolve** does reach the producer — `OutboxNotificationChannel.ResolveAsync` withdraws any undelivered row
  and then returns this queue's answer verbatim. **But every live producer discards it**
  (`AutoFlattenService`, `AutoFlattenWatchdogService`, `TriggerEvaluationService`); only the frozen Tradovate host
  reads it. That is not an argument for changing them — the flatten resolves the same key again on its next pass
  anyway — it is the argument for the fix below carrying the invariant *by construction* rather than by a return
  value nobody is currently reading.

**Three different `true`s already live in this chain, and this change adds no fourth.** The outbox seam's `true`
means *durably recorded*; this queue's `true` means *accepted for delivery*; only the transport's means
*delivered*. The defect was that the middle one was being returned when nothing had been accepted. Making `false`
reachable restores the existing vocabulary rather than overloading it — a reader must still not mistake any of the
three for the one below it.

**Refuse, rather than drop or block.** The three candidate policies are not equivalent for *this* payload:

- **A bounded wait** puts the transport's backpressure onto the caller, and the caller is the auto-flatten on the
  R-13 path. That is `gh#289` re-introduced as a smaller number, and this ADR's own rule is that alerting must
  never delay a trading action.
- **`DropOldest`** leaves `TryWrite` just as dishonest, and discards the wrong end: during an incident the
  *first* page is the one that matters and the later ones are repeats the dedup decorator would collapse anyway.
- **Refusing** is the only outcome that is *recoverable*, because the layer above the queue is the durable
  outbox: `NotificationOutboxRelay` already reads `false` as "the row stays owed and is retried next pass".
  Refusing hands the page back to the ledger that remembers it instead of destroying it.

The mode is `Wait` now, used **only** with `TryWrite` and never `WaitToWriteAsync` — so nothing blocks; `Wait` is
simply the only mode under which `TryWrite` reports a full queue at all.

**Pages and resolves get separate budgets, because their losses are not comparable.** A page is refused at 256 —
the original bound, unchanged — while 64 further slots stay reachable only by a resolve or the pump's own
cancel-retry. What *fills* this queue is pages: the escalation re-emitting every 15 s and the watchdog every 20 s
against a transport that is not draining. Without a reserve the resolve is precisely the item crowded out, and a
lost resolve is the unbounded failure of the two — §*the resolve is the load-bearing half*, above.

Sixty-four is sized against the number of **distinct open incidents**, the same handful that lets
`DedupingNotificationChannel` hold its incident set with no eviction policy. **That bound only holds because
repeats are collapsed, and the first cut of this did not collapse them.**

Do the arithmetic, because it decides the design rather than merely justifying it. `AutoFlattenService` resolves
*every configured instrument on every pass*, paged or not (`:246` flat, `:270` partial), and
`OutboxNotificationChannel.ResolveAsync` always forwards: four built-in instruments at a 15 s poll is **16 queue
writes per minute per account**. (The watchdog's resolve is conditional on a save, so it does not add to the
steady rate.) Against a wedged Pushover — 10 s `HttpClient` timeout — the pump drains about **6 per minute**. Net
fill ≈ 10/min for one account, so a 64-slot reserve is gone in roughly six minutes, and in about three with two
accounts.

**The alternative was to drop the collapse and raise the reserve, and the arithmetic is why that was rejected.**
A larger reserve buys time *linear in its size*: ~300 slots for a 30-minute wedge on one account, ~600 for two,
and several thousand for an overnight partition. Because the fill rate exceeds the drain rate for as long as the
transport is wedged, **no fixed reserve bounds the failure** — it only postpones it, and what it postpones is the
fallback path whose residues are documented below. Collapsing makes the queued-resolve count bounded by *distinct
open keys* — a handful — **independently of how long the wedge lasts**. That is a difference in kind, not in
constant, so the collapse stays and the number does not move.

So a resolve is not enqueued while one for the same key is already queued behind every page for that key: the
operation is keyed and idempotent, so the caller's `true` still means *accepted for delivery* — delivered by the
item already carrying it.

**"Behind every page" is decided by comparing ordinals, not by a flag, and that is the round-3 fix.** The first
cut asked whether a marker was *present*, and set it *after* the channel write — so a producer thread preempted
between the two could leave a resolve queued **ahead** of a page for the same key with the collapse still armed,
and the next resolve was then dropped: the page armed the key and nothing was left to close it. Permanent silence
on that key, reached through the optimisation. Enqueue, ordinal assignment and the bookkeeping now happen under
one short lock — no I/O, no awaits, so it is safe on the R-13 caller — which makes the enqueue ordinal agree with
the queue's actual order, and the collapse a comparison of two values that cannot go backwards. **Nothing in the
suite could express that race**, because every collapse fixture was single-threaded; there is now a concurrent one
that fails on it, and the decision rule is a pure function pinned by a theory over the orderings a race produces.

**And a refused resolve still releases the dedup key**, through a new `IIncidentKeyRegistry` that
`DedupingNotificationChannel` implements over that same incident set. The two halves of a resolve fail
differently: cancelling the outstanding page needs the transport, so it is queued and `false` asks the caller to
retry — which is `INotificationChannel`'s documented meaning for `false`. Releasing the key needs nothing but a
dictionary removal, and **nothing anywhere records that it is owed**: there is no outbox row for a resolve, and
`TriggerEvaluationService`'s staleness recovery resolves exactly once per outage and never returns. So the
release is done directly, on the caller's thread, at the one moment the queue has refused it — and *only* then,
because doing it on the ordinary path would move the re-arm off the single-threaded pump and let a concurrent
escalation slip past the suppression it is meant to meet.

**Releasing is not sufficient on its own, and the first cut of this got it wrong.** The dedup decorator *arms* a
key on a successful send, so a page still sitting in the queue when the resolve was refused re-arms the key the
refusal just released — and the resolve that would have cleared it is gone. The refusal therefore records the
ordinal of the **newest page already queued for that key**, and the pump releases the key once, after delivering
exactly that page. A page enqueued **after** the refusal never matches, so a new incident keeps its suppression —
releasing that one would page on every 15 s escalation pass, which is the noise §4 forbids, reached by way of a
guard against silence.

**The release has exactly one owner, and that is what keeps one incident to one push.** Two separate versions of
this got it wrong in the same direction, so the rule is worth stating as a rule: *while a page for the key is
queued, the covering page's delivery owns the release; with nothing queued that could re-arm the key, the refusal
path owns it.*

- Releasing after **each** covered page let every page beneath the refusal through the dedup it had just cleared,
  so a backlog for one key became one **Emergency** push per queued page.
- Releasing on **every refusal** was the same bug wearing a different hat, and is the one that survives a casual
  reading: the wedge refuses a resolve for that key on *every* 15 s flatten pass while the backlog drains at
  ~6/min, so each refusal released the key the previous page had just armed and the next page delivered
  unsuppressed. One push per refusal instead of per incident — the flood again, at a lower rate.

Both are §4's *"strictly worse than no pager, because it manufactures confidence"*. Bound this way the backlog
dedups to a single push **and** the key still ends released, which are both required: bounding the pushes by
leaving the key armed would trade the flood back for the silence this card exists to remove. The covering page's
bookkeeping runs in a `finally`, so a send that throws still hands the key back rather than stranding it.

**What that does not cover, stated rather than claimed away:** a page that *fails* delivery inside the refusal
window and is re-offered by the outbox later under a **fresh ordinal**. That is a genuinely new send, so the
marker is inert against it and the key stays armed until the producer resolves again. (A page merely *in flight*
when the refusal lands **is** covered — the bookkeeping is cleared in the delivery's `finally`, so the refusal
still sees it queued and defers to it. An earlier version of this update listed that case as a gap; it is not
one, and an unnecessary caveat teaches the next reader to distrust the accurate ones.) The remaining residue is
exactly what the socket hosts' producer-side re-arm (§*Update 2026-09-02*, `gh#1051`) covers, which is why it
**stays** — see the follow-up note below.

**A note on how these were found, because it is the reusable part.** Every defect in this change was one the test
suite was *structurally incapable* of expressing, not one it happened to miss: single-page fixtures hid the
re-arm, single-threaded fixtures hid the collapse race, and a fixture that refused **once** and then drained
uninterrupted hid the per-refusal flood — while a fixture that injected its second refusal from inside the
transport call hid it a second time, because that runs *before* the dedup layer arms and the scenario evaporates.
The question that found each of them is not "does this test pass?" but **"what can this fixture not produce?"**

**Observable, not merely counted — a log, a metric, and a failed result, deliberately all three.** The result is
what makes it recoverable (the outbox keeps the row owed). The log is at **Error** for a page and **Critical**
for a resolve, because `appsettings.json` sets `Logging:LogLevel:Default` to `Information` and a `LogDebug` is
never written in production — a "logged" drop nobody can read is the same silence one level down. And a log line
alone is what `gh#1045` and `gh#1051` were both filed against: *visible* meant an engineer reading structured
logs, never the operator. So it is metered as `trading.notifications.refused{kind}` and ruled as a **P1**,
`NotificationDeliveryRefused`. **This is the one condition Layer 1 structurally cannot self-report** — the push it
would use is the push that did not go — so it is exactly what Layer 2 exists for, and it reaches the operator
through the rule engine or not at all.

**Budget (§4) is unaffected on a healthy session.** The queue only refuses while a transport is not draining, so
a clean session emits nothing here; the promtool clean-session fixture asserts it. When it does fire, the
`increase(...[5m]) > 0` shape means at most one push per five minutes per `kind`, and it is a condition that
warrants a page by construction: it says the pager itself is not working.

**The socket hosts' provisional close stays, and is not made redundant by this.** It was tempting to call it so,
and the first cut of this update did. It is the belt over the residue named above — a page that fails delivery
inside the refusal window is re-offered under a fresh ordinal and re-arms the key with nothing left to release —
and `TriggerEvaluationService`'s one-shot staleness resolve has no equivalent belt at all. Tradovate is frozen
(`gh#41`) in any case, so nothing there is touched; the point is that a later reader must not remove it *citing
this update*.
