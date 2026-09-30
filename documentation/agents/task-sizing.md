# Task sizing — choosing the cheapest model that can do the job

A shared rubric, not a contract: every role uses it, none of them owns it. It answers one question —
**what is the smallest model that can finish this task correctly** — so a docstring fix does not cost what a
retrieval-pipeline change costs. The same tiers carry the durations a time-to-complete estimate is built from
(*Time to complete*, below).

Size the **task**, not the diff you hope to write. "Probably two lines" is a prediction; the tier comes from
what the task *touches* and what it would cost to be wrong.

## The tiers

Tier names (**Haiku**, **Sonnet**, **Opus**) are capability bands. Pick the band from the task, then the model
from *Model slugs* below. The bands are the same ones the `Work Estimate` label routes on
([`work-estimate-rubric.md`](../work-estimate-rubric.md)), so an issue's label and a tier name the same band.

| Tier | Looks like | Band | Effort |
|---|---|---|---|
| **XS** | Typo, comment wording, a stale cross-reference, a version bump with no behavior change. One file, no logic. | Haiku | low |
| **S** | A localized change inside one project with the test already describing it. No new public surface, no cross-project reasoning. | Haiku or Sonnet | low–medium |
| **M** | A feature or fix inside one project that needs a new test, touches a public method, or requires reading two or three files to be safe. | Sonnet | medium |
| **L** | Crosses project boundaries, changes a contract or an interface, needs repo-wide context, or is a debugging task with no known cause. | Opus | high |
| **Floor** | Anything in the list below, **at any size**. | Opus | high |

## Model slugs

This repo dispatches on two model families, and a band has a model in each. In **Claude Code**, pass the id in the
`model` parameter of a subagent. In **Cursor**, pass the slug: **`composer-2.5-fast`** for fast, localized work
and **`grok-4.7-high-fast`** for anything that needs depth. **`inherit`** is fine when the parent session is
already at or above the band the task needs. **Ties round up** (*Applying it*, below): between two models, take
the stronger.

| Band | Claude (Claude Code) | Cursor slug |
|---|---|---|
| **Haiku** | Haiku 4.5 — `claude-haiku-4-5-20251001` | `composer-2.5-fast` |
| **Sonnet** | **Sonnet 5.5 — `claude-sonnet-5-5`** | `composer-2.5-fast` |
| **Opus** | Opus 5.5 — `claude-opus-5-5` | `grok-4.7-high-fast` |

**Sonnet 5.5 is the Claude model for S and M work** — localized and single-project coding where the tier table
above already says Sonnet. It is a good coding model and costs less than Opus 5.5, so it is the default for that
work rather than reaching for Opus (the operator's judgment, 2026-09-29). **Opus 5.5 is for L and the Floor.** Like
the rest of this table it is a guideline that changes as we see what each model handles well
([`work-estimate-rubric.md`](../work-estimate-rubric.md)), not a contract.

**Cheaper does not move the Floor.** Sonnet 5.5 being a good and inexpensive coding model changes nothing about
*The floor* below: a Floor task takes the strongest model at any size, and in this repo any `safety-critical` item
is a Floor by the rubric's own rule (`Work Estimate` at least 4). Do not size such work down because the edit is
small.

**Escalate, do not downgrade.** A Floor task stays on the top model (Opus 5.5, or Grok in Cursor) even if a cheaper
one is available. Sonnet-band work that fails twice moves up — Sonnet 5.5 to Opus 5.5, Composer to Grok — never
the other way around.

**Coordinator dispatch.** Name both the band and the model: "Floor — Code Reviewer on PR #1234,
`claude-opus-5-5`." (Cursor: `grok-4.7-high-fast`.)

## The floor — category beats size

**These always take the strongest model, even as a one-line change**, because the size signals are exactly
wrong here: the smallest diffs on these paths are the ones that end up in an incident note.

- **Clinical output.** Generation, summarization, or anything that produces text a cardiologist reads.
- **Citation and grounding.** The evidence path, bounding boxes, `patient/Binary.read`, anything that decides
  whether a claim can be traced to a source.
- **Degrade and refusal paths.** `PRD.md` §13.1 — the system must degrade deterministically, and the
  failure mode of a cheap edit here is a plausible answer with no citation, which reads as success.
- **Authorization, scopes and token custody.** Anything near SMART launch, `finalizeScopes`, introspection, or
  a token's lifetime.
- **PHI in logs or telemetry.** Adding or changing what gets logged, traced, or exported.
- **Tool I/O contracts** (NFR-CONTRACT-1) and anything in `INTERFACE_CONTROL.md`.
- **The eval gate itself.** A change to what the gate measures cannot be graded by the gate.
- **A review verdict.** Reviewing is judgment under uncertainty, and a cheap reviewer fails silently — it
  produces confident, well-formatted findings that are not the real ones.

## Applying it

1. **Name the tier before dispatching**, in the same sentence as the task. "S — add the missing null guard in
   `OpenEmrAuthClient`, test exists."
2. **Ties round up.** Between two tiers, take the higher one. The saving from guessing low once does not cover
   one wrong answer on a clinical path.
3. **Escalate rather than retry.** If a tier fails twice — gates red twice, or the same misunderstanding twice
   — move up a tier instead of re-prompting. Two failed attempts at a tier is the signal that the sizing was
   wrong, not that the prompt was.
4. **Unknown cause means L.** A bug with no diagnosis is not XS because the fix might be one line. Size the
   *diagnosis*.
5. **Sizing is not a permission.** A cheap tier still obeys every contract; it does not get to skip the
   test-first rule or the docs-in-lockstep rule because it is small.

## Time to complete

**The one home for the durations behind a time-to-complete note** — the Coordinator's
([`coordinator.md`](coordinator.md)) and the change-making agent's fallback (`src/AGENTS.md`,
`tests/AGENTS.md`, [`platform.md`](platform.md), [`wiki-editor.md`](wiki-editor.md), [`doc-simplifier.md`](doc-simplifier.md),
the last only after its migration). Other files point here
rather than copy the figures (`gl#708`). They are what this repo has observed, not targets.

**Time to complete is the time left until the item is ready for the maintainer's merge, review included —
a duration, never a clock time.** "~45 min", not "by 14:30".

| Tier | Work, until the MR is open |
|---|---|
| **XS** | as S — no separate figure observed |
| **S** | ~15–20 min |
| **M** | ~30–45 min |
| **L** | ~45–60 min |
| **Floor**, with new tests | ~60–90 min |

| Step | Adds |
|---|---|
| Code review, first round | ~15–25 min |
| Each further review round — fix plus re-review | ~20–40 min |
| Staging pipeline, only when an acceptance criterion needs a staging run | ~15–20 min |

- **Estimate = work + first review**, plus a further round for each one you expect. An item between two rows
  takes the higher (*Ties round up*, above).
- **Revise it rather than let it age:** on each review round, count what is left, not the original figure.
- **A roll-up's total is wall-clock**, not the sum of its rows: work that runs in parallel counts once, and
  an item waiting on another starts when that one is ready.

## What this does not decide

Model choice, not scope. It never licenses a smaller change than the task needs, and it never overrides a role
contract — [`code-reviewer.md`](code-reviewer.md) still governs review no matter which model runs it.
