# Architecture Decision Records (ADRs)

Short, numbered, **immutable** records of significant architecture decisions — the *why* behind the design.
Nygard-style (Context · Decision · Consequences), named `NNNN-slug.md`. Once **Accepted**, an ADR isn't
rewritten — a later ADR **supersedes** it. Referenced from the
[architecture doc](../trading-platform-architecture.md).

| ADR | Title | Status |
|---|---|---|
| [0001](0001-event-backbone.md) | Event backbone — append-only Timescale event log | Accepted |
| [0002](0002-observability.md) | Observability — OpenTelemetry with Prometheus, Loki, and Tempo | Accepted |
| [0003](0003-authentication.md) | API authentication — JWT, RBAC-ready authorization | Accepted · extended by [0011](0011-multi-user-tenancy.md) |
| [0004](0004-charting.md) | Charting — Lightweight Charts + bespoke canvas/WebGL for order flow | Accepted |
| [0005](0005-ui-design-language.md) | UI design language — dark-first Material, adaptive layout | Accepted |
| [0006](0006-multi-screen-workspace.md) | Multi-screen workspace — detachable pop-out panels (web-native) | Accepted |
| [0007](0007-order-execution-model.md) | Order execution & risk-gate model — one enforcing gate; arm/edit/send; native/synthetic + safety stop; daily governor | Accepted |
| [0008](0008-ai-invocation-cost-model.md) | AI invocation & cost model — deterministic triggers; LLM event-driven at the edges; cheaper-model triage | Accepted |
| [0009](0009-backtesting.md) | Backtesting & historical simulation — engine parity; cheaper-model, news-light; look-ahead-safe | Accepted |
| [0010](0010-progressive-web-app.md) | Progressive Web App — installable SPA; Android primary, iOS best-effort; presentation-only, safety server-side | Accepted |
| [0011](0011-multi-user-tenancy.md) | Multi-user tenancy & data isolation — User = tenant root; row-level `user_id` scoping (default-deny); per-user broker creds | Accepted |
| [0012](0012-containerization-local-dev.md) | Containerization & local development — Docker images (local ≡ Railway); `docker compose up`; DB config-driven | Accepted |
| [0013](0013-failure-recovery-model.md) | Failure & recovery model — client resume, state rehydration, suggestions expire, native-stop + orphan handling, the auto-flatten watchdog (open) | Accepted |
| [0014](0014-news-importance-feedback.md) | News importance feedback & personalized weighting — per-user star raises salience of similar future news; soft weight, not a risk control | Accepted |
| [0015](0015-distribution-licensing-governance.md) | Distribution, licensing & governance — self-hosted fork-first; Apache-2.0; maintainer-led; AI-first authorship disclosed | Accepted · refines [0011](0011-multi-user-tenancy.md) |
| [0016](0016-venue-configuration.md) | Venue configuration — adapters compiled in; firms (name, endpoint, credentials, conventions) configured in settings; full plugin contract deferred to `gh#64` | **Proposed** · extends [0015](0015-distribution-licensing-governance.md) |
