# ADR-0001: Event backbone — append-only Timescale event log

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** [architecture](../trading-platform-architecture.md) (event pipeline), [engineering](../trading-platform-engineering.md) §2 (storage), PRD `R-1` / `R-3` / `R-4` / `R-9`.

## Context
The architecture routes ingested data through an **event pipeline**: thin ingestion/poller services publish
events; processor services consume them, persist, and pre-compute indicators. We must choose the backbone.

Constraints specific to this system:
- **Append-only + replayable.** Adding or rebuilding a technical indicator should be a matter of running a **new
  consumer that reprocesses the stored event history** — not re-ingesting from the venue. *(Operator's explicit
  goal.)*
- **Not overkill.** Single-operator, personal-scale, on Railway; modest throughput (one trader's watchlist).
- **Reuse existing infra.** The stack already runs **Postgres + TimescaleDB**; fewer moving parts and lower cost
  are strongly preferred.
- Decouple thin ingest from processing; independent consumers; per-instrument ordering; at-least-once acceptable.

## Decision
Use an **append-only event log on TimescaleDB** as the event backbone — *not* a delete-on-consume queue.

- Events are **appended to a time-partitioned hypertable** — the durable store *and* the replayable log, one
  artifact, no separate bus.
- Each **consumer group tracks its own cursor** (last-processed sequence/time) in a small offsets table, so
  consumers are independent and a **new consumer replays from offset 0** to (re)build derived data.
- **`LISTEN/NOTIFY`** wakes consumers on new events for low latency; **`SKIP LOCKED`** parallelizes workers
  within a consumer where needed.
- **Indicators are projections over the log:** those that fit (bucketed OHLCV, VWAP, rolling sums) are
  TimescaleDB **continuous aggregates** (incremental, auto-refreshed, real-time); complex/stateful ones are
  **custom replay consumers**. Rebuild = replay.
- **PGMQ is reserved for true work queues** — do-once-and-discard tasks (trigger a poll, send an alert, an
  outbound command) — not the data/event backbone.

## Alternatives considered
- **PGMQ (Postgres queue).** SQS-like: read → delete/archive with a visibility timeout. Correct for work queues,
  but **consume-and-delete is the wrong model** for "a new consumer replays the whole history"; its `archive`
  table is audit, not per-consumer replay. → reserved for work queues.
- **Kafka.** Best-in-class replayable log, but **operationally heavy and overkill** for personal-scale on Railway.
- **NATS JetStream / Redis Streams.** Lightweight replayable logs and the natural **upgrade path** if we outgrow
  Postgres — but a new service that doesn't reuse the Timescale we already run.
- **Plain table + `SKIP LOCKED`.** Viable, but the hypertable gives compression, retention, and continuous
  aggregates for free.

## Consequences
**Positive**
- Append-only + full replay → add/rebuild indicators by adding a consumer; no re-ingest (the operator's goal).
- **Zero new infrastructure** — reuses Postgres/TimescaleDB; lowest Railway cost/ops.
- Store and log are one artifact; indicators are projections (continuous aggregates do much of it automatically).
- Transactional with data writes; naturally supports the `R-9` replay/simulation (the log *is* recorded data).

**Negative / costs**
- We **build the cursor/offset + consumer-group mechanics ourselves** (no turnkey consumer-group API).
- **Retention vs. replayability:** rebuilding by replay requires *retaining the raw events* that far back — the
  retention window becomes a deliberate decision (compression mitigates cost).
- At-least-once → **consumers must be idempotent** (dedupe by event id).
- Throughput ceiling is Postgres, not Kafka — fine at this scale.

## Follow-ups
- Define the **event envelope** (type, source, occurred-at, monotonic seq, payload) and the offsets-table schema.
- Decide the raw-event **retention window** (replayability vs. cost).
- Put the backbone behind an **`IEventLog`** publish/consume seam so a future NATS JetStream (or Kafka) swap is
  an adapter change, not a rewrite — same discipline as the venue abstraction (`R-17`).

## Update (2026-07-18) — event-log retention decided
The raw-event **retention window** (follow-up above) is set: the event log keeps **< 24h, likely < 1h**. Live
event-log data is **augmented/superseded by the data poller's clean historical** (REST), which lands in the
long-retention time-series store as the authoritative record. So the event log is a **short-lived real-time
pipeline**, and a **full indicator rebuild reprocesses the clean historical store**, not the short event log —
the log's own replay covers only recent catch-up/recovery. Retention is **configurable**; a future event type
may warrant a longer window. See engineering §8.

## Update (2026-07-26) — the retention contract is explicit, and a trailing consumer is told (gh#227, decision gh#162)

The window above made the log **lossy by design**, but nothing said so at the seam, and the reader did not act
like it. `ReadAfterAsync` read forward from a cursor with `Sequence > afterSequence`; if retention had since
dropped everything below the cursor, the call returned the survivors, succeeded, and reported nothing. **A silent
hole was indistinguishable from an uneventful poll** — on a log whose only two consumers ride safety-critical
paths (stop promotion gh#153, conditional firing gh#198), where a hole leaves derived state *confidently wrong*
rather than merely stale.

The contract is now stated rather than implied by a migration parameter:

- **The log is lossy past its retention window.** Events older than the window are gone and are **not** recoverable
  from this log. The clean-historical store remains the authoritative record for rebuilds (the 2026-07-18 update).
- **A consumer that has fallen behind the window is told.** `ReadAfterAsync` returns an `EventPage` carrying an
  optional `EventRetentionGap` (the requested cursor, and the oldest sequence that still survives).
- **A consumer must handle a gap explicitly.** Silence is the defect; a `catch` or an ignored field reintroduces
  it. Both current consumers log at high severity naming the group, the cursor, and the surviving bound, then
  resume deliberately at the head — which is recovery, not a guess, because each re-evaluates from fresh truth on
  the next quote and the **native safety stop is the physical floor throughout** (ADR-0007, ADR-0013).

**A typed result, not an exception.** A gap is an expected, recoverable condition on a lossy log. An exception on
a polling loop invites the `catch` that re-hides it; as a field on the result, ignoring it is a visible choice in
the consumer's code rather than an absence.

**The events are returned either way.** A gap degrades what a consumer *knows*, not its ability to make progress —
withholding the survivors would turn a reporting improvement into an outage on a safety path.

**What the signal deliberately does not claim.** It reports two bounds, never a count of lost events: sequence
numbers are not contiguous (a rolled-back append consumes a value), so the span is an **upper bound**. For the
same reason the detection asks *"does anything at or below the cursor still survive?"* rather than inferring loss
from a numbering skip — a skip is normal and means nothing was lost, and a signal that cried wolf on every
rollback would be learned-ignored long before a real gap arrived. Two cases are **not** gaps by definition: a
brand-new consumer group (cursor `0` reads from the start of whatever survives) and an empty log.

*Still open:* **per-type retention** (option 3 on gh#162) lowers the likelihood of a gap without removing the
silence, so it complements this rather than replacing it; **backfill** from the clean-historical store is the R-1
gap-detection path, not this one; and **alerting** on a gap arrives with the observability increment (gh#26).
