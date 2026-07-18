# AGENTS.md — Trading Co-Pilot (root)

Instructions for AI coding agents working in this repository. This project follows the organization and
engineering practices of its predecessor, **agent-forge-copilot**, adapted for a personal futures trading
co-pilot. Nested `AGENTS.md` files (added under `src/`, `tests/` as those appear) take precedence for their
subtree.

## What this repo is
A single-operator **futures day-trading co-pilot** — a human-in-the-loop decision-support **and** execution
system with a safety-critical **auto-flatten** before the CME close. C# / .NET, integrating with the broker via
`MarqSpec.Client.ProjectX`. The `src/` solution today holds only a throwaway placeholder (`TradingCopilot.StubProject`), to be deleted once the real projects are established; read the docs before building.

## Source of truth (read before coding)
**Start at `README.md`, then the `documentation/` folder** — it is authoritative; this file only summarizes and
points to it.
- [`documentation/trading-platform-prd.md`](documentation/trading-platform-prd.md) — product requirements
  (`R-1…R-17`); every capability traces to one.
- [`documentation/trading-platform-engineering.md`](documentation/trading-platform-engineering.md) — architecture
  mapping + the engineering-practices scaffold (stack, testing, observability, deployment, safety-critical
  discipline). *(An `INDEX.md` front door will be added as the doc set grows.)*

## Agent memory — `AGENT-MEMORY.md`
[`AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) (in `documentation/`) is the **catch-all for things agents must remember or
communicate that don't fit any formal document** — practices Adam has asked us to follow, cross-agent
heads-ups, decisions without a formal home yet. **Check it before starting work**, and record such items there
(dated). If something *does* fit a formal doc (the PRD, the engineering guide, this `AGENTS.md`, or code), put
it there instead — `AGENT-MEMORY.md` is overflow, not a substitute.

## Universal rules (settled so far — the engineering guide holds the detail)
- **.NET 10 (LTS), C# latest, `Nullable` on, warnings-as-errors, file-scoped namespaces.** Inherit AgentForge's
  conventions: immutability by default, DI through the constructor, async-all-the-way with `CancellationToken`,
  structured logging via `ILogger` (no interpolation).
- **Postgres + pgvector** over EF Core for relational / vector / time-series data.
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
- **Branch off `develop`** (the default branch); promote `develop` → `staging` (test/review) → `main` (production) — never branch off or PR directly into `main`.
- **Practice accounts only outside production.** dev/staging connect to ProjectX **practice** accounts (real execution path, no real money); a live real-money account is **production-only** — never wire one into a lower environment.

## Build / test
- Solution: `src/TradingCopilot/TradingCopilot.slnx` — only the throwaway `TradingCopilot.StubProject` today. Build: `dotnet build`.
- Test tiers, as they're added: **unit** (mocked) · **integration** (real deps in a practice/QA env) ·
  **deterministic evals**. Before a PR: `dotnet format --verify-no-changes` + unit tests green.

*This file is a lightweight, evolving scaffold — it deepens as the plan and `src/` do.*
