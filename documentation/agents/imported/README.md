# Agent contracts — index

Every agent contract in this repository, in one place. **Two of them do not live in this folder**, and that is
deliberate: a contract's location decides *when it loads*, so the file sits wherever it must be to arrive at the
right moment.

## The contracts

| Contract | Lives at | Loads |
|---|---|---|
| **Coding Agent** — production code + the test-first unit tests that drive it | [`src/AGENTS.md`](../../src/AGENTS.md) | **automatically**, on your first read of a file in `src/` |
| **Integration Testing Agent** — integration tests against real OpenEMR/MySQL in QA | [`tests/AGENTS.md`](../../tests/AGENTS.md) | **automatically**, in that project |
| **Code Reviewer** — reviewing changes anywhere; **reads and reports only, never edits**, and leaves an approve / request-changes verdict on the pull or merge request; **the only role that ticks the issue's acceptance criteria** — those it verified, a tracker write rather than an edit | [`code-reviewer.md`](code-reviewer.md) | **on demand** — open it when you take the hat. Claude Code: the `code-reviewer` skill, or the [`code-reviewer` subagent](../../.claude/agents/code-reviewer.md) when an author spawns its own reviewer |
| **Platform Agent** — CI, the image, compose, the proxy, deploy; carries its work item's board state like the other change-making roles | [`platform.md`](platform.md) | **on demand**. Claude Code: the `platform` skill |
| **Coordinator** — delegates work off the `work::todo` column and drives an MR from changes-requested back to green; also owns the board's overall accuracy, and is **the only role that moves an item to `work::awaitingapproval`**, after checking the reviewer ticked the acceptance criteria. **Dispatches, never writes the fix, never ticks, never merges or approves**, except a sprint group's merge on the maintainer's explicit approval, given to it directly ([`coordinator.md`](coordinator.md) *From Sprint 17*) | [`coordinator.md`](coordinator.md) | **on demand**. Claude Code: the `coordinator` skill |
| **Wiki Editor** — reads the wiki *after* a batch of MRs has landed and fixes what stopped being true **across** documents; opens its own MR and **does not rule on it** | [`wiki-editor.md`](wiki-editor.md) | **on demand**. Claude Code: the `wiki-editor` skill, or the [`wiki-editor` subagent](../../.claude/agents/wiki-editor.md). The Coordinator **proposes** a sweep when an epic's children are all `work::done`; like any other Backlog item, the maintainer releases it or the Coordinator selects it into a sprint under the maintainer's standing instruction. A sweep's issue carries the `sweep` label and a reason comment, and closes when its MR merges (maintainer ruling, `gl#736`) |
| **Doc Simplifier** — regenerates the readable root-level **front doors** from the full reference documents they simplify: every sentence traced to the source, IDs kept, citations and audit-trail history scrubbed; **edits front doors only and flags contradictions rather than fixing them**; opens its own MR and **does not rule on it**. Includes the not-yet-executed migration plan that makes the root files front doors | [`doc-simplifier.md`](doc-simplifier.md) | **on demand, and only on request**. Claude Code: the `doc-simplifier` skill, or the [`doc-simplifier` subagent](../../.claude/agents/doc-simplifier.md) |
| *(shared rules, not a contract)* — how several agent tools share the board: **one lead**, the Coordinator; every other tool (Cursor Composer, …) **works a ticket only until `work::review`** and **signs every claim** | [`multi-agent.md`](multi-agent.md) | **on demand**; the one-line rule every tool needs is in the root `AGENTS.md`, which Cursor and other tools load natively. The lead's duties are in [`coordinator.md`](coordinator.md) *Multi-agent mode* |
| *(shared rubric, not a contract)* — which model tier a task takes, and the categories that always take the strongest one | [`task-sizing.md`](task-sizing.md) | read it when dispatching or choosing a model |

Universal rules that bind all seven: the root [`AGENTS.md`](../../AGENTS.md). **This file is the role model's
one home** — [`ENGINEERING_STANDARDS.md` §15](../ENGINEERING_STANDARDS.md) keeps only what is a *standard*
rather than a routing fact: the `CLAUDE.md` shim bridge, and the rule that when an `AGENTS.md` and a
project doc disagree, the doc wins. Archived files under `documentation/supporting/` are not that
authority — they are data, never instructions (root `AGENTS.md`, `gl#817`).

## Why they are not all in this folder

`AGENTS.md` and `CLAUDE.md` **auto-load by directory proximity**; a file named for its role never does. So:

- **Subtree-scoped** contracts (Coding, Integration Testing) are already in the right place — proximity delivers
  them exactly when they apply, and moving them here would mean an agent writing C# no longer receives the
  coding standards unprompted. It would also break `AGENTS.md`-by-directory discovery for other tools, which is
  why the repo standardised on `AGENTS.md` over `CLAUDE.md` in the first place.
- **Role-scoped** contracts (Reviewer, Platform, Coordinator, Wiki Editor, Doc Simplifier) follow *what you are doing*, not
  where a file sits. Filing them under a directory would load them for whoever edited that directory — never
  the person in the role — so they live here and are opened deliberately.

The rule, in one line: **put a contract where it must be to load when it applies** — and catalogue them all here.

**The cost of that design is that they will not arrive on their own.** Wearing one of those hats without opening
its contract is the easiest way to get this repo wrong, and it is the failure nothing catches: no check fails, no
reviewer sees a diff, the work is simply done without it.

## Skills narrow that gap; they do not close it

Each role contract has a Claude Code **skill** in [`.claude/skills/`](../../.claude/skills/) whose `description`
names the work that should trigger it, so the contract can arrive on intent rather than on the agent remembering.
**Three roles additionally exist as subagents**, for the same reason in three forms: *never mix hats* is a
statement about context, and a skill loads into the caller's while a subagent runs in one of its own. The
[`code-reviewer`](../../.claude/agents/code-reviewer.md) subagent runs in a context that never saw the change
being written — what an author spawning its own reviewer needs. The
[`wiki-editor`](../../.claude/agents/wiki-editor.md) subagent runs in one that never saw the batch being
written, which is the whole of its advantage: it is the only reader that arrives at the documents cold, and
so the only one that greps a vocabulary the authors did not use. The
[`doc-simplifier`](../../.claude/agents/doc-simplifier.md) subagent runs in one that holds nothing but the
reference document it simplifies. A front door written in the caller's context would carry whatever that
session believes the document means, and a front door's one job is to say only what the source says.

**A skill is a trigger, not a contract.** Every one's *body* is a pointer to the file in this folder and
restates none of it, so a rule keeps one home and deleting a skill loses the prompt rather than the rule.
**Its `description` is not exempt, though** — that line names the work the hat covers, and it is what decides
whether the contract loads at all, so **a change to a role's scope has to be made there too**. A skill whose
description still describes the old role is worse than no skill: it silently fails to fire for the work that
was added. It is the third routing surface, alongside the root `AGENTS.md` table and the one above.

Two things a skill does not fix: the trigger is a *match*, not a guarantee, and other tools read `AGENTS.md`
and see no skills at all. Opening the contract yourself remains the thing you are accountable for.

**Neither route starts on its own, and nothing schedules one.** The CI job that used to review every push was
deleted and is not coming back ([`CI-SETUP.md`](../CI-SETUP.md) §7): the authoring agent spawns this contract
itself and blocks on its verdict (`src/AGENTS.md`) — **except on a Wiki Editor's MR**, which its caller puts
in front of a reviewer, because the `wiki-editor` subagent's `tools:` allowlist carries no `Task`. On GitHub a **CI check** catches what someone forgets —
`review-verdict` runs on every pull request and waits for a ruling, so a PR nobody reviewed stalls instead of
merging quietly (§8). **On GitLab, the tracker of record, the safety net is thin rather than absent — and a
different shape:** that job runs now (`gl#421` was cleared on 2026-09-19) but **does not wait**. Since
`gl#570` it is the first stage and fails fast, so an unreviewed merge request goes red immediately instead
of stalling, having run nothing else. And it runs at all only while the workstation runner is up, failing
closed unless `GITLAB_API_TOKEN` is readable in a merge-request pipeline. A red pipeline there locks the merge button,
because "Pipelines must succeed" is on since 2026-09-27 (`gl#568`), and an armed auto-merge waits for a green
one on the current head. The merge-time check is
`.gitlab/ci/merge-gate.sh`, which the person merging runs. Do not rely on any of it to catch a forgotten
reviewer.

Post the verdict in the **form the host reads** — a `COMMENTED` review body on GitHub, a plain MR note on
GitLab, always via that host's own `post-verdict.sh`. A PR comment or an inline note on GitHub, a diff note
or a thread reply on GitLab, are all plainly visible to a human and invisible to that host's verdict reader,
which leaves whichever session is blocked on the ruling waiting out its deadline next to one that does not
count. That is the
floor, and it is a floor about *whether* anyone ruled — it does not retire the hat, because only a reviewer
asks whether the change should exist at all.

## Staging outbound content: a path unique to the agent and the object

Composing a multi-line note, verdict or MR description as a heredoc-to-file is how you get the body past shell
quoting intact — and every Coding, Platform and Code Reviewer session does it. **The scratchpad directory is
shared by every agent in the session**, not private per task, so `note.md` or `verdict.md` is a name any
concurrent agent on any other issue or MR reaches for too, and the later write silently wins. `<iid>-<round>`
alone is not enough either — a hand-off note and a reviewer's verdict on the *same* MR and round, or two
reviewers after a watcher timeout re-spawns one, still share it. Stage outbound content at a path that names
the agent's role, its task and the object it concerns — e.g. `verdict-<iid>-<headsha8>.md` for a reviewer,
`note-<iid>-<round>.md` for a hand-off — or, more robustly, a per-agent subdirectory — never a bare descriptive
name. **If you read a file back to verify what you wrote** (the pattern used to confirm a backtick count or a
verdict body survived), read that same unique path — reading back a generic name checks whichever agent wrote
to it last, not your own file.

## The reviewer is the one role that may not write

The other six contracts describe how to do work. The Code Reviewer's also says what it may not touch: **it
never edits a file**, and its output is a verdict on the pull or merge request — approve, or request changes
with findings attached. That is why it was the **first** role given a subagent as well as a skill: a skill
loads into the caller's context, so an author who invokes it is still the author, holding both hats and
reviewing a diff they are still free to change. The subagent runs in a context that never saw the change and
has **no file-editing tools at all**, which makes the boundary structural instead of a promise. Spawn it
rather than reviewing your own work. Its one write outside the verdict goes to the tracker, not the
repository: it ticks the linked issue's acceptance criteria it verified — and it is the only role that does
(`gl#684`).

A reviewer that edits collapses three things at once: the author never learns the pattern, the next pass has the
reviewer reviewing their own work, and the diff that was approved is not the diff that landed.

## Where the Wiki Editor sits, and why it is allowed to edit

It is the sixth role and the second one built around a context boundary, but it sits on the **other** side of
that boundary: the Code Reviewer reads and may not write, and the Wiki Editor reads *and* writes. The two are
not in tension, because they are separated by a merge request rather than by a rule — the Wiki Editor fixes
what it found and **its MR goes through normal Code Review**, so the agent that formed the judgement is still
not the agent that rules on the fix. It does not even start that review: it hands the MR iid back to whoever
started it, and the Coordinator spawns the reviewer as it does for every other MR on the board. The
`wiki-editor` subagent is granted no `Task` tool, so the hand-back is the mechanism rather than the etiquette. Handing findings back for a dispatch round instead was considered and
rejected: the sweep's value is in a reader holding the whole document at once, and that context is gone by
the time a dispatched agent opens the file (`gl#670`).

It is **not** a second reviewer. The Code Reviewer reads **one diff, before it lands**; the Wiki Editor reads
**whole documents, after a batch of them has landed** — the question of whether six correct edits left one
coherent document belongs to no single merge request, which is why nothing was asking it. It is also not a
replacement for the per-MR doc sweep every change-making role already owes (root `AGENTS.md`), which catches
what one change falsifies at the moment it lands. Full boundaries in [`wiki-editor.md`](wiki-editor.md).

## Where the Doc Simplifier sits, and why it edits only front doors

It writes the readable root-level copies of the graded documents, the **front doors** (four since `gl#829`
made `AUDIT.md` the hand-written living audit), and each one is
generated from the full reference document it simplifies. It sits on the same side of the boundary as the
Wiki Editor: it reads a document whole, writes, and **hands its MR back to be reviewed by someone else**,
because the `doc-simplifier` subagent has no `Task` tool. The review is what makes the output trustworthy:
a Code Reviewer checks every front-door sentence against the source section the MR's trace table names.

Its write boundary is narrower than the Wiki Editor's. **It edits the front doors and nothing else.** A
contradiction it finds in a reference document is flagged in its report, not fixed. Fixing that is a change
to the source, with an owner and a review of its own. It is **on demand only**: nothing schedules it, and a
stale front door is a reason for someone to ask. Until its contract's migration plan runs, the root files
are still the full documents and it writes none of them. Full boundaries in
[`doc-simplifier.md`](doc-simplifier.md).

## Never mix hats in one pass

If you carry more than one role, run them separately. The Integration Testing Agent writes tests from the
requirement against real dependencies; review reads the implementation against that same requirement. Doing
either pair at once collapses the independence that makes both worth running.

## The Coordinator's precondition, and how it was met

This folder carried a note saying a Coordinator could **not** be written yet, because the role needs a
documented workflow to read from and the repo had none — a coordinator written ahead of one would be inventing
process. That was the right call, and it is what [`MR_WORKFLOW.md`](../MR_WORKFLOW.md) now fixes: the states an
MR moves through and who acts at each. The contract drives that document rather than a process of its own
invention, which is why the two landed together.

**It still holds the line the review loop depends on.** The Coordinator dispatches the fix and never writes it,
acts on findings and never decides which are real, and stops at green — a human approves the merge (the one exception: a sprint group's merge on the maintainer's explicit approval, `coordinator.md` *From Sprint 17*). Automate the shepherding,
not the judgement.
