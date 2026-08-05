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
  Cancelled / Rejected); `realtimeFill` carries each execution. The **suggestion** half (gh#684) remains.
