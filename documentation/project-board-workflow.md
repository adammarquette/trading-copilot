# Project board & workflow — how work moves, and who moves it

> **Adopted:** 2026-07-23 (gh#136). **Board:**
> [Trading Copilot, project #2](https://github.com/users/adammarquette/projects/2) (public).
> **Relates to:** [`CONTRIBUTING.md`](../CONTRIBUTING.md) (branching + Definition of Done), root
> [`AGENTS.md`](../AGENTS.md) (the five agent role contracts, including the
> [coordinator](agents/coordinator.md)), engineering guide
> [§10](trading-platform-engineering.md) (Git workflow / CI/CD), and the
> [Work Estimate rubric](work-estimate-rubric.md).

The GitHub Project board is the **schedule**; the git promotion ladder (`develop → staging → main`) is the
**delivery mechanism**. This document governs the board: what each column means, what has to be true for an item
to move, who is allowed to move it, and how items are tagged so that humans *and* model-routed agents can pick up
the right work. It complements — never overrides — the issue-first / no-orphaned-PR rule and the Definition of
Done that already live in `CONTRIBUTING.md` and the engineering guide.

## The board at a glance

Work flows through a **six-column funnel** — a wide intake reservoir narrowing to ready, actionable work:

| Column | One-line meaning | Who moves an item **out** |
|---|---|---|
| **Backlog** | Valid direction, not yet being prepared — the intake reservoir | Maintainer / product owner |
| **Planning** | Being prepared — needs review, sub-task breakdown, or a design decision | Maintainer / product owner |
| **Current ToDo** | Ready and tagged — anyone (agent or teammate) may pick it up | Whoever picks it up |
| **In Progress** | Actively being worked | The worker |
| **Review** | PR open and linked — **the author agent is paused here, monitoring it** | The author, until approved |
| **Done** | **Approved, checks green**, and merged | — (terminal) |

Flow is left-to-right, with **one sanctioned backward move**: an item kicks back to **Planning** (from Current
ToDo or In Progress) when it turns out to be underspecified — see *Kickback*.

**Review is not a parking space.** The agent that opened the PR owns the card while it sits here: it spawns the
reviewer, then **blocks on `scripts/watch-verdict.sh verdict <pr>`**, and on `CHANGES-REQUESTED` it pushes fixes
and waits again — the loop repeats until approved and green. The card leaves for **Done** only then; a merged PR
whose issue still has scope stays open and goes back to a working column, not to Done (canonical:
[engineering §10](trading-platform-engineering.md), which owns the loop and the exit statuses).

`Backlog → Planning → Current ToDo` is the funnel: the reservoir feeds active preparation, which feeds the ready
queue. The gate between each is the **maintainer's / product owner's** judgment.

## Issues, PRs & sub-issues — cards vs. links

The board tracks **issues**, and only issues are **cards** (they carry Status). Two relationships hang off them,
and they use different GitHub mechanisms:

- **A PR is an issue's implementation, not a card.** It **links to its defining issue with a closing keyword**
  — `Closes #N` — which, because PRs target the default branch **`develop`**,
  [auto-closes the issue on merge](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/linking-a-pull-request-to-an-issue)
  and shows the PR in the issue's *Development* section and the board's *Linked pull requests* field. So an issue
  in **Review** visibly carries its PR without the PR taking a card of its own. (`Related to #N` links *without*
  closing — for a PR that touches, but does not complete, an issue.)
- **A sub-issue is issue→issue decomposition.** An epic's tasks are its
  [**sub-issues**](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/adding-sub-issues)
  (the *Parent issue* / *Sub-issues progress* fields); a large task can hold its own. **A PR cannot be a
  sub-issue** — GitHub restricts sub-issues to issues — so PR→issue always uses linking, never the sub-issue
  hierarchy.

## The columns

### Backlog
The **intake reservoir**: valid directions that are not yet being actively prepared. New issues land here by
default, as does anything deferred — work carrying the `backlog` label ("valid direction, not scheduled; revisit
when its trigger fires") lives here, and so do future-phase epics whose first increment is not yet being scoped.
Nothing here is pick-up-able; an item leaves Backlog only when the maintainer pulls it into **Planning** to
prepare it (its trigger has fired, or it is next up).

### Planning
Work that **can begin** but is not yet ready to hand to a worker — it needs a review, a break-down into
sub-tasks, acceptance criteria, or a product/design decision (an ADR, a wireframe) first. This is the **active
on-deck area**, deliberately smaller than Backlog: things here are being shaped for the next stretch of work, not
parked.

An item **leaves Planning only when it is actionable**: scoped clearly enough that a competent worker could
implement it without a blocking question, **and tagged** (see *Tagging*). Promotion to Current ToDo is the
"ready" gate and is the **maintainer's / product owner's call** — the moment work is declared ready to consume.

### Current ToDo
Ready, tagged, not started — a **prioritized rolling queue**; top of the column is highest priority. There is no
sprint boundary: this column *is* the sprint, refilled continuously from Planning. Any agent or teammate may take
the highest item that matches their role and model tier (see *Tagging & model routing*) and move it to In
Progress.

**Claim it before you start — `scripts/claim.sh <issue-id>`.** Moving the card is *not* a claim: sessions run in
parallel and the board is manual, so **the pushed branch is the claim** (gh#375). Push it empty first, then work.
Skipping this has cost a full session's work more than once. Full rules — the 4-hour staleness window, the
split-issue trap (gh#453), and the two-repo card whose claim hides in a submodule (gh#571) — are in
[`CONTRIBUTING.md`](../CONTRIBUTING.md) §*Claiming work*.

Two rules specific to this column:
- **QA/SDET may add directly here.** Integration and smoke-test issues are specified *independently* of the
  implementation (the QA Agent works blind to the code, per its contract), so their acceptance criteria come from
  the requirement, not from a design that needs product review. They therefore **skip Backlog and Planning** and
  land in Current ToDo — the one sanctioned bypass. (The `test(qa)` suites #130/#131/#132 arrived this way.)
- **If picking it up reveals it is underspecified**, don't force it — **kick it back to Planning** (see
  *Kickback*).

### In Progress
Being worked right now. Self-assign when you start (the board's Assignees field) — but **the assignee is not the
collision signal and never was**: in a single-operator repo it is `adammarquette` on every issue, and this column
is manual and goes stale. **The pushed claim branch is the only signal that works** (see *Current ToDo* above and
[`CONTRIBUTING.md`](../CONTRIBUTING.md) §*Claiming work*). This column holds **both** coding-agent and
QA/SDET-agent work, and a single feature can have both in flight at once — the coding task and its independent
test suite advance in parallel.

**Kickback.** If, once you are in, the item has *too many unresolved questions to finish correctly*, move it
**back to Planning** with a comment enumerating the blocking questions. A stalled item must not sit in In Progress
looking like active work. This is the pressure-relief valve the whole process depends on: better to re-plan than
to guess on a system that places real orders.

### Review
Work complete and a **PR is open**. Move here when the PR exists.

- **A PR must be linked to its issue** whenever the change touches **code or documentation** — normally
  `Closes #N` (auto-closes on merge into `develop`; use `Related to #N` when the PR does not complete the issue),
  which populates the issue's *Linked pull requests* field so its card carries the PR (see *Issues, PRs &
  sub-issues*). No orphaned PRs, no unlinked issues (the issue-first rule, enforced at the board).
- The rare issue with **no** code/doc change — a pure decision or a triage outcome — may enter Review (or go
  straight to Done) with the resolution recorded in a comment instead of a PR.
- Review is the **Code Reviewer Agent's** arena: it leaves findings as review comments and submits a formal
  verdict — **Approve** or **Request changes** (per its [contract](agents/code-reviewer.md)) — but does **not merge,
  close, or move the card**. The author addresses findings and resolves threads; the maintainer merges.
- **Who starts it:** the author agent spawns that reviewer itself once the PR's checks are green, handing over the
  PR number and nothing else — the independence rules that keep this honest are the
  [contract's](agents/code-reviewer.md), and the loop is
  [engineering §10](trading-platform-engineering.md)'s (`gh#815`).
- **While the author is waiting, the PR carries `verdict:watching`.** `watch-verdict.sh verdict` applies that
  label for the life of its wait and clears it on every exit, signals included. It exists so a
  [coordinator](agents/coordinator.md) can tell a live author from a dead one without guessing — read the
  label, never infer from silence (`gh#1028`).

#### A split verdict
Two reviewers can rule on the same head — the author spawns one, and a coordinator that could not see the
author's wait spawns another. When their verdicts disagree, **the approval does not carry**:

- **Every reviewer who ruled on the current head must approve.** One `Request changes` outranks any number of
  approvals, and an unresolved finding outranks an approval that ignored it.
- The card goes back to **Current ToDo** and the implementer is re-dispatched on the **same** claim, exactly as
  for a single `Request changes`. There is no tie to break and no casting vote.
- A verdict on a *superseded* head is not a vote at all — `review-verdict` binds an approval to the PR's own
  contribution by `git patch-id`, so a stale one has already stopped counting (`gh#796`).

The remedy for repeat splits is upstream, not procedural: if a coordinator is spawning duplicate reviewers, the
`verdict:watching` signal above is missing or stuck, and that is the thing to fix.

### Done
Merged, and satisfying the **Definition of Done** (engineering §10: test-first, build green, docs updated in the
**same PR**). *Done is Done* — it implies no follow-up. Residual scope discovered along the way is **spun off as
its own issue** (landing in Backlog, or Planning if it is next up), never left implied in a "done" item.

## Transitions & ownership

| From → To | Trigger | Who |
|---|---|---|
| *(new issue)* → **Backlog** | issue opened | author / auto-add |
| **Backlog → Planning** | pulled in to be prepared (trigger fired / next up) | **maintainer / product owner** |
| **Planning → Current ToDo** | scoped + tagged + product-ready | **maintainer / product owner** |
| *(QA)* → **Current ToDo** | integration/smoke suite specified from the requirement | QA/SDET agent |
| **Current ToDo → In Progress** | picked up | the worker (self-assign) |
| **In Progress → Review** | PR open + linked | the worker |
| **Review → Done** | PR merged | maintainer (merge) |
| **Current ToDo / In Progress → Planning** | too many open questions (*kickback*) | the worker |
| *(any)* → **Done** (closed *not planned*) | won't-do / superseded | maintainer |

## Tagging & model routing

The goal: by the time an item reaches **Current ToDo**, its labels alone tell a dispatcher *which kind of worker*
and *which model tier* should take it. Tags are applied during **Planning**, before the ready gate. Two required
dimensions, plus one override.

**1. Work type** — which role/agent owns it (maps to the `AGENTS.md` role contracts):

| Label | Role contract | Typical work |
|---|---|---|
| `work:code` | [Coding Agent](../src/AGENTS.md) | production code + unit tests, test-first |
| `work:qa` | [QA Agent](../src/MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md) | integration / smoke tests, written independently |
| `work:platform` | [Platform Agent](agents/platform.md) | CI/CD, container, deploy, infra |
| `work:docs` | (any) | documentation-only change |

Work that needs a product/UX/design decision before it can be built is not tagged for pickup — it **stays in
Planning** until the decision is made.

**2. Work Estimate** — a 1–5 estimate of the *capability a task demands* (reasoning difficulty × blast radius —
not raw effort: a large but mechanical change can be a 2). Drives which model tier is dispatched — a **guideline
that evolves as we learn what each tier handles well**, not a hard contract. The scoring rubric, factors, and
calibration anchors live in [work-estimate-rubric](work-estimate-rubric.md); the summary:

| Label | Meaning | Model tier *(guideline)* |
|---|---|---|
| `Work Estimate: 1` | Trivial / mechanical — typo, doc tweak, config bump, rename | cheapest (e.g. Haiku) |
| `Work Estimate: 2` | Simple — small, well-scoped, obvious approach, low blast radius | cheap (Haiku / Sonnet) |
| `Work Estimate: 3` | Moderate — ordinary feature, some design latitude | mid (Sonnet) |
| `Work Estimate: 4` | Complex — ambiguous, cross-cutting, or multi-component | top (Opus) |
| `Work Estimate: 5` | Critical / deep — subtle correctness, high blast radius | top (Opus), max effort |

We use a **fixed rubric** rather than relative story points because a single-maintainer-plus-agents operation has
no estimation disagreement to mediate — the rubric yields a *deterministic* score a dispatcher can act on (see the
rubric's *Why a fixed rubric*).

**3. Safety override.** The existing `safety-critical` label **pins the item to ≥ `Work Estimate: 4` (top tier)**
regardless of its nominal score — the risk gate, execution, auto-flatten, and kill-switch paths are never routed
to a cheap model, however small the diff looks. Safety can only **raise** the tier, never lower it.

The Work Estimate is assigned by whoever prepares the item in Planning (maintainer or a planning agent). Model
tiers name current examples (Opus 4.8 / Sonnet / Haiku) but are written as *tiers* so the mapping survives model
changes. Both axes are **repo labels** (not board-only fields), so an agent reading the raw issue via `gh` sees
them.

## Epics vs. tasks — they flow differently

An **epic** (`epic` label) is a **container**, not a unit of work, so it does not move like a task:
- It sits in **Backlog** (future phase) or **Planning** (its first increment being scoped), then moves to **In
  Progress** and *stays there* while its child tasks flow through the columns individually.
- It reaches **Done** only when every child task is Done **and its body checklist is satisfied** — which is *not*
  what GitHub's *Sub-issues progress* field reports. See *Closing an epic* below.
- The things that actually traverse Current ToDo → In Progress → Review → Done are the **child tasks**, each a
  `feature/<id>_…` branch with (per the QA contract) an independent test task.
- **Containers are not `Work Estimate`-tagged** — the estimate is a per-task routing signal; a container has no
  single tier. Its child tasks each carry their own. This binds **anything acting as a container, not only what
  carries the `epic` label**: a task that decomposes into sub-issues becomes one, and `gh#595` kept a
  `Work Estimate: 5` for twelve days after it did — routing a top-tier model at a card with no work in it.

### Closing an epic — the progress bar is not the signal

**GitHub's *Sub-issues progress* counts only what was linked.** An epic's actual definition of done is its
**body checklist**, and the two drift independently — so **100% progress is not evidence of completeness**. An
epic can read fully green while a third of its stated scope was never carded at all.

Both failure directions are real (found 2026-07-28 on **gh#26** and **gh#13**, each at 100% with every sub-issue
closed):

| Drift | What it looks like | Seen as |
|---|---|---|
| **Checklist scope never carded** | the bar says done; a task line has no issue behind it | `gh#13`'s Finnhub/Tiingo **market** providers (`gh#411`) and `gh#26`'s AI-spend dashboards (`gh#412`) — neither existed |
| **Delivered work left parentless** | the bar under-reports; shipped issues sit outside the tree | `gh#155`, `gh#164`, `gh#163`, `gh#161` on D1 |
| **Delivered work mis-parented** | progress credited to the wrong container | `gh#220` sat under `gh#12` though it delivered X1's audit-records task |
| **A whole workstream with no checklist line** | shipped work is invisible, and so is its *open* remainder | X1's alerting (`gh#242`–`gh#246`), still carrying `gh#408` / `gh#400` |
| **A container redundant with its own children** | two cards for one job; the container's estimate makes it look pickup-able | `gh#619` held only `gh#722`, which restated it — closed; `gh#595` also held one child but owns content `gh#597` points at — kept |

**So audit an epic before closing it — diff the body checklist against the sub-issue tree, item by item:**

1. For every checklist line with no obvious sub-issue, **search closed issues by keyword before concluding it is
   undone** — delivered-but-unlinked is the common case.
2. **Beware near-miss titles.** `gh#358` / `gh#383` are *news* Finnhub/Tiingo under epic `gh#14`, and read at a
   glance as covering D1's *market* line. They do not; `gh#383` explicitly carves that surface out.
3. **File a card for genuinely uncovered scope**, re-parent stray deliveries, and **tick the boxes with issue
   citations** so the body stops needing the same audit next time.
4. Check the checklist wording still reflects current decisions — an epic body written early keeps asserting
   superseded ones (X1's spend task still said *"operator-only"* long after ADR-0015 reversed that premise).
5. When one child is left, ask **does the container hold scope its children do not?** — not how many are left.
   If yes it is still a container: keep it, and strip its routing labels (*Epics vs. tasks* above). If no,
   **re-parent the survivors first**, then close it.

*Done is Done* applies to containers too: an epic closed on a green progress bar silently drops whatever its
checklist still names.

## Time-boxing

**None, to start.** The board has no iteration/sprint field, and Current ToDo *is* the sprint — a rolling,
prioritized ready-queue refilled from Planning. If velocity/burndown becomes useful later, add an *Iteration*
field (and optionally group by phase with Milestones) without changing any column meaning.

## Adoption (2026-07-23, gh#136)

The board predated this process; adopting it was one clean-up pass:
- **Added the `Backlog` column** (first Status option); **redefined Planning** from "not ready" to "being
  prepared."
- **Moved to Backlog:** the future-phase epics (#13–26) and the `backlog`-labeled items (#3, #27, #41, #64, #66,
  #84, #95, #100, #103, #109). **Current ToDo** kept the genuinely-ready work (#45, #131, #132); #130 was already
  In Progress.
- **Seeded the labels** (`work:*`, `Work Estimate: 1–5`) and applied a first tagging pass to the actionable
  items.

## Automation

GitHub Projects can run the mechanical moves. **Configuring the built-in workflows requires the Project's web UI**
(Project → **⋯** → **Workflows**) — the GitHub API does not expose them — so these are a maintainer setting,
recorded here so the intended state is visible and reproducible. **Two are required, and both set the `Status`
field** — the field that keeps finished or freshly-filed work from stranding in (or beside) an active column:

- **Auto-add → set `Status = Backlog`.** Auto-adding new repo issues/PRs is not enough on its own: the built-in
  *Auto-add* action leaves `Status` **empty**, and an empty-`Status` card reads as unresolved forever. Pair the
  auto-add with a **"Set value → Status: Backlog"** step so every new card lands *in* the funnel, not off to its
  side.
- **Item closed → set `Status = Done`** (and **PR merged → set `Status = Done`**). A closed issue whose card never
  advanced is the most common board-rot source — it looks like live work while being finished.
- Every **forward** gate (`Backlog → Planning → Current ToDo → In Progress`) stays **manual** — those are human /
  agent judgment.

**The agentic complement.** The mechanical `Status` moves above are the gate-free ones a GitHub Action should
perform; the *judgment-gated* moves have an advisory counterpart — the **task-lifecycle workflow**
([`.claude/workflows/task-lifecycle.js`](../.claude/workflows/task-lifecycle.js),
[ADR-0028](adr/0028-task-lifecycle-graph-workflow.md), gh#750). It reads the board, routes each actionable card to
its role agent at its Work-Estimate tier, and **proposes** the next move — writing nothing to GitHub, so the
maintainer still makes every forward decision. It is a Workflow-tool script rather than an Action *because* it
proposes rather than acts; [ADR-0028](adr/0028-task-lifecycle-graph-workflow.md) records why the two automations
are complementary, not alternatives.

> **Enable-state (2026-07-24 — both now enabled).** *Item added → `Status: Backlog`* was already **on**; only
> *Item closed → `Status: Done`* was **off and unconfigured** ("a value is required"). That single gap is why a
> triage pass found **9 closed issues stranded outside Done** (the `closed → Done` gap), while just **5 legacy
> cards** predating the Backlog default carried no `Status`. Both `Status`-setting workflows are now enabled in the
> web UI, and the stranded cards were corrected by hand (`gh#200`).

> **Board hygiene note.** Two known GitHub quirks to watch: **(1)** a Projects item's title can stop tracking its
> issue after a retitle; the fix is to remove + re-add the item, not another retitle — but the re-added item goes
> through *Item added → Backlog* and lands in **Backlog**, so **its Status is not restored; set the column again
> by hand** (observed 2026-07-25). **(2)** if a card ever appears with an empty `Status` (some add paths can
> bypass the *Item added → Backlog* workflow), set the column by hand — an empty-`Status` card reads as
> unresolved forever.
>
> For *placing* an item, prefer the primitive that avoids the dance entirely: **`addProjectV2ItemById` is
> idempotent by content** — given an already-carded issue it returns the **existing** item id rather than
> duplicating it — so `addProjectV2ItemById` → `updateProjectV2ItemFieldValue` places any issue uniformly,
> whether it is new, auto-added, or long since carded. No delete/re-add, so no lost Status.
