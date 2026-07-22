# AGENT-MEMORY.md

**Purpose — the agent catch-all.** This file is where AI coding agents (Claude Code, Copilot, or any other)
record and communicate things that must persist across sessions but **don't fit any other formal document**:
practices Adam has asked us to follow, cross-agent heads-ups, and decisions that don't yet have a home in the
PRD, the engineering guide, `AGENTS.md`, or the code.

**It is deliberately informal, and it is overflow — not a substitute.** The formal documents remain
authoritative. If something belongs in the PRD (product requirements), the engineering guide
(`documentation/trading-platform-engineering.md`), `AGENTS.md` (agent rules), or the code — **put it there
instead.** This file is for what would otherwise be lost between sessions because it fits nowhere formal.

**How to use it**
- **Read it before starting work** — another agent (or Adam) may have left guidance here.
- **Append, don't overwrite.** Add entries under the right section and date them (`YYYY-MM-DD`) so the history
  stays legible.
- **Promote when it grows up.** If an informal note here becomes stable enough to belong in a formal doc, move
  it there and leave a one-line pointer behind.
- Keep entries terse and concrete — this is shared working memory, not an essay.

---

## Practices to follow

Working practices Adam has asked agents to follow that have no formal-doc home.

- **[2026-07-18] Lightweight scaffold first; stay agile.** For planning / requirements / engineering-practice
  docs, build a *minimal* scaffold first — enough structure and known decisions to reference and decide
  against, with open choices flagged (a `Decide:` marker) — rather than an exhaustive standard up front. Don't
  over-invest until there's a substantial plan; deepen sections only as the plan firms up.
- **[2026-07-18] Apply, then review.** Make non-trivial changes directly in the files and let Adam review the
  diff (version control makes it safe), rather than proposing every change for approval first. Trivial factual
  corrections always go straight in.
- **[2026-07-22] Always use `git worktree` for isolated work.** When working on features, tests, or fixes, use isolated `git worktree` directories (or `Workspace: "share"` subagents) to prevent stepping on or dirtying other active agents' working trees.

## Notes & communications

Cross-agent heads-ups and in-session decisions that don't have a formal home yet. *(none yet)*

---

*Part of the repo's living memory for agents. If you're an agent reading this: check the sections above, keep
entries current, and leave things better than you found them.*
