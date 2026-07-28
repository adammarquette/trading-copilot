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
protection to be exercised — the shape `gh#331`'s harness now uses.

The shipped `ExecutionMetrics` is total by construction (counter `Add`, `Interlocked`), so this guards the
**seam** rather than a present-day bug: the interface is what a later exporter-backed or further-decorating sink
would be registered as, and that is where the fault would come from.
