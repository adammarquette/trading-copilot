# ADR-0028 — Task-lifecycle graph workflow: an advisory, dry-run orchestration over the board

**Status:** Accepted (gh#750)

## Context

The board lifecycle — Backlog → Planning → Current ToDo → In Progress → Review → Done, with a Planning kickback
and a Review↔Fix cycle — is precise and well-documented ([project-board-workflow](../project-board-workflow.md)),
but it is **manually gated end to end**. An item is routed to a *role* by its `work:*` label and to a *model
tier* by its `Work Estimate: 1–5` (with a `safety-critical` floor), yet nothing reads the board and actually
drives an issue through groom → implement → review → fix → re-review → done. Every move is a human or agent
picking up a card by hand.

That is correct for the **forward gates** — the board deliberately declares each one (Backlog→Planning,
Planning→Current ToDo, the pick-up, the merge) to be human/agent judgment, because this system places real orders
and "ready" is a decision, not a mechanical fact. But there is a large gap between "every gate is a judgment
call" and "a human performs every step by hand." The judgment is at the *gates*; the *reading, routing, drafting,
and proposing* between them is mechanical enough to orchestrate — as long as the orchestration **proposes** and
never **decides**.

This ADR records the shape of that orchestration layer, delivered by gh#750. The adjacent constraints are fixed:

- The board's forward gates are non-negotiably human/agent judgment ([project-board-workflow](../project-board-workflow.md),
  *Transitions & ownership*). An orchestration layer that promotes a card on its own has removed the gate the
  board exists to keep.
- Sessions run in parallel and the board is manual, so **the pushed branch is the claim** (gh#375,
  [CONTRIBUTING.md](../../CONTRIBUTING.md)), not the board column and not the assignee — a reader that treats a
  card's column as ground truth will route confidently wrong.
- An **epic is a container, not a unit of work** ([project-board-workflow](../project-board-workflow.md),
  *Epics vs. tasks*); it stays In Progress while its children flow, and it is not `Work Estimate`-tagged. A reader
  that treats every card as a task will try to route a container and mis-estimate the whole board.

## Decision

1. **The board lifecycle is a directed graph, and the orchestration executes it as one.** The six board columns
   are **states**; the sanctioned transitions ([project-board-workflow](../project-board-workflow.md),
   *Transitions & ownership*) are **edges**, each carrying an explicit **guard** (the condition that must hold to
   propose the move) and an **owner** (who is allowed to make it). The one sanctioned backward edge — **kickback**
   to Planning from Current ToDo or In Progress when an item is underspecified — is a first-class edge, not an
   error path. The **QA bypass** (a QA/SDET suite specified from the requirement lands directly in Current ToDo)
   is modelled as an edge into Current ToDo that skips Backlog and Planning.

2. **The layer is advisory and dry-run: it proposes every move and writes nothing to GitHub.** This is the whole
   point of the card, and it is the board's own posture — the risk gate enforces below the model, and this applies
   the same shape to the board itself. Each run reads the board, routes each actionable issue to the node for its
   column, and asks the mapped role agent to **propose** the next transition (with the drafted artifact that would
   justify it — a scoping note, a plan, a readiness assessment); the output is a **proposed-moves ledger** the
   maintainer reads and approves. The default is dry-run, and **every** GitHub write is gated behind an explicit
   apply flag that is off by default, so the ordinary run mutates neither the board nor any issue. Any pickup that
   quietly adds an ungated write path has built a different, more dangerous thing.

3. **Each node runs at the item's Work-Estimate model tier; `safety-critical` floors the tier at top.** The
   per-node executor is dispatched at the model tier the item's `Work Estimate` names
   ([work-estimate-rubric](../work-estimate-rubric.md)) — a cheap tier proposes a trivial move, the top tier
   proposes a complex one — and the `safety-critical` label **pins the tier to top regardless of the nominal
   score**, exactly as the board's routing rule already specifies. The tiering is a guideline the layer reads from
   the label, never a hard contract it invents.

4. **The board-reader discriminates container-from-task and stale-from-live claim before anything is proposed.**
   A reader that cannot tell a container from a task, or a stale claim from a live one, proposes confidently wrong
   moves — the failure Adam's gh#750 board-hygiene pass measured (of 57 open cards: two containers redundant with
   their own children, eight epic checklists drifted from their sub-issue trees, seven stale claim branches). So
   the reader surfaces three shapes **explicitly** as structured fields, and the router honours them: a
   **container** (an `epic`, or any task that has decomposed into sub-issues) is never routed as a task — its
   children are the units that flow; a **claim** (a pushed `<type>/<id>_…` branch) marks its issue as taken, and a
   claim whose tip has not moved in the staleness window is flagged **stale** rather than treated as either live
   or absent.

5. **The Review↔Fix cycle is a bounded loop with a maintainer-escalation exit.** An item in Review is not driven
   to Done by the layer — the author agent owns the card there, spawns the reviewer, and blocks on the verdict;
   on `CHANGES-REQUESTED` it fixes and re-reviews. The layer models this as a **bounded** loop: it proposes "stay
   in Review, address findings" up to a cap, and past the cap proposes **escalate to the maintainer** rather than
   looping forever. Done is proposed only when a linked PR is merged.

6. **Delivered as a Workflow-tool script, not a GitHub Action.** The orchestration lives at
   [`.claude/workflows/task-lifecycle.js`](../../.claude/workflows/task-lifecycle.js) and runs through the
   Workflow tool, **not** `.github/workflows/`. The rationale is the dry-run posture: a GitHub Action runs on
   GitHub's triggers with a token and is shaped to *act* (it is the natural home for the mechanical Status-setting
   moves the board already automates — auto-add→Backlog, closed→Done — which are exactly the gate-free ones). The
   agentic layer is the opposite: it is invoked deliberately by a maintainer, does the model-driven reading and
   drafting, and **proposes**. Putting it in Actions would both misplace it (it is not triggered by a push) and
   invite the ungated write path decision 2 forbids. The two automations are complementary, not alternatives:
   Actions performs the gate-free mechanical moves; this proposes the judgment-gated ones.

## Consequences

- The board gains an orchestration layer that does the mechanical reading, routing, and drafting between gates,
  while **every forward gate stays a human/agent decision** — the layer never promotes a card, it hands the
  maintainer a proposed-moves ledger to approve. The enforcement-below-the-model posture the risk gate takes now
  covers the board.
- The layer is **safe to run at any time**: dry-run by default and write-free by construction, so a run has no
  side effect on the board, the issues, or any branch. Its cost is the model calls it makes to read and propose,
  bounded by the tier routing (a board of trivial cards costs little; a safety-critical card is read at top tier).
- The board's existing invariants are now **executable readings**, not just prose: container-vs-task, stale-vs-live
  claim, the Work-Estimate tiering, and the kickback edge are all things the reader and router must get right, so
  a drift in any of them shows up as a wrong proposal a maintainer can catch — rather than silent board-rot.
- **Not in scope:** the layer does not perform the work of a card (it proposes the *next move* and drafts the
  artifact that justifies it, not the full implementation); it does not enable any GitHub write in its default
  posture; and it adds no `.github/` / CI automation. Turning a proposed move into an applied one stays a
  maintainer action behind the apply flag.

## Follow-ups

- An **apply mode** (behind the off-by-default flag) that performs an approved subset of proposed moves — the
  mechanical Status-setting ones the board already sanctions for Actions — is a later increment; it must keep the
  forward *judgment* gates human even when the *mechanical* move is automated.
- Feeding the proposed-moves ledger a **budget** (the Workflow tool's token target) so a large board scales its
  depth to a directive, rather than reading every card at full depth, is a natural extension once the layer has
  been watched on real boards.
- A **board-hygiene report** — the two-redundant-containers / eight-drifted-checklists / seven-stale-claims shapes
  Adam's gh#750 pass found by hand — falls naturally out of the board-reader's classification and is worth
  surfacing as its own output alongside the proposed moves.
