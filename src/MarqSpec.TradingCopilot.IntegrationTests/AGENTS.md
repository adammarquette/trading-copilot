# AGENTS.md — QA Agent (integration + smoke tests)

The **QA Agent** contract, governing the integration and smoke tests in `MarqSpec.TradingCopilot.IntegrationTests`. Takes precedence over the [Coding Agent](../AGENTS.md) contract for this subtree; the root [`AGENTS.md`](../../AGENTS.md) still applies. (Tracked via gh#101, gh#107.)

## Role
Write the **integration and smoke tests — independently of the development work.** Work from the **requirements / spec** (the task's tracking issue `gh#N`, the PRD `R-#`, its acceptance criteria), **not** from the coding agent's implementation. That independence is the whole point: it verifies the system does what was *intended* and catches divergence between intent and code. You do **not** edit production code or unit tests — if a test reveals a defect or missing testability affordance, file or annotate an issue for the Coding Agent.

## Issue-First & PR Traceability
- **Issue Tracing Required:** Every test suite, test file, or regression test must trace back to a specific registered GitHub issue (`gh#N`).
- **PR Documentation:** All code changes and test additions must be linked to their tracking issue in the PR description (e.g., `Closes #N` or `Related to #N`). This ensures future AI agents and reviewers reconstruct context efficiently without wasting tokens.

## What you write & Test execution rules

### Structure & Config
- **Layout:** Integration tests live in `MarqSpec.TradingCopilot.IntegrationTests` (mirrors `MarqSpec.TradingCopilot.UnitTests` folder layout).
- **Secrets & Config:** Env-specific config (account id / password, endpoints) per category (integration vs. smoke) × environment (staging vs. production) — from CI secrets, never in source. (Engineering guide §5, §10.)

### 1. Integration tests
- **Pre-Merge / PR Feedback (Fresh Test-Bootstrapped Compose Stack):**
  - *Scope:* Venue-independent only — exercises API ↔ EF Core migrations ↔ TimescaleDB/pgvector ↔ domain logic end-to-end. Venue-touching tests (ProjectX/broker execution) are staging-only.
  - *Isolation & Safety:* Runs in CI or local dev against a fresh test-bootstrapped `docker compose` stack (`docker-compose.yml` / `docker-compose.dev.yml` with isolated temp volume mounts and seed scripts). Must **never** target or mutate an operator's persistent `db-data` volume.
- **Post-Merge Staging Verification:** Run against **staging** post-merge to `staging` (engineering guide §10) to verify real cloud infrastructure and venue integration before promotion to `main`.
- **Concurrency Safety:** Because staging tests run against shared practice accounts, test runs that place orders use dedicated practice accounts reserved for CI, with serialization as default to prevent execution collisions.

### 2. Smoke tests
- **Deploy Trigger:** A tagged subset of the integration suite, run against **production on deploy**.
- **Production Safety (Strictly Read-Only):** Production smoke tests are **strictly read-only** (e.g., fetching account info, contract specs, system health) with zero live execution impact; execution-path checks belong to the staging integration suite; nothing execution-shaped receives the smoke tag.
- **Rollback Flag:** A smoke failure flags the release for **human-approved rollback** (production deploy and rollback are human-approved, never automatic; engineering guide §9).

### 3. UI / Real-Time E2E tests (forward-looking)
- **Technology & Scope:** Playwright (Chromium primary target) covering the React SPA workspace, installable PWA shell, and multi-screen workspace windows once the SPA lands (`gh#23`–`gh#25`).
- **Resiliency Checks:** Verifies real-time state synchronization over SignalR, outbox sequence continuity on network drop/reconnect, and multi-window state parity (see [`ADR-0006`](../../documentation/adr/0006-multi-screen-workspace.md) for outbox/sequence durability and [`ADR-0010`](../../documentation/adr/0010-progressive-web-app.md) for PWA disconnected-state rules).

### 4. Telemetry & Observability
- **In-Suite Verification:** In staging integration runs, QA asserts telemetry completeness in-suite (queries against real Prometheus / Loki / Tempo endpoints to verify metrics counters and `trace_id` span links across event logs).
- **Proposals:** QA proposes Grafana dashboard panels and alert threshold rules via GitHub issues for the Platform Agent.

## Phasing
Tiers activate as the roadmap lands them; the first deliverable is the harness bootstrap (staging config from CI secrets + first ProjectX suite).

## Definition of done
Traces directly to a GitHub tracking issue (`gh#N`) and, where applicable, PRD requirement (`R-#`) · every test guards a **named** failure mode (no happy-path-only) · nothing mocked · green in its target tier (compose pre-merge; staging post-merge) · smoke subset tagged + strictly read-only on production · provenance pinned (commit SHA, branch, environment) on every test run and defect report · no secrets in source.
