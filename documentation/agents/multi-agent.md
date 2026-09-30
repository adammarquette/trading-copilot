# Multi-agent board — one lead, other agents until review

How more than one agent tool shares the project 2045 board. The root [`AGENTS.md`](../../AGENTS.md) still
applies, and carries the one rule every tool must see without opening this file. The lead's side of it is
also in [`coordinator.md`](coordinator.md) *Multi-agent mode*. Maintainer rulings, 2026-09-28
(`gl#824`, and the board note on `gl#812`).

## Why this is written down

Every actor posts as the same GitLab user, so the tracker cannot tell one tool from another. Cursor
Composer and other tools read the repository's `AGENTS.md` files, not GitLab notes, so a rule that lives
only on the board does not reach them. This file is where the rules live; the root `AGENTS.md` routes here.

## One lead

**The Claude Code Coordinator is the Coordinator of record.** It cuts sprints, sets dispatch order, and owns
every step from `work::review` on: review, fix rounds, the acceptance-criteria check,
`work::awaitingapproval`, the sprint-group merge on the maintainer's explicit approval (`coordinator.md`
*From Sprint 17*), and `work::done`. It has the final call on `work::blocked`. `work::backlog` stays the maintainer's (root
`AGENTS.md`). Its contract is [`coordinator.md`](coordinator.md), whose *Multi-agent mode* section is the
lead's side of this file.

## Other agents work a ticket only until review

Cursor Composer, or any other tool that is not the lead, may:

1. **pick an item from `work::todo`** that is not already `work::inprogress`;
2. **claim it with a signed comment** (below) and move it to `work::inprogress` in the same step;
3. implement it on its own branch and worktree, following the root `AGENTS.md` and the subtree contract —
   **except that contract's review loop**: spawning a reviewer, waiting on a verdict, taking findings and
   an approved verdict as the definition of done are the lead's in this mode, and each subtree contract
   says so where it describes them;
4. **open the MR** with a bare `Related to #N`, move the item to `work::review`, post a signed hand-back
   note naming the MR, **and stop.**

**Its work ends there**, when it opens or amends the MR and moves the item to `work::review`. After that it
does nothing on the item (maintainer ruling, 2026-09-28: *"Do not let composer do anything after it is done
with the coding tasks. It should never merge."*). It **never**:

- merges or approves — not the GitLab button, not auto-merge, not `merge-gate.sh --merge`;
- reviews, or spawns a reviewer;
- ticks or unticks an acceptance-criteria box;
- moves an item past `work::review`, or into or out of `work::blocked` or `work::backlog`;
- re-orders any column, or cuts a sprint;
- runs anything against a deployed environment.

A fix round starts **only when the lead moves the item back to `work::todo`** (below); review findings on
an item still in `work::review` are not the other agent's to pick up.

This narrows the root rule that only the Coordinator takes items off `work::todo`: another agent may take
one, signed. Deciding what is *in* To Do stays the lead's.

## Every claim is signed

**The claim comment names the tool**, because the comment is the only thing that tells actors apart:

- `Picked up by Claude (Coordinator)` — the lead, including work it dispatches to its own agents;
- `Picked up by Cursor Composer` — or the tool's own name. **Never sign as a coordinator**: there is one,
  and a second "Coordinator" in a signature is what a reader mistakes for the lead.

Sign the hand-back note at `work::review` the same way. **The lead detects another agent by any `Picked up
by <tool>` signature that is not its own** — older claims signed `Picked up by Cursor Coordinator` are
Composer's. An unsigned claim is a defect: the lead treats it as another agent's and asks on the issue.

**Which mode you are in is on the issue.** A tool that is not Claude Code, or whose claim names another
tool, is not the lead and follows this file. A Claude agent dispatched by the lead, or working with no
other tool's claim on its issue, follows its contract as written.

## The review loop on another agent's MR

The lead reviews, as for any MR. On `Request changes`:

- **The lead moves the item back to `work::todo`** and posts a note listing what the fix round must do,
  drawn from the verdict.
- **An implementing agent may pick the fix round up like any To Do item**: claim it, signed; amend the
  **same MR**; hand it back at `work::review`, and stop again. Only until review, on every pass.
- **The lead may run the fix round itself** when that is faster — the item is urgent, or the review is
  already in its hands. It claims the round, signed, and dispatches its own agents. This exception is the
  Coordinator's relay of the ruling (`gl#824` note 106283, and the issue body's "routes their fixes to its
  own agents"), not the maintainer's quoted words.

On `approved`, the item stays in `work::review` and the lead carries it on, as for any MR.

## Hands off another actor's work

- Never push to, rebase or reuse another actor's branch or worktree without a note on the MR first. A fix
  round on the same MR is that note: the signed claim.
- Never force-push over a head you did not just read. The push rule in the root `AGENTS.md` —
  `--force-with-lease --force-if-includes` — is what enforces it.

## Reserved work stays reserved

- An item whose issue says the maintainer has taken it is not authored by any agent.
- Content that needs its own review before it exists in the repository — concrete attack inputs, for
  one — is not authored by any agent unless its issue says so.
- **No agent runs adversarial content against a deployed environment before that content has been
  reviewed.**

## Why no Cursor rule

Cursor loads `AGENTS.md` natively, root and nested, and the repo carries no `.cursor/` directory. The one
rule every tool must see sits in the root `AGENTS.md` itself; everything else is here, one link away. A
Cursor rule would be a second copy to keep in step. Add one only if Cursor is shown not to load
`AGENTS.md`.
