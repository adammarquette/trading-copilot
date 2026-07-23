# Agent contracts — index

Every agent contract in this repository, in one place. **Two of them do not live in this folder**, and that is
deliberate: a contract's location decides *when it loads*, so the file sits wherever it must be to arrive at the
right moment.

| Contract | Lives at | Loads |
|---|---|---|
| **Coding Agent** — production code + unit tests, test-first | [`src/AGENTS.md`](../../src/AGENTS.md) | **automatically**, editing `src/` |
| **QA Agent** — integration + smoke tests, written independently | [`src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md`](../../src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md) | **automatically**, in that project |
| **Code Reviewer** — reviewing changes anywhere | [`code-reviewer.md`](code-reviewer.md) | **on demand** — open it when you take the hat |
| **Platform Agent** — CI/CD, image, compose, deploy | [`platform.md`](platform.md) | **on demand** (a stub sits in `.github/workflows/`) |

Universal rules that bind all four: the root [`AGENTS.md`](../../AGENTS.md).

## Why they are not all in this folder

`AGENTS.md` and `CLAUDE.md` **auto-load by directory proximity**; a file named for its role never does. So:

- **Subtree-scoped** contracts (Coding, QA) are already in the right place — proximity delivers them exactly when
  they apply, and moving them here would mean an agent writing C# no longer receives the coding standards
  unprompted. It would also break `AGENTS.md`-by-directory discovery for other tools, which is why the repo
  standardised on `AGENTS.md` over `CLAUDE.md` in the first place.
- **Role-scoped** contracts (Reviewer, Platform) follow *what you are doing*, not where a file sits. Filing them
  under a directory loaded them for whoever edited that directory — never the person in the role — so they live
  here and are opened deliberately. See gh#146.

The rule, in one line: **put a contract where it must be to load when it applies** — and catalogue them all here.

## Related

- [`.github/copilot-instructions.md`](../../.github/copilot-instructions.md) — the substantive review checklist.
  It stays in `.github/` because GitHub's Copilot reviewer reads that exact path; the Code Reviewer contract
  points at it rather than restating it.
- [`../project-board-workflow.md`](../project-board-workflow.md) — how work flows across the board, and the
  `work:*` + `Work Estimate` tagging that routes it by role and model tier.
