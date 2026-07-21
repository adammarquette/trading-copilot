# AGENTS.md — Trading Co-Pilot (root)

Instructions for AI coding agents working in this repository — a **self-hosted, single-operator** futures trading
co-pilot. This root file holds the rules that apply everywhere; **two role-specific contracts take precedence in
their subtree**:
- **Coding Agent** — [`src/AGENTS.md`](src/AGENTS.md): production code + unit tests (test-first).
- **QA Agent** — [`src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md`](src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md): integration + smoke tests, written *independently* of the coding work.

## What this repo is
A **self-hosted futures day-trading co-pilot** — a human-in-the-loop decision-support **and** execution
system with a safety-critical **auto-flatten** before the CME close. C# / .NET, integrating with the broker via
`MarqSpec.Client.ProjectX`. The `src/` solution (**`MarqSpec.TradingCopilot.slnx`**, base namespace `MarqSpec.TradingCopilot.*`) builds out per the roadmap — `Domain`, `Data` (EF Core), the `Api` (BFF), + test projects so far; read the docs before building.

## Source of truth (read before coding)
**Start at `README.md`, then the `documentation/` folder** — it is authoritative; this file only summarizes and
points to it.
- [`documentation/trading-platform-prd.md`](documentation/trading-platform-prd.md) — product requirements
  (`R-1…R-21`); every capability traces to one.
- [`documentation/trading-platform-engineering.md`](documentation/trading-platform-engineering.md) — architecture
  mapping + the engineering-practices scaffold (stack, testing, observability, deployment, safety-critical
  discipline). *(An `INDEX.md` front door will be added as the doc set grows.)*
- [`documentation/trading-platform-architecture.md`](documentation/trading-platform-architecture.md) — runtime
  architecture: services, event pipeline, data flow, open design decisions.
- [`documentation/data-dictionary.md`](documentation/data-dictionary.md) — the data-model catalog (entities,
  fields, storage tier), kept in lockstep with the `MarqSpec.TradingCopilot.Data` entities + `dotnet ef` migrations.
- [`documentation/deployment-runbook.md`](documentation/deployment-runbook.md) — deployment resources + procedures
  (Railway, environments↔branches, secrets, CI/CD, deploy/rollback).

## Agent memory — `AGENT-MEMORY.md`
[`AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) (in `documentation/`) is the **catch-all for things agents must remember or
communicate that don't fit any formal document** — practices Adam has asked us to follow, cross-agent
heads-ups, decisions without a formal home yet. **Check it before starting work**, and record such items there
(dated). If something *does* fit a formal doc (the PRD, the engineering guide, this `AGENTS.md`, or code), put
it there instead — `AGENT-MEMORY.md` is overflow, not a substitute.

## Universal rules (settled so far — the engineering guide holds the detail)
- **.NET 10 (LTS), C# latest, `Nullable` on, warnings-as-errors, file-scoped namespaces.** Also: immutability by default, DI through the constructor, async-all-the-way with `CancellationToken`,
  structured logging via `ILogger` (no interpolation).
- **Coding conventions follow Microsoft's C# guidelines** (engineering §4 / [wiki](documentation/wiki/pages/dotnet-coding-conventions.md)), with one firm deviation: **define queries in fluent / method syntax — `.Where(x => …).Select(…)`, never LINQ query-comprehension (`from … select …`)** — everywhere, EF Core included.
- **Postgres over EF Core** with **TimescaleDB** (time-series — the bulk of the data) and **pgvector** (vectors: rulebook + AI-decision/retrieval data); **Cohere** for embeddings + rerank on decision-making / chat retrieval.
- **Data layer:** entities/storage types in **`MarqSpec.TradingCopilot.Data`**; **EF Core** is the default (raw SQL only with a good, e.g. perf, reason); schema changes via **`dotnet ef` migrations**.
- **No secrets in source** — Options pattern + environment; broker credentials server-side only.
- **Dependencies via Central Package Management** (`Directory.Packages.props`); respect license caps (e.g.
  FluentAssertions `[6.12.0,8.0.0)`).
- **Test-first is the Definition of Done** — no new public method without a failing test written first. The
  safety-critical paths (risk gate, execution, auto-flatten, kill switch) carry their own high-rigor suites.
- **Enforcement lives below the model** — the risk / execution gate enforces limits; the LLM only *proposes*.
  Never rely on prompt text to hold a risk limit.
- **Integrate brokers only through the venue abstraction** (PRD R-17): broker-specific code lives in a per-venue
  adapter (`MarqSpec.Client.ProjectX` is the v1 adapter); the core depends on venue-neutral interfaces — don't
  scatter venue-specific calls.
- **Commits:** Conventional Commits; add an `Assisted-by:` trailer for AI-authored changes.
- **Issues & PRs on GitHub (`adammarquette/trading-copilot`); issue-first.** Every PR references a tracking issue opened *before* it (`Closes #N` / `Related to #N`) — no orphaned PRs. Cite issues/PRs like doc sections (`gh#N`). Planning/progress is tracked on the GitHub **Project board** (may span related repos).
- **Docs in lockstep — the same-PR rule.** Any change whose behavior, data model, API, or UX a document describes must update that document **in the same PR** — the PRD (`R-#`), the data dictionary **and its ERD**, the wireframes, the ADRs, this file. A PR whose changes aren't reflected in the docs is **not done**. (Engineering guide §10.)
- **All new work branches off `develop`** and PRs back into it — `develop` is the sole integration branch. Promotion is a one-way ladder with **exactly one allowed source per step**: `staging` takes **`develop` only** (any other source is an exception needing a stated, good reason in the PR), and `main` takes **`staging` only — no exception**, so production history stays single-source. Never branch off or PR directly into `main`. Name branches **`<type>/<work-item-id>_<title>`** (`feature` | `bug` | `hotfix`; the tracking issue #) — see [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Practice accounts only outside production.** dev/staging connect to ProjectX **practice** accounts (real execution path, no real money); a live real-money account is **production-only** — never wire one into a lower environment.

## Build / test
- Solution: `src/MarqSpec.TradingCopilot.slnx` (base namespace `MarqSpec.TradingCopilot.*`) — `Domain`, `Data`, `Api` + test projects so far. Build: `dotnet build src/MarqSpec.TradingCopilot.slnx`.
- Test tiers, as they're added: **unit** (mocked) · **integration** (real deps in **staging**) ·
  **deterministic evals**. Before a PR: `dotnet format --verify-no-changes` + unit tests green.
- **Unit tests → `MarqSpec.TradingCopilot.UnitTests`:** xUnit + FakeItEasy + FluentAssertions, fully mocked (suite runs
  in seconds). Test **every public product method**; folders mirror the namespace; name tests
  `MethodUnderTest_Should{ExpectedBehavior}_When{condition}`. (Engineering guide §5.)
- **Integration tests → `MarqSpec.TradingCopilot.IntegrationTests`** (mirrors UnitTests layout): nothing mocked, run
  against **staging** (not local dev); a tagged **smoke** subset runs on production deploy. Env-specific config
  (creds/endpoints) per category × environment, from CI secrets. Prod deploy & rollback are human-approved.
  (Engineering guide §5, §10.)

*This file is a lightweight, evolving scaffold — it deepens as the plan and `src/` do.*
