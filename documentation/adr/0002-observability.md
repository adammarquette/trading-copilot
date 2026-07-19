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
- Alert channel + thresholds for the on-call-of-one (auto-flatten reliability must page); a trace **sampling** policy if needed.
