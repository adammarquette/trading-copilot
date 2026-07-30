# ADR-0002: Observability — OpenTelemetry with Prometheus, Loki, and Tempo

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** [engineering](../trading-platform-engineering.md) §7, [architecture](../trading-platform-architecture.md), [ADR-0001](0001-event-backbone.md) (event backbone), PRD §7 (latency / health targets), `R-4` (suggestion engine).

## Context
We need **logging, distributed tracing, and performance counters** to operate the platform: follow a data point **end to end**, measure response times and pipeline latency, and judge each service's health and availability. Two forces shape the choice:

- **End-to-end traceability is the priority.** Following one event across the ingestion service, the async event log (ADR-0001), and the processor is a **distributed trace** — not something metrics or logs alone can reconstruct.
- **The suggestion path needs it too.** The trade-suggestion flow (R-4) is a multi-step, multi-agent fan-in — several **strategy agents** plus an **executor** that combines them into a timely suggestion. Seeing *which strategies fired, how they combined, and where the time went* is exactly what a trace shows; the same logs / traces / counters serve it directly.
- Stay within the already-chosen **Grafana stack**; personal-scale, self-hosted on Railway.

## Decision
Instrument everything with **OpenTelemetry** and export the three pillars to the self-hosted **Grafana LGTM stack**:

- **Metrics → Prometheus.** Per-service **RED** (rate / errors / duration histograms → p50/p95/p99); **runtime / performance counters** (GC, thread pool, heap, sockets/connections) via OTel runtime instrumentation + `System.Diagnostics.Metrics`; execution-specific SLIs (order-gate pass/block, auto-flatten success + time-to-flat, order-ack latency); and **pipeline lag** (append→consume, DB write latency, ingestion gap/backfill).
- **Logs → Loki.** Structured logs via `ILogger`, each carrying `trace_id` / `span_id` + a correlation ID.
- **Traces → Tempo.** The tracing backend that makes end-to-end traces queryable. **One trace per event:** `websocket → event-log append → processor → DB write` and `poll → normalize → append → processor → DB write`; and the **suggestion path:** `strategy agents → executor → suggestion`.
- **Cross-pillar correlation** in Grafana: `trace_id` in logs + Prometheus **exemplars** → jump trace ↔ logs ↔ metrics in one click.
- **Health / availability:** `/health` (liveness) + `/ready` (dependency checks), uptime.
- **Async trace continuity:** because the event log is async, **trace context rides in the event envelope** and consumers continue the trace via a **span link** — so a trace spans producer and consumer instead of breaking at the log.

## Alternatives considered
- **Prometheus + Loki only (no trace backend).** Rejected — can't reconstruct a cross-service trace (websocket→DB, or agents→suggestion), the capability we most want.
- **Jaeger / Zipkin for traces.** Capable, but outside the Grafana stack — loses native Grafana trace↔logs↔metrics correlation and adds a foreign UI.
- **Grafana Cloud (managed LGTM).** Simpler ops, but we self-host on Railway for cost/control; revisit if ops burden grows.

## Consequences
**Positive**
- True **end-to-end traceability** for the ingest pipeline *and* the strategy-agents → executor → suggestion path — see which strategies contributed to a suggestion and where latency lands.
- One-click **trace ↔ logs ↔ metrics** correlation; standard health/availability signals.
- Stays in the chosen Grafana stack; Tempo is object-storage-backed and cheap to run on Railway.

**Negative / costs**
- One more service to run (Tempo) alongside Prometheus / Loki / Grafana.
- **Trace context must be propagated through the event envelope** (span links) — a concrete requirement on ADR-0001's schema, or the async hop breaks the trace.
- Minor instrumentation overhead; a **sampling** policy may be needed if trace volume grows.

## Follow-ups
- Fix the **event-envelope trace-context fields** (with ADR-0001's envelope schema).
- **Span conventions** for the agent path: a span per strategy agent and per executor step, so a suggestion's trace shows the full fan-in.
- **The LGTM stack is stood up (`gh#231`, 2026-07-26).** Prometheus, Loki, Tempo and Grafana behind an
  **OpenTelemetry Collector**, in `docker-compose.yml` under the **`observability` profile** — off by default, so
  `docker compose up -d` is unchanged and a developer who does not want five extra containers is not blocked
  (telemetry export is fire-and-forget, so an absent stack can never become a reason trading does not work).
  Configuration lives in `./observability/` and is **provisioned as code**, including the Grafana datasources and
  their cross-pillar links: Prometheus **exemplars** → Tempo, Tempo → Loki (`tracesToLogsV2`), Tempo → Prometheus
  (`tracesToMetrics`). Metrics arrive by **remote write** rather than by scraping the collector, because remote
  write is what carries exemplars. Retention is 7 days on all three backends. `prometheus.yml` already references
  a mounted `rules/` directory so `gh#245` adds alerting by dropping files in rather than re-plumbing.
- ~~Alert channel + thresholds for the on-call-of-one (auto-flatten reliability must page)~~ — **decided in
  [ADR-0019](0019-alerting-channel-and-thresholds.md)** (2026-07-25, `gh#242`): Pushover behind an
  `INotificationChannel` seam, three layers including an out-of-process dead-man's switch, and the P1/P2/P3
  taxonomy with its noise budget. This ADR's collection decisions are unchanged; ADR-0019 extends them with what
  happens once a signal is worth waking someone for.
- A trace **sampling** policy if needed.

## Update (2026-07-26) — the SDK and the three signals landed (gh#230)

This ADR specified the target in 2026-07-18; nothing implemented it until now. **Traces, metrics and logs** are
wired in `Api/Program.cs` over **OTLP**, with ASP.NET Core / HttpClient / Npgsql / runtime instrumentation, and
resource attributes (service name, version, **`deployment.environment`**) so a staging signal is never mistaken
for a production one — this system's whole R-14 posture rests on knowing which environment an action came from.

**RED comes from the instrumentation's own instruments**, whose duration is a **histogram** by default, so
p50/p95/p99 are derivable. A mean would hide precisely the tail that matters on an execution path.

**`/health` and `/ready` are deliberately different contracts.** Liveness answers from the process alone and
touches **no** dependency — a liveness probe that queries the database restarts a healthy app during a database
blip, taking the auto-flatten scheduler down with it. Readiness **must** touch the database, because "ready"
means ready to serve; a failure removes the instance from rotation without killing it. Verified end to end: with
the database stopped, `/health` stays `200` and `/ready` returns `503 not-ready`.

**Trace context across the log's async gap.** The `traceparent` envelope field already existed (gh#7) and the
quote producer already stamped it; what was missing was the other half. Both consumers now **span-link** to the
producer's context, so `websocket → append → consume → DB write` is one queryable trace. A **link, not a parent**:
the consumer may run long after the append, and one batch can carry events from many producers, so a link
expresses "caused by" without pretending the consumer's span lives inside the producer's.

**Degradation is the load-bearing property.** With no exporter configured the SDK is still wired and
instrumentation still runs, but nothing is exported and **the app starts and trades normally** — verified. An
absent `traceparent` (pre-migration rows, an unstamped producer) starts a fresh trace; a **malformed** one is
ignored rather than thrown on. Losing a trace is a reporting loss; throwing would stop stop-promotion or
conditional firing outright. Instrumentation must never be able to break trading (engineering §9).

One correction worth recording: a `traceparent` declaring an **unknown version** (`99-…`) is **not** malformed.
W3C trace-context is deliberately forward-compatible, so a parser that recognises the shape accepts it. A first
draft of the tests asserted a rejection there — that would have pinned a bug.

*Still open:* the **LGTM stack** this exports to (gh#231), the **execution-specific SLIs** (gh#232), AI spend
(ADR-0008), and a trace **sampling** policy.

## Update (2026-07-26) — the execution SLIs, first increment (gh#232)

gh#230 landed generic RED health. This adds the signals particular to a system that places orders and flattens
before a close, in a new meter `MarqSpec.TradingCopilot.Execution` registered with the metrics pipeline.

**Landed in this increment — the flatten family**, the one engineering §7 singles out (*"auto-flatten reliability
is a monitored, alertable metric"*):

- `trading.flatten.deadlines` — a counter, dimensioned by **tier** (`primary` / `watchdog`) and **outcome**
  (`executed` / `escalated` / `missed` / `disabled` / `nothing-to-do`).
- `trading.flatten.time_to_flat` — a **histogram**, tagged by tier.

Two properties are load-bearing.

**An absence must be detectable.** A deadline that passes with no exposure still emits, as
`outcome="nothing-to-do"`. Without it, *"the flatten never fired"* and *"there was nothing to do"* are the same
silence — and the failure this system exists to prevent would look exactly like an ordinary Tuesday. The health
signal is the **presence of the series**; a dashboard alerts on its disappearance, which is also what makes this
complementary to the external dead-man's switch rather than redundant with it.

**The two tiers are never merged.** The whole point of a redundant watchdog is knowing *which* one saved you;
merged counters would hide a primary quietly failing every day while the watchdog covered for it.

**Cardinality is a safety property.** Dimensions come from a closed set — `outcome`, `binding_layer`, `tier`,
`consumer_group`. No account id, order id or instrument may ever become a label: an unbounded label set takes the
metrics backend down, which would mean instrumentation causing the very outage it exists to reveal (§9).

**All eight instruments are now wired (gh#295).** The gate counter records at the single choke point every SIZED attempt passes through — `OrderExecutionService`, where the decision object that later becomes a `GateDecisionRecord` is produced — so the metric and the rows reconcile by construction rather than by two call sites agreeing. Order-ack times transmit → acknowledgement around the only call to an executor. The kill-switch gauge is set on the FLAG itself, not the endpoint, so the startup rehydration that restores an operator lock moves the metric too. The orphan gauge is re-counted from the database on both drop and re-arm, so it cannot drift and genuinely returns to zero.

The sink is a Domain seam, `IExecutionMetrics`, because the send path lives in Domain and cannot depend on Api — the same shape as `IEventLog` and the notification channel. `NullExecutionMetrics` is the default, so an un-composed host measures nothing rather than needing a null check on a trading path.

## Update (2026-07-27) — instrumentation cannot fail a trade (gh#343)

`IExecutionMetrics` is a Domain seam injected into six services and measured from more places than that, and
**every call site invoked it unguarded**. QA `gh#331` probed the seam with a throwing sink and found three ways
observability could break trading — the outcome engineering §9 rules out:

- a throw while counting the gate decision **failed the send** (safe direction: nothing transmitted);
- a throw while recording **order-ack latency** failed the send **after the venue had accepted the order and
  before the journal was written**, leaving a **live order with no `Order` row and no stop plan** while the
  operator was told the send had failed — orphaned exposure caused by instrumentation;
- a throw while setting the kill-switch gauge **failed the operator's emergency halt**, although the switch had
  in fact engaged: the halt took effect and the operator was told it had not.

**Resolved by decorating the seam, not by guarding each call site.** `FailureTolerantExecutionMetrics` wraps the
real sink at the one place the composition root binds `IExecutionMetrics`: every fault is absorbed and logged at
**error** — never silently — so the invariant holds for every current and future measurement and cannot be
forgotten by whoever adds the next one.

Two consequences worth recording. **All faults are absorbed, including `OperationCanceledException`:** the seam
takes no cancellation token, so a cancellation surfacing from a sink is not a cooperative stop of the trading
action but a sink fault like any other — elsewhere a caller's token *is* available and its cancellation is
deliberately rethrown, and that difference is why this one does not. And **a test that replaces the registered
`IExecutionMetrics` outright bypasses this guard**, so the fault must be injected *inside* the decorator for the
protection to be exercised — the shape `gh#331`'s harness uses.

That last point was written here as already true and **was not**: `gh#331`'s harness went on replacing the
registration, so from this fix landing until `gh#382` its three probes ran against a host with no decorator in it,
kept observing the pre-fix behaviour, and passed *vacuously* over the very invariant they were cited as
establishing. `gh#382` wraps the fault injector in the shipped decorator and adds a guard asserting that the
registration it displaced **is** `FailureTolerantExecutionMetrics`, so the same bypass fails loudly instead of
silently. Recorded because the failure mode is general: **a doc that states an intended shape does not make the
harness adopt it**, and a green suite is not evidence that it did.

The shipped `ExecutionMetrics` is total by construction (counter `Add`, `Interlocked`), so this guards the
**seam** rather than a present-day bug: the interface is what a later exporter-backed or further-decorating sink
would be registered as, and that is where the fault would come from.

## Update (2026-07-28) — the stack now alerts (gh#245)

This ADR chose the LGTM stack for **seeing** what the system did. It now also **tells you** when something is
wrong: **Alertmanager** joins the `observability` profile, delivering to Pushover per ADR-0019, with rules
evaluated by the Prometheus this ADR already chose.

Two consequences worth recording here rather than only in ADR-0019:

- **The stack acquires a duty it did not have.** Until now it was pure diagnosis — if it was down, you lost
  visibility, not safety. It is now part of a safety-adjacent path, which is why the rules include an
  always-firing **heartbeat** whose *absence* is the alarm, and a **P1 on the app's own telemetry going silent**.
  A monitor that fails quietly is the same failure class as the fault it was watching for.
- **The profile stays opt-in, and that is a deliberate limit.** `docker compose --profile observability up -d` is
  still required, so a deployment that never enables it has no Layer 1 alerting at all. That is acceptable only
  because ADR-0019's **Layer 2 dead-man's switch is external and independent of this stack** — it is the tier that
  survives this stack, and the host, not existing.

## Update (2026-07-28) — the exemplar pivot actually works (gh#338)

This ADR's *Decision* has promised **cross-pillar correlation** since it was accepted — *"`trace_id` in logs +
Prometheus **exemplars** → jump trace ↔ logs ↔ metrics in one click"* — and engineering §7 repeats it. The logs
half was real from `gh#230`. **The exemplar half was not**: the metrics pipeline configured no exemplar filter and
OpenTelemetry's default is `AlwaysOff`, so no measurement ever carried one. An operator clicking through from an
`ack_latency` spike to the trace that caused it found nothing to click. Found by QA `gh#329`.

`SetExemplarFilter(ExemplarFilterType.TraceBased)` closes it. **Trace-based rather than always-on** deliberately:
an exemplar is attached only when the measurement was taken inside a **sampled span**, which is the only case the
pivot can actually be followed. Always-on would decorate every series with a dead link and pay the storage for
nothing.

Two notes for whoever meets this next. The **cost is bounded** — one exemplar per series per collection cycle, on
metrics whose dimensions are already a closed set (`gh#232`) — so this does not reopen the cardinality concern
above. And a **unit test that builds the real telemetry configuration** now guards it: the defect lived in the
*configuration*, so a test that assembled its own provider would have passed while production stayed dark. That
test also asserts a measurement was collected at all, because the failure mode it replaced was an assertion that
was quietly vacuous.

*With this, everything ADR-0002's Decision section claims is now true in code.*

## Update (2026-07-28) — the dashboards exist, as files (gh#366)

This ADR chose the LGTM stack and the 2026-07-28 update above gave it alerting. It now has the **single pane**
the choice was made for: three provisioned dashboards, proposed by QA on gh#332 and built here.

| Dashboard | What it answers |
|---|---|
| **Auto-flatten reliability** | Did the R-13 obligation run, how fast did it flatten, did the backstop have to save it |
| **Execution & risk gate** | Is every order gated, which limit is binding, venue round-trip, kill switch, unprotected exposure |
| **Synthetic risk & pipeline health** | Protection that is platform-held rather than exchange-held, and whether the log's two safety consumers are keeping up |
| **AI usage & spend** *(gh#412)* | What the AI is costing — total and against the daily cap, split by model tier and by LLM-vs-embeddings, with call outcomes, tokens and latency |

**Provisioned as files, and deliberately not editable in the UI.** The provider sets `allowUiUpdates: false` and
the mount is read-only, so a dashboard cannot become console state that no PR reviewed and no `down -v` survives —
the Platform contract's *"configuration that exists only in a provider's web console does not exist"*, made
structural rather than aspirational. Editing a dashboard is a commit.

**The absence of a series is the alarm.** The auto-flatten board leads with that in a text panel, because it is
the one thing a reader gets wrong by default: `trading_flatten_deadlines_total` is emitted on **every** deadline
evaluation including `outcome="nothing-to-do"`, so a blank panel means the loop did not run — a zero bar and no
bar mean opposite things. A dedicated stat panel reports *reporting / SILENT* rather than leaving that inference
to the viewer.

**A defect found while building, worth recording because the class recurs.** gh#366's proposed queries named
`trading_flatten_time_to_flat_bucket`, `trading_order_ack_latency_bucket` and
`trading_eventlog_pipeline_lag_bucket`. **None of those series exist**: all three instruments declare `unit: "ms"`
and the collector's `prometheusremotewrite` exporter appends the unit, so the real families end
`_milliseconds_bucket`. A panel over a non-existent series renders *"No data"* — **indistinguishable from a
healthy quiet period**, which is precisely the failure this board exists to reveal. It is the same class as
gh#245's refusal to write alert rules over unemitted metrics, and it is why the queries here were validated
against a live Prometheus rather than transcribed.

**Exemplar drill-through is wired.** gh#366 was written while gh#338 was open and noted the metric → trace pivot
did not work. It landed, so the latency panels set `exemplar: true` and click through to Tempo.

*Still open:* the two **alert candidates** gh#366 proposed alongside the panels (`absent_over_time` on the
flatten family, and a sustained `trading_stops_orphaned > 0`) are close cousins of rules gh#245/gh#370 already
ship — `TelemetryPipelineSilent` and `OrphanedStopsWithExposure` — and are deliberately not duplicated here.

## Update (2026-07-30, gh#412) — the AI usage & spend dashboard, and the unit trap caught a second time

The **AI usage & spend** board lands (table above), completing X1's task 3 and giving AI cost the single pane the
LGTM choice was made for. It reads the LLM meter (gh#477, `ai_llm_*`) and the embedding meter (gh#403,
`ai_embed_*`) — both on the `MarqSpec.TradingCopilot.Ai` meter — and answers: what is this costing in total and
against the daily cap, is the ADR-0008 tiering bet actually paying (spend split by `tier`), where is it going
(LLM vs embeddings), and are calls failing (outcome split, where a `Failed` call is a real zero-token datapoint
rather than an absence).

**The epic's "operator-only" framing is retired**, per ADR-0015: spend is simply the operator's own, so it lives
in Grafana because it is a running-cost question, not because it is withheld from anyone. The **in-app**
cost-per-suggestion surface remains a separate Phase-4 client concern.

**Two honest limits, both filed rather than papered over.** *Headroom* is drawn against a Grafana constant
mirroring `Governor__DailyBudgetUsd`, because Prometheus cannot read application config — gh#506 proposes
emitting the cap as a gauge so the two cannot drift. *Per-feature* attribution is not yet possible: the meter
tags model / tier / outcome but not feature, so the LLM-vs-embeddings split stands in as the feature axis
(harmless while every LLM row is `Feature = Triage` per gh#449) — gh#507 added the tag, so the metric half can be split by feature the moment a second one emits. Neither is
enforcement: a meter is export-only, so the governor still caps on the `AIUsage` ledger floor (gh#448).

**The unit trap from the gh#366 note above recurred, was caught the same way, and is now fixed (gh#505).** The cost
instruments declared `unit: "USD"`, which the exporter appends **verbatim and case-sensitively** — and because the
instrument name already ends in a lowercase `usd`, the duplicate-suffix check did not match. The series that
actually landed were `ai_llm_cost_usd_USD_total` and `ai_embed_cost_usd_USD_total`, while the intuitive
`ai_llm_cost_usd_total` returned nothing. This was established by emitting the exact instrument set through the real
collector → Prometheus path and reading back `/api/v1/label/__name__/values`, then running **every panel query**
against it — a guessed name would have shown *"No data"* on a spend panel forever, which reads as *"nothing was
spent"*.

**gh#506 publishes the governor's configured ceiling** as `ai_governor_daily_budget_usd`, so the dashboard computes headroom from Prometheus alone and the mirrored `$budget` Grafana constant is deleted -- a hand-copied cap silently reports against the old number the moment `Governor__DailyBudgetUsd` changes, and a wrong headroom reading on a cost governor is the same class of failure as a wrong cap. An **unset** budget emits no series rather than 0, so "no cap configured" stays distinguishable from "a cap of zero"; the headroom panel divides by it, and dividing by absence yields an empty panel, which the panel description names explicitly. It is observability only -- enforcement stays on the `AIUsage` ledger floor, a meter being export-only (gh#448).

**gh#505 moved both instruments to the OTel annotation unit `{USD}`**, which the exporter does *not* append, so the
series are now `ai_llm_cost_usd_total` and `ai_embed_cost_usd_total` — the form every other instrument here already
used (`{call}`, `{token}`, `{position}`). The dashboard queries moved in the **same PR**, because a rename landing
without them turns six cost panels into *"No data"*, which on a spend view reads as **zero spend** rather than a
broken query. A dashboard restored from a pre-gh#505 revision against a newer app has exactly that failure, so the
JSON keeps the old names in its description as a breadcrumb.

*The rule this makes concrete, twice over: **never transcribe a metric name — emit, scrape, and read it back.***
