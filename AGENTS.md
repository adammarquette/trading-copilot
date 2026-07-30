# AGENTS.md — Trading Co-Pilot (root)

Instructions for AI coding agents working in this repository — a **self-hosted, single-operator** futures trading
co-pilot. This file holds **only what applies to every agent**. Everything role- or subtree-specific lives in its
own contract, so it costs context only when it is actually relevant.

| If you are… | Read **first** | How it loads |
|---|---|---|
| writing production code or unit tests | [`src/AGENTS.md`](src/AGENTS.md) — **Coding Agent** | automatically, in `src/` |
| writing integration or smoke tests | [`src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md`](src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md) — **QA Agent** | automatically, in that project |
| **reviewing a change anywhere** | [`documentation/agents/code-reviewer.md`](documentation/agents/code-reviewer.md) — **Code Reviewer** | **on demand — open it yourself** |
| **touching CI/CD, the image, compose, or deploy** | [`documentation/agents/platform.md`](documentation/agents/platform.md) — **Platform Agent** | **on demand** (a stub sits in `.github/workflows/`) |

The first two are **subtree-scoped**: directory proximity loads them exactly when they apply. The last two are
**role-scoped** — they follow *what you are doing*, not where a file sits — so they are deliberately **not**
auto-loading `AGENTS.md` files. **If you take one of those hats, open its contract before you start.**

## What this repo is
A **self-hosted futures day-trading co-pilot** — a human-in-the-loop decision-support **and** execution system
with a safety-critical **auto-flatten** before the CME close. C# / .NET, integrating with the broker via
`MarqSpec.Client.ProjectX`. Solution: **`src/MarqSpec.TradingCopilot.slnx`** (base namespace
`MarqSpec.TradingCopilot.*`) — `Domain`, `Data` (EF Core), `Api` (BFF), the venue/data adapters `Integration.ProjectX` / `Integration.Finnhub` / `Integration.Tiingo`, + `UnitTests` / `IntegrationTests`.
Build with `dotnet build src/MarqSpec.TradingCopilot.slnx`; before a PR, `dotnet format --verify-no-changes` and
unit tests green.

## Source of truth (read before coding)
**The documentation layer — these markdown files and the GitHub issues/PRs — is the highest-level source code of
the system** (README §*How we work — AI-Engineering first*): it abstracts the C# below the way C# abstracts
assembler and machine code, and the system is **reconstructable from it**. Read it as source and keep it
compiling — the same-PR rule below is this layer's build rule, and the stable identifiers (`R-#`, ADR numbers,
`gh#N`) are its symbol table.

**Start at `README.md`, then [`documentation/`](documentation/)** — it is authoritative; this file only points to it:
[PRD](documentation/trading-platform-prd.md) (`R-1…R-22`; every capability traces to one) ·
[engineering guide](documentation/trading-platform-engineering.md) (stack, testing, observability, deployment,
safety-critical discipline) · [architecture](documentation/trading-platform-architecture.md) ·
[data dictionary](documentation/data-dictionary.md) (+ its ERD) ·
[deployment runbook](documentation/deployment-runbook.md) · [ADRs](documentation/adr/) ·
[board workflow](documentation/project-board-workflow.md) + [Work Estimate rubric](documentation/work-estimate-rubric.md).

[`AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) is the **catch-all** for practices Adam has asked us to follow
and cross-agent heads-ups with no formal home. **Check it before starting work**, and record such items there
(dated) — but if something fits a formal doc, put it there instead; it is overflow, not a substitute.

## Universal rules
- **Wear a hat, open its contract — before you start.** The table above is an obligation, not a signpost: the
  role contract holds the rules this file deliberately does not repeat, and the two role-scoped ones
  (**Code Reviewer**, **Platform**) never auto-load. Two that catch agents out because they bind *whatever* you
  came here to do:
  - **Reviewing?** Findings are review comments **plus a submitted state** — **Approve** (with a summary) or
    **Request changes** — never a bare comment. The state is submitted as the **reviewer identity** (a `…[bot]`
    GitHub App), never the author — GitHub blocks self-review (gh#141; setup in the deployment runbook), with a
    verdict-prefixed comment as the interim until it is live. **Merging stays the maintainer's**; an approval is a
    signal, not authority to ship, and an agent approves a diff it *reviewed*, **never one it authored**.
    Substance: [Code Reviewer](documentation/agents/code-reviewer.md).
  - **Touching CI/CD, the image, compose, or deploy?** That is the
    [Platform](documentation/agents/platform.md) contract, wherever the file sits.
- **No secrets in source** — Options pattern + environment; broker credentials server-side only.
- **Enforcement lives below the model** — the risk / execution gate enforces limits; the LLM only *proposes*.
  Never rely on prompt text to hold a risk limit.
- **Practice accounts only outside production.** dev/staging connect to ProjectX **practice** accounts (real
  execution path, no real money); a live real-money account is **production-only** — never wire one into a lower
  environment.
- **Test-first is the Definition of Done** — no new public method without a failing test written first, and the
  safety-critical paths (risk gate, execution, auto-flatten, kill switch) carry their own high-rigor suites. The
  mechanics belong to the Coding and QA contracts.
- **Commits:** Conventional Commits; add an `Assisted-by:` trailer for AI-authored changes.
- **Issue-first — no orphaned PRs.** Every PR references a tracking issue opened *before* it (`Closes #N` /
  `Related to #N`). Cite issues/PRs like doc sections (`gh#N`). **Task specs and acceptance criteria belong in
  the issue**, never as files under `documentation/` — a parallel spec in the repo duplicates the tracker and
  drifts from it. Planning/progress lives on the GitHub **Project board** (may span related repos); the
  [board workflow](documentation/project-board-workflow.md) governs its columns and the `work:*` +
  `Work Estimate` tagging that routes pickup by role and model tier.
- **Maximal metadata on every issue & PR/MR.** Populate every field it offers: **assign the current account**,
  **set the milestone** when the phase is known (the milestones *are* the phases), and apply the `work:*` +
  `Work Estimate` labels. **Issues are the board cards; a PR is not carded** — it links to its defining issue with
  `Closes #N`, which auto-closes it on merge into `develop` and moves it to *Review*. Epics decompose into
  **sub-issues** (issue→issue; a PR cannot be one). A thin issue/PR is a defect — the next agent rebuilds context
  from these fields, so err toward more. Detail: [board workflow](documentation/project-board-workflow.md).
- **Docs in lockstep — the same-PR rule.** Any change whose behavior, data model, API, or UX a document describes
  must update that document **in the same PR** — the PRD (`R-#`), the data dictionary **and its ERD**, the
  wireframes, the ADRs, this file. A PR whose changes aren't reflected in the docs is **not done**.
  (Engineering guide §10.)
- **All new work branches off `develop`** and PRs back into it — `develop` is the sole integration branch, **not
  a workspace**; never leave work uncommitted on it. Promotion is one-way with exactly one source per step:
  `staging` ← **`develop` only** (an exception needs a stated reason and the `ladder-exception` label), `main` ←
  **`staging` only, no exception**. Never branch off or PR directly into `main`. Name branches
  **`<type>/<work-item-id>_<title>`** (`feature` | `bug` | `hotfix`; the tracking issue #). **Agents must use
  `git worktree` isolation** (e.g. `git worktree add .worktrees/<branch> <branch>`) so concurrent working trees
  never dirty each other's work. Detail: [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Claim work before you start it — `scripts/claim.sh <issue-id>`** (gh#375). Sessions run in parallel and a
  local worktree is invisible to them, so **the pushed branch is the claim**: create it and push it *empty*
  first. Skipping this duplicated roughly a full session's work in one evening. A claim whose branch tip has not
  moved for **4 hours** is fair game — but say so on the issue before taking it over.

*This file is a lightweight, evolving scaffold — it deepens as the plan and `src/` do. Keep it small: every line
here is paid by every agent in every session, so anything role- or subtree-specific belongs in its contract.*
