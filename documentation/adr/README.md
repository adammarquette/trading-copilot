# Architecture Decision Records (ADRs)

Short, numbered records of significant architecture decisions — the *why* behind the design. Nygard-style
(Context · Decision · Consequences), named `NNNN-slug.md`. Once **Accepted**, the **decision** is immutable — it is
not rewritten or reversed in place; a later ADR **supersedes** it. The *record*, though, is a living trail: it is
**extended by increment** with dated `## Update` entries under a `## Decision log`, and structural housekeeping that
**preserves every word** (the gh#492 / gh#524 decision-log treatment — re-heading and re-ordering the trail, never
touching the reasoning) is allowed. What must never change is the reasoning itself. That **structure** — `## Follow-ups`
last, the `## Decision log` index matching the `## Update` trail one-to-one, dates ascending — is checked in CI
(`scripts/check-adr-decision-log.sh`, gh#600), so a drifting append fails at PR time instead of being re-fixed by hand a
fourth time (gh#492 → gh#524 → gh#580). Referenced from the [architecture doc](../trading-platform-architecture.md).

| ADR | Title | Status |
|---|---|---|
| [0001](0001-event-backbone.md) | Event backbone — append-only Timescale event log | Accepted |
| [0002](0002-observability.md) | Observability — OpenTelemetry with Prometheus, Loki, and Tempo | Accepted |
| [0003](0003-authentication.md) | API authentication — JWT, RBAC-ready authorization | Accepted · single-operator premise restored by [0017](0017-single-operator-data-isolation.md) |
| [0004](0004-charting.md) | Charting — Lightweight Charts + bespoke canvas/WebGL for order flow | Accepted |
| [0005](0005-ui-design-language.md) | UI design language — dark-first Material, adaptive layout | Accepted |
| [0006](0006-multi-screen-workspace.md) | Multi-screen workspace — detachable pop-out panels (web-native) | Accepted |
| [0007](0007-order-execution-model.md) | Order execution & risk-gate model — one enforcing gate; arm/edit/send; native/synthetic + safety stop; daily governor | Accepted |
| [0008](0008-ai-invocation-cost-model.md) | AI invocation & cost model — deterministic triggers; LLM event-driven at the edges; cheaper-model triage | Accepted |
| [0009](0009-backtesting.md) | Backtesting & historical simulation — engine parity; cheaper-model, news-light; look-ahead-safe | Accepted |
| [0010](0010-progressive-web-app.md) | Progressive Web App — installable SPA; Android primary, iOS best-effort; presentation-only, safety server-side | Accepted |
| [0011](0011-multi-user-tenancy.md) | Multi-user tenancy & data isolation — User = tenant root; row-level `user_id` scoping (default-deny); per-user broker creds | **Superseded** by [0017](0017-single-operator-data-isolation.md) |
| [0012](0012-containerization-local-dev.md) | Containerization & local development — Docker images (local ≡ Railway); `docker compose up`; DB config-driven | Accepted |
| [0013](0013-failure-recovery-model.md) | Failure & recovery model — client resume, state rehydration, suggestions expire, native-stop + orphan handling, the redundant auto-flatten watchdog | Accepted · both flatten tiers implemented (`gh#185`, `gh#187`); rehydration `gh#221`, orphan handling `gh#209`, settlement reconcile `gh#193` |
| [0014](0014-news-importance-feedback.md) | News importance feedback & personalized weighting — per-user star raises salience of similar future news; soft weight, not a risk control | Accepted |
| [0015](0015-distribution-licensing-governance.md) | Distribution, licensing & governance — self-hosted fork-first; Apache-2.0; maintainer-led; AI-first authorship disclosed | Accepted · refined by [0017](0017-single-operator-data-isolation.md) · its per-account / singleton follow-up taken up by [0024](0024-per-credential-set-venue-clients.md) |
| [0016](0016-venue-configuration.md) | Venue configuration — adapters compiled in; firms (name, endpoint, credentials, conventions) configured in settings; full plugin contract deferred to `gh#64` | Accepted · implemented via `gh#76` (credentials divergence in its status note) · extends [0015](0015-distribution-licensing-governance.md) · gh#64 deferral resolved by [0023](0023-venue-setup-contract.md) · per-account follow-up taken up by [0024](0024-per-credential-set-venue-clients.md) |
| [0017](0017-single-operator-data-isolation.md) | One operator per deployment — isolation kept as a fail-closed safety property, not tenancy; shared venue credentials are a ToS liability; sharing becomes a JSON export/import | Accepted · supersedes [0011](0011-multi-user-tenancy.md) |
| [0018](0018-image-registry-ghcr.md) | Image registry — build once in CI, publish to GHCR (public), consumed by local (pull, dev-build override) and Railway | Accepted · extends [0012](0012-containerization-local-dev.md) |
| [0019](0019-alerting-channel-and-thresholds.md) | Alerting — Pushover behind a channel seam; three layers (direct push, rule engine, out-of-process dead-man's switch); P1/P2/P3 taxonomy + noise budget | Accepted · extends [0002](0002-observability.md) · answers PRD Q-12 · unblocks web push in [0010](0010-progressive-web-app.md) |
| [0020](0020-spa-served-by-the-bff.md) | The SPA is served by the BFF from one origin (bundle inside the same image) — no CORS boundary, same-origin R-18 token, one artifact to promote | Accepted · extends [0012](0012-containerization-local-dev.md), [0018](0018-image-registry-ghcr.md) · unblocks Phase 4 (gh#23) |
| [0021](0021-realtime-hub-contract.md) | The realtime hub — one authenticated, **presentation-only** SignalR hub; JWT on the *connection* (query-string token, hub path only); at-least-once idempotent resume from a client-named sequence; owner-scoped push deferred to gh#683/gh#684 | Accepted · extends [0001](0001-event-backbone.md), [0020](0020-spa-served-by-the-bff.md) · unblocks gh#649 · gh#645 |
| [0022](0022-trade-round-trip-pairing.md) | Trade round-trip pairing — FIFO, **per-leg** (one opening fill + its retiring closing fills), **split a spanning closing fill** across legs; refuse only genuine ambiguity; key becomes `(ClosingFillId, OpeningFillId)` so a scaled-in / reversed trade's realized P&L reaches the daily governor | Accepted · extends [0007](0007-order-execution-model.md) · resolves the gh#731 deferral · gh#759 |
| [0023](0023-venue-setup-contract.md) | Venue setup contract — a compiled-in adapter declares its own onboarding (credential schema, endpoint model, mode-reporting mechanism, capabilities); **discovery stays compile-time** (self-describe, never runtime-loaded — R-13/security); capabilities gate declaration but the gate stays enforcement; the contract is a published, versioned interface | Proposed · extends [0016](0016-venue-configuration.md) · resolves its gh#64 deferral · gh#41 |
| [0024](0024-per-credential-set-venue-clients.md) | Per-credential-set venue client lifetimes — a ProjectX client pair is built + cached **per `CredentialKey`**, not bound once; credentials resolve by key (`ProjectX__<key>__ApiKey`), the gh#92 409 becomes "no creds for key X"; **supersedes the process-singleton websocket**; single-operator/several-logins, not tenancy | Proposed · extends [0016](0016-venue-configuration.md) (its per-account follow-up) · relates to [0023](0023-venue-setup-contract.md) · gh#95 |
