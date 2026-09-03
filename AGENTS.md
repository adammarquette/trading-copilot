# AGENTS.md — Trading Co-Pilot (root)

Rules for **every** agent in this repository — a self-hosted, single-operator futures trading co-pilot with a
safety-critical auto-flatten before the CME close. Role- and subtree-specific rules live in their own contracts,
so they cost context only when they apply.

## Take your role's contract first

| If you are… | Read first | How it loads |
|---|---|---|
| writing production code or unit tests | [`src/AGENTS.md`](src/AGENTS.md) — Coding | on your first read of a file in `src/` |
| writing integration or smoke tests | [`IntegrationTests/AGENTS.md`](src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md) — QA | on your first read in that project |
| **reviewing any change** | [`agents/code-reviewer.md`](documentation/agents/code-reviewer.md) | **open it yourself** |
| **touching CI/CD, the image, compose, or deploy** | [`agents/platform.md`](documentation/agents/platform.md) | **open it yourself** |
| **assigning work from the board, or driving a task to approval** | [`agents/coordinator.md`](documentation/agents/coordinator.md) | **open it yourself** |

The subtree contracts load by directory proximity — **lazily, when you first read a file there, not at session
start**. The role contracts follow *what you are doing* rather than where a file sits, and never auto-load.
**Wearing one of those hats without opening its contract is the most common way agents get this repo wrong.**

> Each `AGENTS.md` has a one-line `CLAUDE.md` beside it holding `@AGENTS.md`. **Those shims are load-bearing** —
> Claude Code reads `CLAUDE.md`, not `AGENTS.md`. Deleting one as "redundant" silently unloads that contract.

## What this repo is

A human-in-the-loop decision-support **and** execution system in C# / .NET; the broker is reached through
`MarqSpec.Client.ProjectX`. Solution `src/MarqSpec.TradingCopilot.slnx` (namespace `MarqSpec.TradingCopilot.*`):
`Domain`, `Data` (EF Core), `Api` (BFF), the adapters `Integration.ProjectX` / `.Finnhub` / `.Tiingo`, plus
`UnitTests` and `IntegrationTests`. Build with `dotnet build src/MarqSpec.TradingCopilot.slnx`; before a PR,
`dotnet format --verify-no-changes --exclude external/` (the vendored `external/` clients keep their own style)
and unit tests green.

## Source of truth

The markdown under [`documentation/`](documentation/) **and the GitHub issues and PRs** are the highest-level
source code of the system: the C# below is reconstructable from them. Read them as source and keep them
compiling. `R-#`, ADR numbers and `gh#N` are its symbol table.

**Route, don't read.** [`documentation/README.md`](documentation/README.md) maps every document — what it is and
when to open it. Resolve the section you need through it; **never load the corpus**. A compiler resolves symbols
on demand rather than reading every source file, and so should you.

[`AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) is the catch-all for practices with no formal home — check it
before starting, and add dated entries only when nothing formal fits.

## The five that are never traded away

- **No secrets in source.** Options pattern plus environment; broker credentials server-side only.
- **Enforcement lives below the model.** The risk / execution gate enforces limits; the LLM only *proposes*.
  Never hold a risk limit in prompt text.
- **Practice accounts only outside production.** dev/staging connect to ProjectX practice accounts; a live
  real-money account is production-only. A **third** mode exists: an `undeclared` account is refused
  **everywhere, production included** — it produces no orders at all.
- **Test-first, and done means an approved PR.** No new public method without a failing test written first;
  the safety-critical paths (risk gate, execution, auto-flatten, kill switch) carry high-rigor suites. Your
  task ends when the PR you opened is **approved and green**, and you wait for that **in this session** with
  a command, not a habit: `scripts/watch-verdict.sh checks <pr>` → **spawn a reviewer** → `watch-verdict.sh
  verdict <pr>` → fix what it returns. (Engineering §10 owns the loop; its exit statuses are the steps.)
- **Wear a hat, open its contract** — before you start, not after.

## Working rules

- **Docs in lockstep — the same-PR rule.** A change whose behavior, data model, API or UX a document describes
  updates **the affected section of that document, in the same PR** — the PRD (`R-#`), the architecture doc, the
  data dictionary (its domain page, and the ERD in the index), the wireframes, the ADRs, this file. Update the
  section, not the whole file. (Engineering §10 owns the rule.)
- **Issue-first — no orphaned PRs.** Every PR cites an issue opened before it (`Closes #N` / `Related to #N`);
  cite issues as `gh#N`. **Task specs and acceptance criteria belong in the issue**, never as files under
  `documentation/` — a parallel spec duplicates the tracker and drifts from it.
- **Maximal metadata on every issue and PR:** assignee, milestone, `work:*` and `Work Estimate` labels. Issues
  are the board cards; a PR is not carded. Epics decompose into sub-issues (issue→issue). A thin issue is a
  defect — the next agent rebuilds context from these fields.
  Detail: [board workflow](documentation/project-board-workflow.md).
- **Commits:** Conventional Commits, plus **both** an `Assisted-by:` and a `Co-Authored-By:` trailer on
  AI-authored changes. Full type list: [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Branch off `develop` and PR back into it.** `develop` is the sole integration branch, never a workspace.
  Promotion is one-way with one source per step: `staging` ← `develop`, `main` ← `staging`. Never branch off or
  PR into `main`. Name branches `<type>/<work-item-id>_<title>`. Ladder detail:
  [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Work in a `git worktree`, never in the main checkout** — `git worktree add .worktrees/<branch> <branch>`.
  Sessions run in parallel; sharing a working tree means one session's uncommitted edits land in another's
  commit. A worktree is *local*, so it isolates but does **not** claim — that is the next rule.
- **Claim before you start — `scripts/claim.sh <issue-id>`.** The **pushed** branch is the claim; a local
  worktree is invisible to parallel sessions. A tip unmoved for 4 hours is fair game — say so on the issue
  first. Two traps that cost real work — a split issue's new id, and a two-repo card whose claim hides in the
  submodule — are in [`CONTRIBUTING.md`](CONTRIBUTING.md).

*Every line here is paid by every agent in every session. Keep it small: anything role- or subtree-specific
belongs in its contract, and anything with a formal home belongs there rather than restated here.*

## Cursor Cloud specific instructions

The environment — SDKs, submodule checkout, dependency restore — is provisioned from the Cursor dashboard, not
this tree (there is no `.cursor/` here to keep it true), so **bootstrap defensively rather than assume a prior
run**: if `external/` is empty, `git submodule update --init` (the build needs the `external/MarqSpec.Client.*`
projects); if `dotnet`, Node or Docker is missing, install it. Standard commands and the full config surface
already have homes — README "Run it locally", [`.env.example`](.env.example),
[`ci.yml`](.github/workflows/ci.yml), ADR-0012, ADR-0020 — so the notes below are only the non-obvious caveats
with none.

- **Docker is not auto-started** (no init in the VM): if `docker info` fails, start it (`sudo dockerd` in a
  background/tmux session; `ubuntu` needs the `docker` group or `sudo`) before Postgres, `docker compose`, or
  the Testcontainers integration suite — that suite needs only the daemon up, not the compose `db`.
- **`dotnet run` seeds no operator on its own.** `Bootstrap__Email` / `Bootstrap__Password` are *compose*
  interpolation defaults (`operator@local` / `changeme-local`) the API process never reads; set both explicitly
  on the `dotnet run` path, or `StartupTasks.BootstrapOperatorAsync` returns at its first guard and every
  sign-in 401s with nothing logged. Separately, `Jwt__SigningKey` throws at *startup* only when absent — a
  present-but-short key (<32 bytes for HS256) boots clean and throws `IDX10653` on the first `POST /auth/login`.
- **The Vite dev server never proxies the API.** `npm run dev` (`:5173`) proxies `/health` only, and only when
  `VITE_BFF_ORIGIN` is set (otherwise nothing) — never the REST/SignalR routes, so sign-in fails through Vite
  alone. To drive the full UI against the live API, `npm run build` and copy `dist/*` into the API's `wwwroot`,
  then use the BFF at `:8080`. `wwwroot` must exist *before* the API starts (`UseStaticFiles` binds its provider
  when the host is built), so restart after populating it; that copy is a build artifact — do not commit it,
  and `.gitignore` now enforces that rather than relying on you to remember (gh#1088).
