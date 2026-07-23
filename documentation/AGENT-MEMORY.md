# AGENT-MEMORY.md

**Purpose — the agent catch-all.** This file is where AI coding agents (Claude Code, Copilot, or any other)
record and communicate things that must persist across sessions but **don't fit any other formal document**:
practices Adam has asked us to follow, cross-agent heads-ups, and decisions that don't yet have a home in the
PRD, the engineering guide, `AGENTS.md`, or the code.

**It is deliberately informal, and it is overflow — not a substitute.** The formal documents remain
authoritative. If something belongs in the PRD (product requirements), the engineering guide
(`documentation/trading-platform-engineering.md`), `AGENTS.md` (agent rules), or the code — **put it there
instead.** This file is for what would otherwise be lost between sessions because it fits nowhere formal.

**How to use it**
- **Read it before starting work** — another agent (or Adam) may have left guidance here.
- **Append, don't overwrite.** Add entries under the right section and date them (`YYYY-MM-DD`) so the history
  stays legible.
- **Promote when it grows up.** If an informal note here becomes stable enough to belong in a formal doc, move
  it there and leave a one-line pointer behind.
- Keep entries terse and concrete — this is shared working memory, not an essay.

---

## Practices to follow

Working practices Adam has asked agents to follow that have no formal-doc home.

- **[2026-07-18] Lightweight scaffold first; stay agile.** For planning / requirements / engineering-practice
  docs, build a *minimal* scaffold first — enough structure and known decisions to reference and decide
  against, with open choices flagged (a `Decide:` marker) — rather than an exhaustive standard up front. Don't
  over-invest until there's a substantial plan; deepen sections only as the plan firms up.
- **[2026-07-18] Apply, then review.** Make non-trivial changes directly in the files and let Adam review the
  diff (version control makes it safe), rather than proposing every change for approval first. Trivial factual
  corrections always go straight in.
- **[2026-07-22] Always use `git worktree` for isolated work.** When working on features, tests, or fixes, use isolated `git worktree` directories (or `Workspace: "share"` subagents) to prevent stepping on or dirtying other active agents' working trees.

## Notes & communications

Cross-agent heads-ups and in-session decisions that don't have a formal home yet.

- **[2026-07-23] QA Integration & Smoke Test Backlog (Audit Doc: [`documentation/integration-test-audit.md`](integration-test-audit.md)):**
  - **Issue #130 — `QA(task#11) - Staged send path & order execution integration test suite`** (Spec: [gh#130](https://github.com/adammarquette/trading-copilot/issues/130)): Integration suite for `POST /accounts/{id}/orders`, `POST /orders/arm`, `PUT /orders/{id}`, `POST /orders/{id}/take`, `DELETE /orders/{id}` testing fail-closed risk checks, credential process guard (ADR-0015), `WorkingStopPrice` DB persistence (`gh#134`), dual `Order` + `GateDecisionRecord` persistence, and R-14 mode x environment refusals against Testcontainers Postgres.
  - **Issue #132 — `QA(task#128) - Multi-tenant workspace & resource isolation integration suite`** (Spec: [gh#132](https://github.com/adammarquette/trading-copilot/issues/132)): R-20 default-deny workspace isolation test suite verifying User A's resources (connections, accounts, risk profiles, staged orders, gate decisions) are completely invisible (`HTTP 404` / empty `[]`) to User B (ADR-0017).
  - **Issue #131 — `QA(system) - Production-safe read-only smoke test suite`** (Spec: [gh#131](https://github.com/adammarquette/trading-copilot/issues/131)): Production deploy smoke suite tagged `Category=Smoke` covering read-only endpoints (`GET /auth/me`, `/firms`, `/connections`, `/connections/{id}/accounts`, `/accounts/{id}/risk`).
  - **Issue #142 — `QA(task#7) - Connection credential lifecycle & account stage resolution integration suite`** (Spec: [gh#142](https://github.com/adammarquette/trading-copilot/issues/142)): Suite covering `POST /connections`, `GET /connections/{id}`, `PUT /connections/{id}/credentials`, `DELETE /connections/{id}`, and `PUT /accounts/{id}/stage` validating credential rotation, soft-delete cascading, and firm stage convention enforcement.
  - **Issue #143 — `QA(task#10) - Risk profile dynamic trailing drawdown & floor tracking integration suite`** (Spec: [gh#143](https://github.com/adammarquette/trading-copilot/issues/143)): Suite validating dynamic risk profile updates under live trading conditions (`POST /accounts/{id}/risk`) and immediate risk limit enforcement on staged orders.

---

*Part of the repo's living memory for agents. If you're an agent reading this: check the sections above, keep
entries current, and leave things better than you found them.*

