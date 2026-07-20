# Contributing

How we work in this repo. This is the contributor front-door; the **authoritative detail** lives in the
[engineering guide §10](documentation/trading-platform-engineering.md) (Git workflow, CI/CD, Definition of Done) and
the root [`AGENTS.md`](AGENTS.md) (agent contract). Source-control practices draw on Microsoft's
[Code-With Engineering Playbook — Source Control](https://microsoft.github.io/code-with-engineering-playbook/source-control/)
(wiki: [source-control practices](documentation/wiki/pages/source-control-practices.md)).

## Branching model
**All new work branches off `develop`** and PRs back into it — `develop` is the sole integration branch.
Changes then promote up a one-way ladder, and **each step has exactly one allowed source**:

| Target | Allowed source | Exception |
|---|---|---|
| `develop` | any `feature` / `bug` branch | — |
| `staging` | **`develop` only** | allowed with a stated, good reason recorded in the PR |
| `main` | **`staging` only** | **none** |

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
`Related to #N`). Work is planned as epics → tasks on the GitHub **Project board**, and each feature gets a **dev
task** plus an independent **QA task** (engineering §10).

## Commits
- **[Conventional Commits](https://www.conventionalcommits.org/)** (`feat:`, `fix:`, `docs:`, `chore:`, `build:`, …);
  the commit *type* drives SemVer.
- AI-authored changes carry an **`Assisted-by:`** trailer (plus `Co-Authored-By:`).
- **Docs move with the code — the same-PR rule:** any change whose behavior / data model / API / UX a doc describes
  updates that doc **in the same PR** (PRD `R-#`, the data dictionary + its ERD, wireframes, ADRs). A PR that drifts
  is **not done**.

## Pull requests
- Open against **`develop`**; reference the tracking issue.
- **Clean history:** a branch may carry several commits while in progress, but **rebase / squash before merge** so
  each merged commit has a single clear purpose (ideally one coherent commit per branch).
- Before a PR: `dotnet format --verify-no-changes` + **unit tests green**. **Test-first is the Definition of Done**
  (no new public method without a failing test first). Query code uses **fluent / method syntax, never LINQ
  query-comprehension** (engineering §4).
- **Branch protection** on `develop` / `staging` / `main` requires status checks (build / test / eval) before merge.
  Production deploy and any rollback are **human-approved**.

## Local development
`docker compose up -d` from the repo root (ADR-0012 / engineering §8); the database connection string is
config-driven. See the [deployment runbook](documentation/deployment-runbook.md).

---
*Fuller rationale + the CI/CD and Definition-of-Done detail: [engineering guide §10](documentation/trading-platform-engineering.md).*
