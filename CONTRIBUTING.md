# Contributing

How we work in this repo. This is the contributor front-door; the **authoritative detail** lives in the
[engineering guide §10](documentation/trading-platform-engineering.md) (Git workflow, CI/CD, Definition of Done) and
the root [`AGENTS.md`](AGENTS.md) (agent contract). Source-control practices draw on Microsoft's
[Code-With Engineering Playbook — Source Control](https://microsoft.github.io/code-with-engineering-playbook/source-control/)
(wiki: [source-control practices](documentation/wiki/pages/source-control-practices.md)).

## Branching model
**All new work branches off `develop`** and PRs back into it — `develop` is the sole integration branch
(`hotfix` is the one unsettled case; see below). Changes then promote up a one-way ladder, and **each step has
exactly one allowed source**:

| Target | Allowed source | Exception |
|---|---|---|
| `develop` | any `feature` / `bug` branch | — |
| `staging` | **`develop` only** | state the reason in the PR **and** add the `ladder-exception` label |
| `main` | **`staging` only** | **none** |

The `ladder` CI check (`.github/workflows/branch-policy.yml`) validates the base/head pair on every PR into
`staging` or `main`, and requires the head branch to live in **this** repository — a fork branch merely *named*
`staging` is a different lineage, so fork contributions go to `develop`, which carries no ladder constraint. The
`ladder-exception` label is the escape hatch made explicit and auditable — it excuses a **branch** deviation into
`staging`, never a foreign repository, and deliberately has **no equivalent for `main`**.

**`hotfix` is deliberately absent from the table above.** What it branches from, and what it merges into, is
**undecided** ([gh#43](https://github.com/adammarquette/trading-copilot/issues/43)): an emergency fix that must
reach production without waiting out the full ladder is precisely what the `staging` exception and the
no-exception-for-`main` rule would have to arbitrate, and that trade-off has not been made. Nothing is in
production yet, so the question isn't live — **settle it before the first production deploy**, and until then
raise a hotfix on its issue rather than assuming a route.

**Never** branch off `main`, and never PR into it from anything but `staging` — production history stays
single-source, so every release traces back through `staging`. Note the asymmetry: `staging` has an escape
hatch for the occasional justified exception; `main` does not. Each long-lived branch deploys to its
environment (engineering §8 / §10).

## Branch naming
Name every working branch:

```
<type>/<work-item-id>_<title>
```

- **`<type>`** — one of **`feature`**, **`bug`**, or **`hotfix`**.
- **`<work-item-id>`** — the tracking **GitHub issue number** (issue-first — the issue exists *before* the branch).
- **`<title>`** — a short, kebab-case summary.

Examples:

```
feature/42_risk-gate
bug/57_flatten-timing-drift
hotfix/60_kill-switch-regression
```

Every branch traces back to the work item it delivers.

## Issue-first — no orphaned PRs
Every change starts from a **tracking issue** opened *before* the branch/PR; the PR references it (`Closes #N` /
`Related to #N`). Work is planned as epics → tasks on the GitHub **Project board**; the
[board workflow](documentation/project-board-workflow.md) governs how items flow across it (Backlog → Planning →
Current ToDo → In Progress → Review → Done) and how they're tagged for pickup and model routing. Each feature gets
a **dev task** plus an independent **QA task** (engineering §10).

## Commits
- **[Conventional Commits](https://www.conventionalcommits.org/)** (`feat:`, `fix:`, `docs:`, `chore:`, `build:`, …);
  the commit *type* drives SemVer.
- AI-authored changes carry an **`Assisted-by:`** trailer (plus `Co-Authored-By:`).
- **Docs move with the code — the same-PR rule:** any change whose behavior / data model / API / UX a doc describes
  updates that doc **in the same PR** (PRD `R-#`, the data dictionary + its ERD, wireframes, ADRs). A PR that drifts
  is **not done**.

## Pull requests
- Open against **`develop`**; reference the tracking issue.
- **Populate every field — maximal metadata.** Assign the current account, set the **milestone** (the work's
  phase), apply the `work:*` + `Work Estimate` labels, and **link the PR to its defining issue** with `Closes #N`.
  Issues are the board cards; a PR rides its issue's *Linked pull requests* rather than taking a card of its own
  ([board workflow](documentation/project-board-workflow.md)).
- **Reviews submit a verdict.** A reviewer leaves its findings as comments and **Approves** or **Requests
  changes** — never a bare comment that leaves the state ambiguous. **Merging stays the maintainer's**; an
  approval is a signal, not authority to ship.
- **Clean history — rebase-merge with curated commits (no squash, gh#104):** a branch may carry several commits
  while in progress; before merge, **interactive-rebase it into understandable units of work** — each commit a
  coherent, Conventional-typed package whose message carries the why. PRs land by **rebase-merge** (squash-merge is
  disabled in the repo settings); **true merge commits are reserved for the `develop → staging → main` promotions**.
  The `commit-hygiene` check fails non-Conventional and leftover `fixup!` / `wip` commits.
- Before a PR: `dotnet format --verify-no-changes` + **unit tests green**. **Test-first is the Definition of Done**
  (no new public method without a failing test first). Query code uses **fluent / method syntax, never LINQ
  query-comprehension** (engineering §4).
- **Merge gate — enforced (gh#45).** GitHub **rulesets** protect `develop`, `staging`, and `main`: each requires a
  pull request and green status checks before merge and blocks force-push / deletion, so **a red check now blocks
  the merge.** `build & unit tests`, `commit-hygiene`, and the pre-merge integration suite are required on all
  three; `ladder` is additionally required on `staging`/`main`, so a promotion can only come from the allowed
  source. `stale-base` is intentionally *not* required (it is skipped on the long-lived branches, and a
  never-running required check would deadlock the merge). Approvals aren't required (single operator); the rulesets
  carry no bypass. **One more gate never appears in the checks tab at all:** a separate `copilot-review-develop`
  ruleset requires **Copilot to have responded** to a PR into `develop` before it can merge. Its *findings* are
  advisory, but the response is required — and because a ruleset rule is not a status check, a PR still waiting on
  it reads as blocked with everything green and nothing to point at. Quota exhaustion only *delays* this: Copilot
  replies "unable to review", and that reply satisfies the rule. All the rulesets live in repo settings, recorded
  in the [deployment runbook](documentation/deployment-runbook.md). Production deploy and any rollback remain
  **human-approved**.

## Local development
`docker compose up -d` from the repo root (ADR-0012 / engineering §8); the database connection string is
config-driven. See the [deployment runbook](documentation/deployment-runbook.md).

---
*Fuller rationale + the CI/CD and Definition-of-Done detail: [engineering guide §10](documentation/trading-platform-engineering.md).*
