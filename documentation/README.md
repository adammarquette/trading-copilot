# documentation/ — the routing map

**This directory is authoritative, and it is ~240K tokens. Do not read it wholesale.** Find the document you
need here, open **the section you need**, and stop. `R-#`, ADR numbers and `gh#N` are the symbol table — resolve
symbols on demand, the way a compiler does, rather than loading every source file.

Sizes below are approximate tokens, so you can see what a read costs before you pay for it.

## Start here

| Document | ~tok | Read it when |
|---|---:|---|
| [`trading-platform-prd.md`](trading-platform-prd.md) | 23.5K | You need **what** the system must do. Requirements `R-1…R-22`; every capability traces to one. §6 is 76% of the file — **open the `R-#` you need, not the file**. |
| [`trading-platform-architecture.md`](trading-platform-architecture.md) | 7.6K | You need **how the pieces fit** — components, seams, data flow. The cheapest whole-file read here. |
| [`trigger-workflow-guide.md`](trigger-workflow-guide.md) | 2.8K | **Operator-facing.** You need to explain — to yourself or the operator — how a trigger becomes an alert, what the two routes do, and why a fire can legitimately produce no notification. Companion to the architecture doc's *Trigger / condition engine* diagrams, in prose. |
| [`trading-platform-engineering.md`](trading-platform-engineering.md) | 15.7K | You need stack, standards, testing, observability, deployment or the Definition of Done. Numbered §1–§12; cite and open by section. |
| [`data-dictionary.md`](data-dictionary.md) | 4.0K | You need the **data model**. This is now an **index**: the ERD, *Conventions* and a routing table over 12 domain pages in [`data-dictionary/`](data-dictionary/) (~2.1K each). Open the index, then the one domain — not the catalog. `§N` numbers are stable and cited from C# XML docs. |
| [`deployment-runbook.md`](deployment-runbook.md) | 14.1K | You are deploying, on call, or setting up locally. Alert runbooks live under *When a page arrives*. |

## Working agreements

| Document | ~tok | Read it when |
|---|---:|---|
| [`project-board-workflow.md`](project-board-workflow.md) | 4.7K | You are filing, grooming or moving a card — columns, `work:*` labels, sub-issues. |
| [`work-estimate-rubric.md`](work-estimate-rubric.md) | 1.9K | You are setting a `Work Estimate` on an issue. |
| [`AGENT-MEMORY.md`](AGENT-MEMORY.md) | 0.9K | **Before starting any work** — the catch-all for practices with no formal home. Cheap; just read it. |
| [`integration-test-audit.md`](integration-test-audit.md) | 17.7K | You are writing integration tests and need the existing inventory. §2 is the inventory; live per-issue status is **the tracker**, not this file. |

## Decisions — [`adr/`](adr/) (21 records, ~84K)

**Never read the folder.** [`adr/README.md`](adr/README.md) indexes every record; open the one ADR you need.
Decisions are appended as dated `## Update` sections and **superseded, never rewritten**, so an ADR is a trail:
read its Decision, then the update that matches your increment — not the whole history.
[ADR-0007](adr/0007-order-execution-model.md) (order execution) is the largest at ~22K and has its own
*Decision log* index at the top — use it.

## Role contracts — [`agents/`](agents/)

Loaded **on demand by role**, not by directory. See the table at the top of the root
[`AGENTS.md`](../AGENTS.md).

| Contract | ~tok | Read it when |
|---|---:|---|
| [`agents/code-reviewer.md`](agents/code-reviewer.md) | 1.1K | You are reviewing any change, anywhere. |
| [`agents/platform.md`](agents/platform.md) | 1.4K | You are touching CI/CD, the image, compose or deploy. |
| [`agents/coordinator.md`](agents/coordinator.md) | 1.8K | You are assigning work from the board, or driving a task to approval. Reviewer, Platform and Coordinator **never auto-load**. |

The two subtree contracts — [`src/AGENTS.md`](../src/AGENTS.md) (Coding) and
[`IntegrationTests/AGENTS.md`](../src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md) (QA) — load from their
directory instead.

## Reference — [`wiki/`](wiki/) (18 pages, ~32K)

External domain knowledge: venue and data-provider APIs, prop-firm rules, market sessions and settlement,
trading methodologies, .NET conventions. **Ingested reference, not repo truth** — when the wiki and a repo
document disagree, the repo document wins. Route through [`wiki/index.md`](wiki/index.md); never sweep the
folder.

## Design — [`design/`](design/)

[`wireframes.html`](design/wireframes.html) is the **primary UX artifact** — Adam validates UX and drives
requirements from it, so a UI change updates it in the same PR. Not a markdown read; open it in a browser.

---

*Adding a document? Add its row here in the same PR — a document nothing routes to is a document nobody opens.*
