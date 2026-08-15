# ADR-0021: Realtime hub contract (push, auth-on-connection, idempotent resume)

**Status:** Accepted · **Date:** 2026-08-05 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-10` (the live chart/surfaces), `R-18` (auth), `R-20` (data isolation), `R-13`/`R-16` (the
safety strip); [ADR-0001](0001-event-backbone.md) (the event log, cursors, at-least-once),
[ADR-0007](0007-order-execution-model.md) (the single send path), [ADR-0017](0017-single-operator-data-isolation.md)
(data isolation), [ADR-0020](0020-spa-served-by-the-bff.md) (same-origin serving); gh#645, gh#219.

## Context
The SPA is specified to consume "the BFF's REST endpoints **and SignalR hubs**" (architecture, *Analysis &
management UI*), and gh#23 [U1] carries the client half ("SignalR client + idempotent-resume"). The server half did
not exist — every surface was poll-only, which the live blotter, the flatten countdown and fill markers cannot use.
Three tensions make the contract worth recording:

- **Authorization is on the connection, not the request.** A subscription is long-lived, so the R-18 token check
  and R-20 scoping are decided **once at connect** and hold for the socket's life — a different shape from the
  per-request filter the REST API uses.
- **The hub must never become a command channel.** A hub method that placed an order would be a *second* path to a
  broker; there is exactly one (`OrderExecutionService`), and enforcement lives below the model.
- **Resume must be idempotent.** A client that drops and reconnects has to catch up without double-applying.

## Decision
- **One authenticated hub** at `/hubs/realtime`, `RequireAuthorization()`. Because a WebSocket cannot send an
  `Authorization` header, the JWT arrives on the **`access_token` query string** and is lifted onto the validated
  token by `JwtBearerEvents.OnMessageReceived` **scoped to the hub path** — no other route accepts a query-string
  token (R-18).
- **Presentation-only — the hub exposes no invocable method** (asserted structurally, not just intended). Every
  state change keeps going through the gated REST endpoints and ADR-0007's single send path.
- **What it pushes** is an **explicit allow-list** (`RealtimeEventCatalog`), not everything on the log: global
  market data (R-22) and the operator-wide safety strip — kill switch + auto-flatten (R-13/R-16). The event log is
  a **shared/global journal by construction** (its rows are never `IUserOwned`), so these broadcast to every
  authenticated connection. **Owner-scoped data — orders, fills, suggestions — is not on the log** and reaches
  clients through separate per-owner seams (gh#683/gh#684), never this hub.
- **Resume is at-least-once.** A reconnecting client names its last-applied sequence via `?after=`; the hub replays
  the durable events after it to that caller, then the live fan-out takes over. Overlap at the boundary is
  deliberate — the client **dedupes by the monotonic `sequence`**, so replay-then-live is gap-free without being
  exactly-once (matching the event log's own discipline, ADR-0001). A cursor that has fallen off the 24h retention
  window, or a resume beyond a bounded size, is reported as a **gap** (the client re-fetches state over REST), never
  silently skipped.
- **Restart catch-up is bracketed; a cold start is silent.** The fan-out's cursor is durable (a committed consumer
  group, ADR-0001), so a restart resumes exactly where it left off — nothing is skipped. But the events that accrued
  while it was down are **history, not live**: broadcasting them "live at once" would render a *historical*
  kill-switch / auto-flatten as a **live safety banner**. So a restart **brackets** its backlog — a `realtimeCatchUp`
  **Started**, the missed events replayed as history, then a `realtimeCatchUp` **Completed** and live from there — and
  the presentation decides how to render a catch-up, never mistaking it for live. A **cold first start** (no committed
  cursor) is not an outage — its "backlog" is the whole log — so it catches up to head **silently**, emitting no
  bracket; a fast restart already at head emits none either.
- **Lifecycle.** A hosted fan-out tails the log with a **fresh DI scope per pass** and a **clean stop-token exit**
  (the `StopPromotionHost` discipline, gh#153) so it never outlives the host or cascades an `ObjectDisposedException`.

## Alternatives considered
- **Poll-only (status quo).** Simplest, but no live blotter, countdown, or fill markers. Rejected.
- **Exactly-once delivery.** Per-connection acknowledgement + server-side replay buffers — heavy, and needless once
  the client dedupes by sequence. Rejected in favour of at-least-once + client dedupe.
- **A command-capable hub** (place/cancel over the socket). Rejected outright: a second path to the broker breaks
  "enforcement lives below the model" and ADR-0007's single send path.

## Consequences
**Positive** — live push for the chart and the always-visible safety strip; one contract gh#649 builds its client
on; the same at-least-once discipline end to end.
**Negative / costs** — the client must dedupe by sequence and handle a gap by re-fetching (documented here so gh#649
can rely on it); owner-scoped push (orders/fills/suggestions) is deferred to gh#683/gh#684, so those surfaces stay
poll-until-refresh until then.

## Follow-ups
- The per-owner broadcast seams for **orders/fills** (gh#683) and **suggestions** (gh#684) — net-new in-process
  notifications at the write paths, routed with `Clients.User(owner)` and a custom `IUserIdProvider` (the `sub`
  claim, since `MapInboundClaims = false`).
- QA hub E2E (independent, ADR-0006/§3): unauthenticated connection refused, resume-without-gaps-or-double-delivery,
  and the no-command-channel guarantee under a live SignalR runtime.
- **Landed** (gh#683): the **order/fill** half of the per-owner seam — `AccountEventIngestionService` pushes a fill /
  order-state change to the owning operator (`Clients.User`, via a custom `IUserIdProvider` resolving the `sub`
  claim) **after** the journal write commits, best-effort so a hub failure can never affect the write.
  `realtimeOrderState` is the **complete** order-status stream (fill-driven PartiallyFilled / Filled *and* terminal
  Cancelled / Rejected); `realtimeFill` carries each execution. The **suggestion** half (gh#684) lands incrementally — see below.
- **Landed** (gh#649): the **client** this contract was written for (SPA `src/realtime/`). A single connection owns
  reconnect and resume — it rebuilds with `?after=<lastSequence>` on every (re)connect (manual, *not*
  `withAutomaticReconnect`, whose build-time URL would re-send the original cursor and miss the outage backlog) and
  dedupes by the monotonic `sequence`, so replay-then-live is a no-op; a `realtimeGap` re-fetches over REST, and the
  `realtimeCatchUp` bracket tags replayed events as history, not a live safety banner. Owner-scoped order/fill pushes
  carry no sequence and are live-only, so a *reconnect* re-fetches that state (there is nothing to replay). The
  connection state is surfaced to the operator (declared-unknown over stale, R-19 / ADR-0013).
- **Landed** (gh#684, partial): the **issued / superseded** half of the suggestion seam. The trigger scan
  (`TriggerEvaluationService`) queues a compact `realtimeSuggestion` (the id, the new `SuggestionState`, and when) as
  it stages a new `Active` row and, on a supersede, as it voids the incumbent to `ExpiredVoid`; both flush **after**
  the pass's single `SaveChanges` commits, per-owner (`Clients.User`) and best-effort, so a hub fault can never fail
  or roll back the write.
- **Landed** (gh#718): the **drift → Stale** (gh#546) and **expiry → ExpiredVoid** (gh#545) half. Each is a
  database-evaluated, set-based `ExecuteUpdate` across *all* owners that returned only a *count*, so the seams
  (`ISuggestionDrift` / `ISuggestionExpiry`) now **recover the affected `(SuggestionId, UserId)` rows** — a
  **read-then-update in one transaction**, the update re-applying the same prior-state-guarded predicate so the
  monotonic compare-and-swap and the single-UPDATE `StateChangedAt` stamp do not regress (gh#546). After the write
  commits, `SuggestionDriftService` and the expire sweep (`SuggestionExpiryHost.ExpireAndNotifyAsync`) push one
  `realtimeSuggestion` per affected row to its owner through the same seam, per-owner (`Clients.User`) and
  best-effort — a hub fault only logs and never fails or unwinds the transition. The startup **recovery-expire**
  pass does not push (no operator is connected yet; the card loads current state over REST on connect). The exact
  `(id, owner)` recovery is proven on container Postgres by QA.
- The payload is the compact signal by design (id + state), never the full projection: the card surface (gh#654)
  upserts by id and reconciles against the REST read model, which stays the source of truth.
- **Landed** (gh#760): the **client** now consumes `realtimeSuggestion`. `messages.ts` adds the method and its
  compact type; `connection.ts` listens and fans out a new **`onSuggestion`** (owner-scoped, live-only — no
  `sequence`, outside the resume replay, exactly like order / fill); `RealtimeProvider` exposes
  `onSuggestion(handler)`. On each push the **suggestion panel** (gh#654) and the **chart zones** (gh#727) re-fetch
  over REST — and on a **reconnect** too (`onResync`), since a live-only push dropped with the socket is never
  replayed — so a new / superseded suggestion updates both without a poll or reload. A degraded socket still surfaces
  R-19 — the chart **zones** self-label **stale**, and the global connection indicator marks the view not-live (the
  panel carries no per-surface stale badge of its own — superseded by gh#874 below). Refining the note above: the panel reloads the whole **R-4 actionable list** rather
  than upserting one id, because a supersede changes *set membership* (the incumbent leaves as a new row appears)
  and the server owns that set; the reload is a **soft** refresh — it updates on success and keeps the current list
  on a failed background read, so a reconcile *signal* never nukes a working decision surface.
- **Landed** (gh#874): the panel **now does** carry a per-surface stale badge, closing the R-19 gap the gh#760 note
  above left. Keeping the list on a failed background refresh is right, but a suggestions REST read failing while the
  socket stays `live` (global indicator green) is a degraded state the panel would otherwise hide. It now tracks
  whether its last background refresh failed and, while set, shows a subtle, non-destructive "may be out of date"
  affordance — never an error screen, and never on the loading / error states (which own their own screens) — cleared
  by the next successful load or refresh. It shows over the **empty** state too, not just a populated list: an empty
  panel whose last refresh failed is the worst case, a just-issued suggestion hidden behind a confident "nothing
  proposed". This is the **honest-states** stance R-19 / ADR-0013 asks for, and that `useExecutionOverlays` /
  `useFillMarkers` take — they blank and mark **unavailable** on a failed read; the panel instead **keeps** the list
  and flags it (same principle, since a stale decision list is more useful to act against than a blank one).
