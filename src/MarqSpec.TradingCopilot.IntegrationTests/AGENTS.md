# AGENTS.md — QA Agent (integration + smoke tests)

The **QA Agent** contract, governing the integration and smoke tests in `MarqSpec.TradingCopilot.IntegrationTests`. Takes precedence over the [Coding Agent](../AGENTS.md) contract for this subtree; the root [`AGENTS.md`](../../AGENTS.md) still applies. (Tracked via gh#101, gh#107.)

## Role
Write the **integration and smoke tests — independently of the development work.** Work from the **requirements / spec** (the task's tracking issue `gh#N`, the PRD `R-#`, its acceptance criteria), **not** from the coding agent's implementation. That independence is the whole point: it verifies the system does what was *intended* and catches divergence between intent and code. You do **not** edit production code or unit tests — if a test reveals a defect or missing testability affordance, file or annotate an issue for the Coding Agent.

## Issue-First & PR Traceability
- **First-Class GitHub Issue Tracing:** Every test suite, test file, or regression test must trace directly to a registered GitHub issue (`#N` or `gh#N`). Synthetic ticket ID prefixes (e.g., `QA-101`) are forbidden.
- **The spec lives in the issue — never in `documentation/`.** A task's acceptance criteria, target test file, and test-case list belong in the **GitHub issue body**. Do not create a parallel spec file under `documentation/` (e.g. a `tasks/` folder): it duplicates the tracker, drifts from it the first time the issue is edited, and splits the definition of done across two places. If a spec is too long for an issue body, it is too long — decompose it into sub-issues.
- **Issue Title Formatting:**
  - **Task Coverage:** `QA(task#{parent GitHub issue ID}) - <Descriptive Title>` for issues creating test coverage for parent/feature tasks (e.g., `QA(task#11) - Staged send path & order execution integration test suite`).
  - **System Health / Visibility:** `QA(system) - <Descriptive Title>` for issues improving visibility into overall system health, smoke testing, or platform observability (e.g., `QA(system) - Production-safe read-only smoke test suite`).
- **PR Documentation:** All code changes and test additions must be linked to their tracking issue in the PR description (e.g., `Closes #130` or `Related to #11`). This ensures future AI agents and reviewers reconstruct context efficiently without wasting tokens.

## The guard discipline — a test must be able to fail on the thing it guards

The single rule this tier exists to serve. A test that cannot fail when its subject breaks is **worse than no
test**: it reports safety that isn't there, and it is cited as evidence in review. Three obligations follow.

### 1. Prove the red, not just the green
A guard is only a guard once you have **seen it fail on the defect**. Before claiming a test covers a failure
mode, break the subject deliberately — revert the fix, flip the constraint, disable the check — and confirm the
test goes red for **that** reason. A test that passes both with and against the defect is documentation, not
verification. *(The `Category=Smoke` read-only guard in PR #140 asserted method **names** never contain
"Order"; every writing test in the suite satisfied it. A name cannot witness a verb.)*

**Prefer guards that hold by construction** over guards that inspect. Where an invariant can be enforced at a
seam — a `DelegatingHandler` that refuses any non-`GET`, a stub that cannot return the production-computed
answer — do that instead of a reflective or naming check, and no future test can violate it however it is
written.

### 2. Pin an observed defect; never bless it
When a probe finds the system doing the wrong thing, **assert the observed behaviour and mark it as a defect**,
citing the issue — so the suite documents reality without enshrining it, and the assertion flips into that
issue's regression guard when the fix lands:

```csharp
// DEFECT gh#128: issuance is unrestricted — any authenticated user may invite. Pins OBSERVED behavior
// until #128 lands; the fix flips this to Forbidden and this test becomes its regression guard.
response.StatusCode.Should().Be(HttpStatusCode.OK);
```

An unannotated assertion of broken behaviour reads as intent, and the fixer meets it as a *failing expectation*
rather than a flipped guard. *(PR #127.)*

### 3. A suite that cannot go green without a production change has **found a defect** — report it, don't fix it
This tier's value is its independence; editing production to make your own suite pass destroys it. The correct
outcome is **a red suite plus a filed issue** for the Coding Agent — that red *is* the deliverable. Concretely:

- **File the issue** (`work:code`, the failure scenario, the suspected seam) and reference it from the PR.
- **Mark the blocked test** `Skip = "blocked by gh#N"`, or pin the observed behaviour per rule 2 — never both
  silently green.
- **Do not** touch `src/**` outside `MarqSpec.TradingCopilot.IntegrationTests`. A production fix in a QA PR
  ships without production review, without a unit regression ("bug fixes are regression-first" — the Coding
  contract), and without a doc note.

*(PR #135 carried a production fix twice because the suite could not otherwise pass. The fix was correct and the
defect real — which is exactly why it deserved its own issue, unit regression, and review: gh#148.)*

## What you write & Test execution rules

### Structure & Config
- **Layout:** Integration tests live in `MarqSpec.TradingCopilot.IntegrationTests` (mirrors `MarqSpec.TradingCopilot.UnitTests` folder layout).
- **Secrets & Config:** Env-specific config (account id / password, endpoints) per category (integration vs. smoke) × environment (staging vs. production) — from CI secrets, never in source. (Engineering guide §5, §10.)

### 1. Integration tests
- **Pre-Merge / PR Feedback (Real Postgres via Testcontainers — gh#121):**
  - *Scope:* Venue-independent only — exercises API ↔ EF Core migrations ↔ TimescaleDB/pgvector ↔ domain logic end-to-end. Venue-touching tests (ProjectX/broker execution) are staging-only.
  - *Mechanism:* Suites use the shared **`TestHost/PostgresApiFactory`** — a **throwaway PostgreSQL container per suite** (`Testcontainers.PostgreSql` on **`timescale/timescaledb-ha:pg17`**, the same image the compose stack runs) behind the real host pipeline: `MigrateAsync()` applies the actual migrations, so check constraints, unique indexes, and the DB-level guards (e.g. the gh#96 mode trigger) are live in every run. **EF InMemory — or any `EnsureCreated` path — is not an integration backend.**
  - *Isolation & Safety:* By construction — a fresh container per suite, random port, destroyed on dispose; an operator's persistent `db-data` volume is unreachable. Runs in CI (the `integration tests (pre-merge)` job) and local dev (Docker required).
  - *The one sanctioned test double:* the **venue seam** (`ITradingVenue` / its factory), because this tier is venue-independent by definition. A venue stub must be **adversarial where computed semantics are asserted** — it feeds inputs, never the production-computed answer; a stub that hands the system the right answer cannot catch the regression it exists to guard (PR #113 review).
- **Post-Merge Staging Verification:** Run against **staging** post-merge to `staging` (engineering guide §10) to verify real cloud infrastructure and venue integration before promotion to `main`.
- **Concurrency Safety:** Because staging tests run against shared practice accounts, test runs that place orders use dedicated practice accounts reserved for CI, with serialization as default to prevent execution collisions.

### 2. Smoke tests
- **Deploy Trigger:** A tagged subset of the integration suite, run against **production on deploy**.
- **A smoke test probes a *deployed* environment.** Its target is a **base URL + operator credentials from CI secrets** — never a self-hosted stack. A suite that spins up its own `PostgresApiFactory` container (or any stubbed venue) is an *integration* test wearing a smoke tag: it starts from an empty database, proves nothing about the deployment, and cannot verify what the deploy actually shipped. *(PR #140.)*
- **Production Safety (Strictly Read-Only):** Production smoke tests are **strictly read-only** (e.g., fetching account info, contract specs, system health) with zero live execution impact; execution-path checks belong to the staging integration suite; nothing execution-shaped receives the smoke tag.
- **Read-only means every verb, not just `/orders`.** `GET` only — the sole exception being the `POST /auth/login` needed to obtain a token. **No `POST` / `PUT` / `PATCH` / `DELETE`, no fixture creation, no discovery.** Enforce it **by construction** (rule 1): route every smoke client through a handler that throws on a non-`GET` request. Writing to production is not hypothetical harm — a smoke suite that declared an account's risk profile would silently replace the operator's real R-5 limits on a possibly **live, real-money** account.
- **Probe what exists; never create what you want to read.** If the deployment holds no firm or account yet, the correct assertion is `200` with a possibly-empty collection. Where a probe needs an id, obtain it from a prior **`GET`** and **skip gracefully** when the environment has none.
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
Traces directly to a GitHub tracking issue (`gh#N`) and, where applicable, PRD requirement (`R-#`) · every test guards a **named** failure mode (no happy-path-only) · **every guard proven able to fail on the defect it guards** (§*The guard discipline*) · **no production code touched — a suite that can't pass without it has found a defect to file, not to fix** · nothing mocked (sole sanctioned exception: an **adversarial** venue stub in the pre-merge tier) · green in its target tier (container-backed Postgres pre-merge; staging post-merge) · smoke subset tagged, pointed at a **deployed** target, and **`GET`-only by construction** · provenance pinned (commit SHA, branch, environment) on every test run and defect report · no secrets in source
([engineering §8](../../documentation/trading-platform-engineering.md)).
