# ADR-0001: Event backbone — append-only Timescale event log

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** [architecture](../trading-platform-architecture.md) (event pipeline), [engineering](../trading-platform-engineering.md) §2 (storage), PRD `R-1` / `R-3` / `R-4` / `R-9` / `R-22`.

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

## Update (2026-07-26) — the clean-historical store exists (gh#302)
This ADR has assumed that store since the update above; it did not exist until now. `BarRecord` / the `Bars`
hypertable is it: OHLCV filled by a periodic REST backfill (`BarBackfillHost`, default 60 s per engineering §8),
keyed on `(venue, instrument, resolution, bucket)` so an overlapping re-poll **updates** rather than duplicates,
and never storing a bucket that has not closed. **It carries no retention policy on purpose** — expiring the
system of record would defeat what it is for — which is the deliberate contrast with this log's 24 hours.

**Reconciling the numbers above:** the text says *"< 24h, likely < 1h"*; the `AddEventBackbone` migration sets
**24 hours**, the outer bound. The migration is the operative value and 24h stands — with the clean-historical
store now real, there is no pressure to shorten it, and the retention-gap signal (gh#227) makes a trailing
consumer's loss explicit rather than silent either way. Treat 24h as the decision and this text as its range.

Still open: **backfilling a gap** from this store is the R-1 path (gh#306), not this log's.

## Update (2026-07-27) — projections exist, and rebuild really is replay (gh#310)
The "indicators are projections … rebuild = replay" line above is now implemented rather than intended.
`IndicatorValueRecord` / the `IndicatorValues` hypertable holds them, and the first is **ATR** — the indicator
`StopPlan` has refused a promotion band for since ADR-0007 was written (gh#311 lifts that refusal).

Two properties are worth recording because they are what make the rebuild claim true rather than aspirational:

- **`AverageTrueRange.Compute` is a pure function of the bars handed to it** — no clock, no storage, no state.
  Each pass recomputes from the *start* of the stored series, so the derived values are a function of the store
  and nothing else. Seeding from a moving window would have made a value depend on when it happened to run.
- **A revised bar corrects the values derived from it.** gh#302 upserts a restated bar in place; the projection
  upserts the values that depended on it, so the store never holds a number its own data no longer supports.

Chosen deliberately: **Wilder's smoothing**, not a simple moving average of true range — that is what "ATR" means
on the operator's chart, and a stop distance that disagrees with their chart gives them no way to tell a bug from
a definition. And **no value at all until the period is satisfied**: a partial average looks entirely ordinary and
would place a stop at the wrong distance, which is the silent mis-measurement the refusal existed to prevent.

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
gap-detection path, not this one *(landed gh#306 — see the update below)*; and **alerting** on a gap arrives with
the observability increment (gh#26).

## Update (2026-07-27) — the gap signal has a recovery path (gh#306)

gh#227 above ends by handing backfill to R-1 as *"not this one"*. That path now exists, so a trailing consumer is
no longer merely **told** what it lost — what is still recoverable is recovered, from the clean-historical store
(gh#302), which is where ADR-0001 has always said a rebuild comes from.

**The gap is reported in sequences; the store is keyed in time.** So recovery works on a window:
`EventRetentionGap` gained `OldestAvailableOccurredAt` (the end — only the log knows it), and `IEventLog` gained
`GetCursorCommittedAtAsync` (the start). The start is a **commit** time, not the last event's occurred-at — the
event at the cursor is gone, which is what a gap *is*, so its own timestamp cannot be read back. That makes the
window very slightly wide; recovery is idempotent, so covering a little too much is the safe direction of that
error.

**Extremes, not a bar-by-bar replay.** Both questions this answers — *did price come within the promotion band?*
and *did price cross this trigger?* — are monotone in the window's extremes, so the deepest adverse excursion
answers them exactly. Replaying each bar would cost a venue round trip per bar to reach the same answer.

**The two consumers are treated differently, and the difference is the decision:**

- **Stop promotion recovers.** A hidden stop whose band was crossed while the consumer was blind is promoted.
  Promoting is protective, one-way, and still fully gated — the venue is asked whether the position is open, the
  stop is sized to the live remaining, transmit-then-record is unchanged. The backfill supplies a *price*, not an
  exemption, so acting late is strictly safer than not acting.
- **Conditional firing deliberately does not**, and that is now recorded rather than omitted. A conditional fires
  an **entry**; firing one on a cross that happened minutes ago would open a position at a price that has moved
  on — acting on stale grounds, which ADR-0013 forbids and R-12's validity tolerance exists to prevent. The
  failure direction stays *"did not fire"*. What it gains is **specificity**: the operator is told *which*
  conditionals were crossed while blind, which is what makes the signal actionable rather than merely alarming,
  and the decision to re-arm stays theirs.

**A partial recovery is never reported as a whole one.** `GapCoverage` measures what the store actually covered
against what was asked for, and any shortfall is logged separately, at error, naming the instrument and the
duration. This is the one place the increment could have made things worse than the silence gh#227 replaced: a
backfill that said "recovered" while covering half a window would be *believed*. It deliberately does not measure
an **interior** hole — coverage runs first-bucket to last — because bars arrive from one periodic REST poll over a
contiguous window, so an interior hole is a venue omission rather than the partial-coverage case this measures.

**Backfilling is in addition to the signal, never instead of it.** The gh#227 error log fires unchanged, and the
`RecordRetentionGap` metric with it. Recovery that quietened the signal would trade a known hole for an unknown
one. Backfill also **never throws into the live path**: it runs inside its own consumer's pass (so a consumer
catching up cannot stall one that is current), and a failure degrades to the pre-gh#306 behaviour — resume at the
head — rather than escalating a recovery problem into an outage on a safety path.

*Still open:* **alerting** on a gap or a shortfall (gh#26 / gh#232, ADR-0019) — this increment logs at error;
routing that to a push is the observability increment's.

## Update (2026-07-28) — the third storage shape is real, and its degrade is not the Timescale degrade (gh#109)

This ADR chose Postgres and named three shapes: relational, time-series via TimescaleDB, and **vector via
pgvector**. The third existed only on paper until now. `EmbeddingRecord` / the `Embeddings` table is it — the
data dictionary's polymorphic `VEC` row, keyed on **owner kind + owner id + model**.

**Model is in the key on purpose.** Vectors from different models are not comparable, so two models' vectors must
coexist as separate rows rather than one silently overwriting the other and leaving a mixed-model index nobody
can trust. The key doubles as the idempotence guard, the same DB-enforced pattern the bar and news stores use: a
re-embed **updates** rather than appends, which matters because embedding is a *paid* call and re-ingestion is
normal.

**The degrade needed its own decision, because it is genuinely unlike Timescale's.** Without `timescaledb` the
`Events` table still exists and every query still works — you lose compression and retention. Without `vector`
**there is no column type at all**, so the table cannot be created. The choice was: skip the table and start, or
refuse to start.

**Decided: skip and start, loudly.** Refusing to start would let an unavailable *retrieval* feature take down the
**safety-critical auto-flatten** (R-13) — a system that will not run before the CME close because semantic search
is missing has its priorities exactly inverted, and nothing on the trading path depends on embeddings. But
skipping *silently* is the failure this codebase refuses everywhere else (gh#227's silent hole, gh#245's
unemitted metrics, gh#306's declared-unknown), so the absence is **declared**: the migration raises a warning
naming the consequence, and `IEmbeddingProvider.IsAvailable` reports false so retrieval **refuses** rather than
returning an empty result set that reads as *"nothing is relevant"*.

**The `IsAvailable` half was aspirational until gh#474** — this paragraph described it, and the provider did not do
it. `CohereEmbeddingProvider.IsAvailable` returned true on a configured key alone, so a deployment with a key and
no extension passed the gate: gh#377's pass embedded through Cohere on **every poll** (real spend on the operator's
own key) and faulted at the upsert every time — logged, harmless to trading, and never self-healing. Availability
now means the **whole round trip**: a key to make the call *and* somewhere to put what comes back, probed once at
startup (`VectorStore`, recorded by `StartupTasks` after the migration that would have installed the extension) and
defaulting to **absent**, so a caller racing the probe declines rather than spends.

Verified against both: applied to `timescale/timescaledb-ha:pg17` (table + HNSW index created) and to plain
`postgres:17` (table skipped, **the other 24 tables created normally**, migration succeeded).

**Two smaller choices worth recording.** The index is **HNSW, not IVFFlat**: IVFFlat must be built after
representative data exists and needs a list count tuned to row count, so an empty-table migration cannot build a
good one, while HNSW builds usefully on an empty table and stays correct as rows arrive. And the operator class is
**cosine**, because embedding models emit direction-normalised vectors — L2 over them would rank partly by
magnitude, which here is noise.

**The store is relational-only in the EF model.** `Vector` has no in-memory-provider mapping, so the entity is
configured only when the provider is Npgsql and ignored otherwise. That is the honest shape rather than a
workaround — a vector column cannot exist without pgvector — and it means the store's coverage is
**integration-tier**, with unit tests covering the seam. A `float[]` value converter was rejected: it would let
the in-memory provider *pretend* to store embeddings while the similarity operators, the entire point, silently
did not exist.

*Landed:* the real provider (**gh#403**, Cohere with its sparse fallback and per-call cost metering) and the first
consumer wiring (**gh#377**, `NewsEmbeddingService` populating `NewsItem`'s embedding). *Still open:* which other
owner kinds (suggestion / rule / snapshot) embed, and when.

## Update (2026-08-14) — "not comparable" is now enforced at the read, not just the key (gh#881)

The **Model is in the key** decision above keeps two models' vectors as separate rows, but the retrieval reads
(`INewsEmbeddingSimilarity`) filtered only on owner kind — so after a model change they returned **both** models'
rows for an owner. Downstream, the by-owner reads (`GetVectorsAsync` / `GetTopicVectorsAsync`) returned a duplicate
row per owner, so the gh#854 last-wins collapse picked an **arbitrary** model's vector, and a **cross-model cosine**
— a meaningless-but-nonzero similarity — could result: exactly the mixed-model comparison the key was meant to
prevent. (Before gh#854 added that collapse, an unguarded `ToDictionary` would have *thrown* on the duplicate key —
which is why the collapse exists; it is not a whole-pass throw today.) `NearestNewsAsync`, a plain ranked list, could
likewise rank a retired-model vector or return the same owner twice. The invariant is now enforced **where it
matters**, at the read: `PgVectorNewsSimilarity` injects `IEmbeddingProvider` and the **by-owner** reads
(`GetVectorsAsync`, `GetTopicVectorsAsync`) filter `Model == provider.Model`, so `(OwnerKind, OwnerId)` is unique in
the result and every comparison is same-model. After a model change an owner embedded only under the old model reads
back **empty** — a bounded degrade — until the embedding pass re-embeds it under the current model (its candidate
query already keys on the current model), so the transition **self-heals** and never returns a wrong (cross-model)
answer. `NearestNewsAsync` gained the same filter in the follow-up (gh#889) — deferred out of *this* PR only because
it had to land together with aligning the gh#864 recall guard (which drives `NearestNewsAsync` through a provider
whose `Model` is `"none"`, so adding the filter alone would have reddened that guard). See the 2026-08-15 update.

Two honest costs. **The self-heal is cap-gated, not prompt:** a model change re-embeds the *whole* corpus (every news
item + topic, each a paid call), so under the daily AI-spend cap a large corpus re-embeds over several passes —
potentially days — during which the affected items degrade correctly to non-semantic. And now that `NearestNewsAsync`
carries the filter too (gh#889), the model predicate is a *second* post-scan filter on the approximate HNSW window, so
during a migration window (new-model rows still sparse) recall can be reduced further than the owner-kind filter alone —
the gh#864 interaction; the result stays distance-ordered and the consumer degrades gracefully, never wrong.

The now-unread stale rows are **harmless** (never read), so the "leaves its vector behind until swept" cost the
polymorphic store documents is a **storage-only** concern; the actual GC follows in gh#889 (stale-model rows, below)
and gh#902 (orphaned-owner rows after a rename/delete), neither a correctness fix.

## Update (2026-08-15) — the read-scope filter reaches `NearestNewsAsync`, and the stale-model sweep lands (gh#889)

gh#881 (above) scoped the **by-owner** reads to the current model and deferred two coupled pieces to this follow-up:

- **`NearestNewsAsync` current-model filter.** The ranked read now also filters `Model == provider.Model`, so after a
  model change it can no longer rank a retired-model vector or return the same owner twice — the last gap gh#881 named.
  It had to ship with a **QA alignment**: the gh#864 recall guard drives `NearestNewsAsync` through a host whose
  provider `Model` is `"none"`, so that guard's seed model moves to `"none"` in the same PR — the filter alone would
  have reddened it. The **gh#864 interaction** the gh#881 update predicted is now live (a *second* post-scan filter on
  the approximate HNSW window), so during a migration window recall can narrow further than the owner-kind filter
  alone; still distance-ordered, still a graceful degrade. The sweep below does **not** blunt this: it removes an
  owner's retired-model row only *after* that owner is re-embedded, so early in the pass — new-model rows sparsest —
  the still-unprocessed owners' old rows crowd the window undiminished; the sweep prevents a lingering
  two-rows-per-owner state, not the early-pass starvation.
- **Stale-model sweep.** After each embed page's `SaveChanges` (current-model row durably present), a set-based
  `ExecuteDeleteAsync` removes the just-re-embedded owners' **other-model** rows — scoped to `(this owner kind, an
  owner embedded this page, Model != current)`, so it can never touch a current-model row, another owner kind, or an
  owner not embedded this page. **Best-effort** (a sweep fault logs and leaves the harmless stale rows) and spends
  nothing (a SQL DELETE on the success path, no AI call). A *completed* model transition now leaves **no**
  duplicate-owner rows; it still self-heals incrementally under the AI-spend cap (one owner swept per owner re-embedded).

**Still deferred: full GC (gh#902).** Two row classes the incremental sweep can't reach: rows for a **deleted or
renamed** owner (no re-embed ever touches them), and **stale-model rows leaked by a crash** between a re-embed's
`SaveChanges` and its sweep (the owner then has a current-model row, so the next pass idempotent-skips it and never
re-sweeps). Both are harmless to reads (every read pins the current model) but accumulate; both are reclaimed by a
**periodic full `Model != current` / anti-join GC** rather than the embed hot path — it needs per-owner-kind existence
logic (skipping the producer-less owner kinds), a concurrency-safe bulk delete, and an irreversibility guard (a deleted
vector is a paid re-embed if the owner returns or the model reverts), so it is its own increment (gh#902).

## Update (2026-08-15) — the orphaned-owner GC lands (gh#902)

The **orphaned-owner** half of the deferred full GC ships: `EmbeddingOrphanGcHost` periodically deletes
`EmbeddingRecord` rows whose owner no longer exists — a renamed `NewsTopic` (its old-name `Topic` row) or a pruned
`NewsRecord` (its `SoftSignal` row). A set-based **anti-join** `ExecuteDeleteAsync` per owner kind
(`OwnerKind = SoftSignal AND NOT EXISTS(News.DedupKey)`, `OwnerKind = Topic AND NOT EXISTS(NewsTopics.Name)`), on a
long conservative cadence **off** the hot embed path — the delete is irreversible (a paid re-embed if the owner
returns). One atomic statement, so a concurrent embed of a still-live owner cannot be raced (it only embeds owners
that exist, which the anti-join never deletes); best-effort per kind (a fault sweeping one logs and leaves the others).

The **producer-less** owner kinds — `Suggestion` / `Rule` / `MarketSnapshot`, which have no producer table yet — are
kept out by an **allow-list**, not a deny-list: `EmbeddingOrphanSweep.SweepableKinds` is exactly `SoftSignal` + `Topic`,
so a future owner kind is **not** swept until it is deliberately added alongside its producer check, and cannot be
GC'd to zero before it has one.

The second row class the paragraph above lumped under gh#902 — **stale-model rows leaked by a crash** between a
re-embed's `SaveChanges` and its sweep (the owner still *exists*, so this anti-join keeps it) — is **not** this
increment. It is a bounded, storage-only residual split out to **gh#915**, whose natural home is this same host.

## Update (2026-08-15) — the stale-model crash-leak backstop lands (gh#915)

The residual gh#902 split out now ships in the same `EmbeddingOrphanGcHost`: after the orphaned-owner sweep, when a
current model is known (a provider is configured), a second set-based `ExecuteDeleteAsync` removes **crash-leaked
stale-model duplicates** — a `Model != current` row for an owner that **also** has a current-model row. That
current-model sibling is the safety: it proves the owner is legitimately embedded now, so its other-model row is the
redundant leftover a re-embed's incremental sweep (gh#889) should have removed, but a crash between its `SaveChanges`
and that sweep left behind. An owner's *only* vector — whatever its model — has no current-model sibling, so it is
never touched (the read filter degrades over it gracefully rather than losing it). One atomic self-`EXISTS` DELETE,
**owner-kind-agnostic** (the sibling proves legitimacy without a producer lookup, so unlike the orphaned-owner sweep it
needs no allow-list) and **best-effort** (a fault leaves the harmless rows). With no provider configured it is skipped —
no current model, so no duplicate to resolve. The deferred "full GC" clause is now **closed**.

## Update (2026-09-03) — the vector store serves cross-kind retrieval, not just news (gh#1065)

The third storage shape has had exactly **one** retrieval consumer since gh#377: news. gh#18's increment 5 was meant
to be general cross-kind retrieval, and #995 delivered only the news slice, so the co-pilot could ground a
conversation on the market and on nothing the operator had actually done. This generalises the existing pipeline
rather than adding a second one beside it.

**Two more owner kinds are now written**: `Suggestion` (its trade line plus the model's rationale) and a new
`JournalEntry` (a **closed** `Trade`, rendered as its trade line plus its realized result). Adding them needed **no
schema change to the store itself** — the same "new owner kind, no migration" property gh#854 relied on for `Topic`,
because the `OwnerKind` column's only constraint is `<> 0` and the key `(OwnerKind, OwnerId, Model)` admits any kind.
What it *did* need is **one partial HNSW cosine index per kind**, which is gh#864's own rule arriving at its first
real test: a vector-ordered read whose owner-kind predicate is applied as a post-scan filter can return **zero**
neighbours though thousands exist. That rule has a corollary this increment had to obey in code as well as in SQL —
a partial index is matched against the query's predicate, which needs a **constant**, so `PgVectorEmbeddingRecall`
dispatches to one query per kind with a literal owner kind instead of parameterising it. Parameterising would have
re-opened gh#861's starvation silently, with no failing query to notice.

**The ranked read is now kind-parameterised.** `NearestNewsAsync` moved off `INewsEmbeddingSimilarity` onto a new
`IEmbeddingRecall.NearestAsync(kind, ...)`; the two remaining reads on the old seam really are news/topic-specific,
so its name is now accurate rather than inherited. Because every kind reports the same cosine metric, distances are
**comparable across kinds**, which is what makes the pipeline's merge a ranking rather than an interleave — and it
is also the keyless **degrade**: with no Cohere key the reranker returns identity order, so a deployment with no key
still gets a genuine nearest-first cross-kind list.

**R-20 is enforced at the hydrate, and that is a deliberate placement.** The `Embeddings` table is not `IUserOwned`
— it follows its owners, and news, topics and market snapshots are global — so the vector recall is
deployment-wide and can legitimately return another operator's suggestion. Rather than bolt an owner column onto a
polymorphic store whose other kinds have no owner, the pipeline reads each recalled row back **through the tenant
query filter**: a foreign row is simply absent, dropped by the same code path that drops a deleted owner. The
symmetric consequence sits on the **write** side, where a background host has no request user and therefore
`IgnoreQueryFilters` (or every row would look unembedded forever) — so each embed is ledgered to **that row's own
owner** instead of the `SystemOwner` sentinel, keeping the operator's spend meter honest.

**The two sweeps extend rather than change.** `Suggestion` and `JournalEntry` join `SweepableKinds` with producer
checks against `Suggestions.Id` / `Trades.Id`; `Rule` and `MarketSnapshot` stay off it, still waiting on producers.
The allow-list's failure mode is now two-sided and both sides are pinned by test: a kind swept **without** a producer
is deleted to zero, and a kind **retrieved** but never swept accumulates orphans that keep filling the recall window
until the hydrate discards them — a silent recall degradation rather than a visible failure. The stale-model
backstop (gh#915) needed nothing: it is owner-kind-agnostic by construction.
