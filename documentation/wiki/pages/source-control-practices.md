# Source-control practices (reference)

> **Trust tier:** authoritative (Microsoft Code-With Engineering Playbook). **Verified:** WebFetch 2026-07-19.
> **Source:** https://microsoft.github.io/code-with-engineering-playbook/source-control/
> **Access:** public GitHub Pages site fetched directly; the playbook repo is **CC-BY-4.0** (LICENSE verified
> 2026-07-22) — this page is an attributed summary with the canonical link, not a copy.
> **Informs:** [`CONTRIBUTING.md`](../../../CONTRIBUTING.md), engineering §10 (Git workflow). This page is the
> external reference; our **concrete conventions live in `CONTRIBUTING.md`**.

Microsoft's engineering playbook on source control — the general best practices we adopt, with **our specifics in
[`CONTRIBUTING.md`](../../../CONTRIBUTING.md)**.

## What the playbook recommends
- **Agree the approach as a team** before coding; **consistency** matters more than the specific choice.
- **Lock the default branch**; merge only via **pull requests** with agreed branch / PR policies.
- **Clean, traceable history** — meaningful commits; choose a **merge strategy** (linear vs. non-linear) deliberately.
- **Branch naming** — the playbook's *example* is `user/<alias>/<feature>`; **we deviate** to a work-item-oriented
  scheme (below).
- Repos should ship a **`CONTRIBUTING.md`** documenting the strategy — which we do.

## Our specifics (authoritative in CONTRIBUTING.md)
- **Branch naming: `<type>/<work-item-id>_<title>`** — `type ∈ {feature, bug, hotfix}`, `work-item-id` = the GitHub
  issue number, `title` = short kebab (e.g. `feature/42_risk-gate`). **Work-item-oriented, not alias-oriented** — a
  branch traces to the issue it delivers (issue-first).
- **`develop → staging → main`** branching; **rebase-merge with curated units-of-work commits** (squash retired,
  gh#104; merge commits are promotions-only); Conventional Commits + an `Assisted-by` trailer; the **same-PR doc
  rule** (engineering §10).

## Relevant-link index
- Microsoft Code-With Engineering Playbook — Source Control — https://microsoft.github.io/code-with-engineering-playbook/source-control/
