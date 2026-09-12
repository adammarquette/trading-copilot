# ADR-0029: Software-factory packaging — standalone template repo, seed-once, Apache-2.0

**Status:** Accepted · **Date:** 2026-09-11 · **Deciders:** Adam (operator/maintainer)
**Extends:** [ADR-0015](0015-distribution-licensing-governance.md) — the factory is packaging, not a licence
change; Apache-2.0 and the rest of 0015's *Decision* stand. **Relates to:**
[CONTRIBUTING.md](../../CONTRIBUTING.md) (*Creating a new supporting repo* —
[`MarqSpec.Repo.Template`](https://github.com/adammarquette/MarqSpec.Repo.Template) is a different scaffold).
Issues: `gh#1177` (this record), `gh#84` (the factory epic).

## Context

The reusable engineering machinery in this repo — the agent role-contracts, the ADR discipline, the
docs-as-memory pattern, the CI/CD and container scaffolding — is a **software factory** meant to seed *other*
products, not something trading-specific. Epic [gh#84](https://github.com/adammarquette/trading-copilot/issues/84)
is that extraction.

The method (role decomposition, ADR immutability, docs-as-memory, test-first) is not copyrightable. The
**artifacts** need a home and a recorded split (Tier 1 copy-as-is / Tier 2 templatize / Tier 3 in-file / Tier 4
stays). That inventory lives on gh#84; this record does not move files and does not create the factory repo.

gh#84 left four packaging questions open. [gh#1177](https://github.com/adammarquette/trading-copilot/issues/1177)
pinned them so this ADR is a record, not a second planning debate. No later comment on #1177 named a different
factory title.

## Decision

1. **Delivery — a standalone GitHub template repository** (`Use this template`). Not a `dotnet new` pack, not
   copy-and-adapt docs only.

2. **Sync — seed-once.** Product repos do not track factory updates; improvements are cherry-picked by hand.

3. **Licence — Apache-2.0**, same as this product. Extends [ADR-0015](0015-distribution-licensing-governance.md);
   cite that record for the grant, the trademark non-grant, and the AI-authorship disclosure. This ADR does not
   reopen them.

4. **Name — `MarqSpec.Repo.Factory`** (working title). Distinct from `MarqSpec.Repo.Template` (the venue-client /
   supporting-repo scaffold named in CONTRIBUTING). A rename at creation time is a dated `## Update` here, not a
   rewrite of this *Decision*.

5. **Where — a new public repo under `adammarquette`**, not an in-tree `template/` folder. This record does not
   create that repo.

## Alternatives considered

- **`dotnet new` template pack.** Rejected. It parameterizes namespace and project names well, but the factory is
  mostly docs, workflows, compose, and contracts — not a .NET project scaffold. GitHub `Use this template` is the
  lowest-friction seed.
- **Copy-and-adapt docs only.** Rejected. Leaves no home for the artifacts and no recorded split; every new
  product re-derives the factory from this repo by hand.
- **Track factory updates in product repos** (submodule, downstream sync, or equivalent). Rejected. Divergence
  management is hard; seed-once plus manual cherry-pick is the simple model gh#84 leaned.
- **In-tree `template/` folder.** Rejected. The factory is meant to evolve on its own and seed products that are
  not this one; a folder here keeps it coupled to the trading product.
- **Reuse `MarqSpec.Repo.Template`.** Rejected. That repo bootstraps venue-client / supporting siblings
  (CONTRIBUTING). Mixing factory and venue-client scaffold would collapse two different jobs into one name.

## Consequences

**Positive**
- Packaging is recorded before any files move, so the Tier 1+2 extraction (gh#1178) does not guess the delivery
  mechanism.
- gh#84's open decisions (delivery, sync, name/brand, record-as-ADR) close by citing this record.
- One licence across product and factory, owned by [ADR-0015](0015-distribution-licensing-governance.md).
- Seed-once avoids a sync contract the factory does not yet need.

**Negative / costs**
- Product repos drift from later factory improvements unless someone cherry-picks.
- The factory repo does not exist yet; this ADR only names it and the packaging.
- A working title may change when the repo is created; that is an increment, not a reversal.

## Decision log

The *Decision* above is extended by increment; the dated updates below are the trail. Oldest first; this index
mirrors the `## Update` headings, so keep the two in step when an entry is appended (gh#600).

| Date | Update |
|---|---|
| 2026-09-11 | factory repo exists; `develop` carries Tier 1+2 ([MarqSpec.Repo.Factory](https://github.com/adammarquette/MarqSpec.Repo.Factory)) (gh#1178) |
| 2026-09-12 | Tier 3 spines on Factory PR #9; not yet the template tree (gh#1179) |

## Update (2026-09-11) — factory repo exists (gh#1178)

[`MarqSpec.Repo.Factory`](https://github.com/adammarquette/MarqSpec.Repo.Factory) is live: a public GitHub
template repository (`is_template: true`), default branch `develop`. The name is unchanged from this
record's *Decision*. **`develop` carries the Tier 1 + Tier 2 extract** — that is the tree **Use this
template** copies, not the venue-client `MarqSpec.Repo.Template` bootstrap the repo was created from.
Tier 3 remains [gh#1179](https://github.com/adammarquette/trading-copilot/issues/1179).

## Update (2026-09-12) — Tier 3 spines on Factory PR #9 (gh#1179)

Tier 3 factory spines are on
[MarqSpec.Repo.Factory PR #9](https://github.com/adammarquette/MarqSpec.Repo.Factory/pull/9)
(`feature/1179_split-factory-spines`): the `AGENTS.md` contract family (role decomposition, DoD,
test-first, report-don't-fix, console-config-must-be-recorded), the copilot-instructions review
format, the engineering-practices skeleton plus current-default vs **Decide:**, the ADR index /
Nygard / decision-log rules, and the AGENT-MEMORY dated-catch-all concept. Product fill-ins are
`{{PLACEHOLDER}}`s. They are **not yet** the Use-this-template tree — factory `develop` still
carries Tier 1+2 only. **This product's contracts are unchanged as this product** — no wholesale
replace. The *Decision* is unchanged. Tier 4 stays here.

## Follow-ups

- ~~Stand up `MarqSpec.Repo.Factory` as a public GitHub template and extract Tier 1 + Tier 2
  ([gh#1178](https://github.com/adammarquette/trading-copilot/issues/1178)). Not this card.~~
  Done — https://github.com/adammarquette/MarqSpec.Repo.Factory
- Split factory spines out of product files — Tier 3
  ([gh#1179](https://github.com/adammarquette/trading-copilot/issues/1179)). In flight on
  [Factory PR #9](https://github.com/adammarquette/MarqSpec.Repo.Factory/pull/9); not yet on factory
  `develop`. Tier 4 stays in this product.
