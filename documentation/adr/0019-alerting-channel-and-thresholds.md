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
- **`gh#243` — Layer 1 landed.** `Domain/Notifications/INotificationChannel` is the transport-free seam
  (severity / title / body / incident key — no priority numbers, no thread semantics), with
  `PushoverNotificationChannel` mapping **Page → Emergency (2)**, Notify → 0, Quiet → −1, and cancelling an
  outstanding page via its receipt when the incident resolves. `NullNotificationChannel` keeps an unconfigured
  deployment booting while logging a Page as an **error**, so unmonitored is never silent. The three P1 flatten
  conditions (`flatten.escalated`, `flatten.missed`, `flatten.watchdog.critical`) push directly, and a successful
  flatten or a watchdog save **resolves** the incident.
  **Corrected by `gh#289` (2026-07-26).** The first cut awaited the send inline, so a slow channel put its full
  latency on a flatten pass — on the R-13 path, and at the moment a position was already failing to close. The
  gh#246 suite caught it (a 5-second channel made a pass take 5.15 s) and pinned it. The chain is now
  **queue → dedup → transport**: `QueuedNotificationChannel` accepts and returns, a `NotificationPumpHost` drains
  it. Two properties fall out of the pump being single-threaded that a bare fire-and-forget would not have given —
  **dedup can no longer race itself** into a double-page, and it still sees the *real* delivery result, so a
  failed push is not mistaken for one the operator received. The seam's contract shifts with it:
  `SendAsync` returning true now means **accepted for delivery**, not delivered — a caller on the safety path
  cannot wait for delivery without reintroducing the defect.
  **One refinement to this ADR:** dedup was specified "in the adapter"; it landed one layer out as
  `DedupingNotificationChannel`, so every future adapter (Discord `gh#100`, web push ADR-0010) inherits it rather
  than reimplementing it, and it is unit-testable without a transport. The requirement is unchanged — one push per
  incident, re-armed on resolve.
- **`gh#245`** Alertmanager rules and routing (Layer 2) · **`gh#246`** the QA suite.
- **Thresholds are recorded but not yet enforced.** The P1/P2/P3 tables above describe what `gh#245` must build; only
  the dead-man's switch's own rules (check-in absent by deadline + 5 min, heartbeat missed ≥ 3 intervals) are live
  today, and they live in the **monitor's** configuration rather than in this repo.
- **Confirm the deadlines against the CME rulebook.** The times here inherit the flatten schedule's own caveat
  that they are operator-provided reference times, not verified exchange data.
- **Escalation beyond one person.** Out of scope while the operation is an on-call-of-one; revisit if that changes.
