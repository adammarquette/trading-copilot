# Work Estimate rubric

> **Adopted:** 2026-07-23 (gh#136). Expect to tune the level boundaries and the model mapping as we see how each
> tier performs against real scored work.
> **Relates to:** [project-board-workflow](project-board-workflow.md) §*Tagging & model routing* — this is the
> detailed rubric behind the **`Work Estimate: 1–5`** label.

## What the Work Estimate is for
One number, applied as the repo label **`Work Estimate: 1`**–**`5`** during **Planning** (before an item is
promoted to Current ToDo). It routes work to a **model tier**: cheap models for low estimates, the most capable
for high ones. It is a **guideline for dispatch, not a contract** — a mis-score costs money or quality, not
correctness, because enforcement always lives below the model. The
[coordinator](agents/coordinator.md) is who dispatches from this number.

**It estimates the *capability the work demands* — its reasoning difficulty and blast radius — not the time or
line-count it takes.** A 300-line mechanical rename is a *low* estimate; a one-line change to the auto-flatten
deadline is a *high* one. If you find yourself scoring by "how long will this take," stop and re-read the factors.

### Why a fixed rubric (and 1–5, not story points)
Relative-estimation scales — Fibonacci points, planning poker — exist to **mediate disagreement**: a room of
engineers argues an item up from a 5 to an 8, and the gaps in the scale absorb that negotiation. This operation
is a single maintainer plus model-routed agents; **there is no room to argue, so there is nothing to mediate.** A
fixed rubric instead gives a *deterministic* estimate — the same task scores the same number every time, by the
factors below — which is exactly what an automated dispatcher needs. Hence a tight, absolute **1–5** anchored to
worked examples, not an open-ended point scale.

## How to score
1. **Rate the task against the factors** below.
2. **Take the highest factor that materially applies.** A task is as demanding as its hardest aspect — one that
   is trivial everywhere except it touches the risk gate is not a trivial task.
3. **Apply the safety floor.** Anything on a safety-critical path (risk gate, order execution, auto-flatten,
   kill switch — the `safety-critical` label) is **≥ 4**, whatever the factors say.
4. **When torn between two levels, round up.** Under-powering a hard change to a system that places real orders
   is the more expensive mistake. Genuine uncertainty about the task *is itself* a signal — it means the
   reasoning load is real.
5. **Re-score on kickback.** If an item bounces back to Planning because scope grew, estimate it again.

## The factors
Each factor pushes the estimate. Read left-to-right; the task's score is anchored by its **furthest-right** column.

| Factor | Low (→ 1–2) | Moderate (→ 3) | High (→ 4–5) |
|---|---|---|---|
| **Ambiguity / design latitude** | approach is obvious; one clear way to do it | some design choices, but bounded by precedent | genuinely open design; needs an ADR or a judgment call |
| **Blast radius if wrong** | docs, a comment, an isolated helper | one feature/endpoint; caught by tests | money, safety path, data integrity, or many callers |
| **Surface area** | one file / one layer | a few files in one component | cross-cutting; spans layers, or the `cross-cutting` label |
| **Domain depth** | none — mechanical or generic | ordinary app logic | deep trading/venue knowledge (prop rules, settlement, venue quirks) |
| **Coordination** | self-contained | one migration or one seam | cross-repo (submodule), supersedes an ADR, multi-system, or new schema + data |
| **Verification difficulty** | obvious to check; a diff read confirms it | ordinary unit/integration tests suffice | subtle — timing, concurrency, financial correctness, unattended behavior |

## The five levels

| Label | Definition | Model tier *(guideline; current example)* |
|---|---|---|
| **`Work Estimate: 1`** | **Trivial / mechanical.** No design, no domain, no risk. Rename, typo, config bump, a doc line. | cheapest (Haiku) |
| **`Work Estimate: 2`** | **Simple.** Small and well-scoped, obvious approach, low blast radius — may touch many files if the change is uniform. | cheap (Haiku / Sonnet) |
| **`Work Estimate: 3`** | **Moderate.** An ordinary feature or a contained bug-fix with some design latitude; verifiable by normal tests. | mid (Sonnet) |
| **`Work Estimate: 4`** | **Complex.** Ambiguous, cross-cutting, cross-repo, or subtle domain correctness; or any safety-critical path (floored here). | top (Opus) |
| **`Work Estimate: 5`** | **Critical / deep.** High blast radius *and* subtle correctness — the safety-critical spine, unattended actions, money-at-risk logic. | top (Opus), max reasoning effort |

## Calibration anchors (real items, for consistency)
Scored against work already in this repo, so future estimating has fixed reference points:

- **`1`** — [#110](https://github.com/adammarquette/trading-copilot/issues/110) `ConsumerCursor → EventCursor` in the data dictionary: a mechanical rename, single doc, no risk.
- **`2`** — [#119](https://github.com/adammarquette/trading-copilot/issues/119) wiki `Access:`-header backfill: many files, but uniform and mechanical, zero blast radius, light judgment. [#118](https://github.com/adammarquette/trading-copilot/issues/118) terms review sits here too — research + judgment, but nothing ships that can break.
- **`3`** — [#56](https://github.com/adammarquette/trading-copilot/issues/56) ProjectX enum-serialization `400`: venue-specific debugging with a contained fix. [#45](https://github.com/adammarquette/trading-copilot/issues/45) ladder CI guard and [#121](https://github.com/adammarquette/trading-copilot/issues/121) real-Postgres test tier — platform work, bounded, but the logic needs care.
- **`4`** — [#60](https://github.com/adammarquette/trading-copilot/issues/60) `TradingMode` can't be derived from a venue flag: subtle domain correctness with wide implications, risk-adjacent. [#95](https://github.com/adammarquette/trading-copilot/issues/95) multi-login: cross-repo (submodule), supersedes an ADR, per-credential client lifetimes — complex and cross-cutting, but not itself the safety path.
- **`5`** — [#10](https://github.com/adammarquette/trading-copilot/issues/10) risk gate, [#11](https://github.com/adammarquette/trading-copilot/issues/11) order execution, [#12](https://github.com/adammarquette/trading-copilot/issues/12) auto-flatten: the safety-critical spine — subtle correctness, high blast radius, unattended action. `safety-critical` floors them at 4; the reasoning load puts them at 5.

## Notes & edge cases
- **Effort ≠ estimate.** The single most common mis-score. Big-but-uniform is low; small-but-subtle is high. The
  Work Estimate is about *how demanding*, not *how much*.
- **`safety-critical` is a floor, not a synonym.** It guarantees ≥ 4; it does not by itself make something a 5.
- **QA/SDET items estimate on their own terms.** An integration suite for a safety path inherits the domain
  subtlety (often 4); a smoke test asserting an endpoint responds is a 2.
- **Documentation isn't automatically a 1.** A doc that *decides* something (an ADR, a requirements change) can
  be a 3–4 on ambiguity even with zero code.
- **The estimate is the issue's, not the PR's.** If one issue fans out into several PRs of differing difficulty,
  that is usually a sign it should have been broken into child tasks in Planning — each with its own estimate.
