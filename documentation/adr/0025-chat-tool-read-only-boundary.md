# ADR-0025 — The chat co-pilot's tools are read-only, by construction

**Status:** Accepted (gh#925, chat epic #18 inc 4, R-6)

## Context

The co-pilot chat (R-6) grew a **tool layer**: the model can call in-process tools to ground its reply in the
operator's real data (a quote, the journal), rather than answering from the conversation alone. This inverts the
prior increments' control flow — for the first time the **model chooses an action** (which tool, with what input)
and the app **executes it**. That is exactly where "the five that are never traded away" put a hard line:
**enforcement lives below the model; the LLM only proposes.** A tool that could place, size, or modify an order
would hand the model a second path to the broker — the same path [ADR-0021](0021-chat-turn-delivery.md) refused for
the realtime hub.

The operator was asked (2026-08-15) and chose a **read-only** tool set for this increment.

## Decision

1. **Tools are read-only by construction, not by convention.** An `IChatTool` injects only **read** seams (the
   journal read, the market-data read) and reaches no order / execution / write type. There is no registered tool
   that can place, size, or modify an order. "Read-only" is therefore a property of *what is wired*, not a runtime
   check that could be bypassed — the write tools the PRD anticipates (generate-suggestion, edit-rulebook) are
   deliberately **not** in this increment (gh#925 follow-ons), and even those, when they land, propose rather than
   execute.

2. **The model can only ever run a tool from the fixed offered set.** `ChatTurnService` dispatches a tool call only
   to a matching registered `IChatTool`; a name it does not recognise — a tool the model *invents*, including an
   order-shaped one — is **never dispatched** and returns a fail-closed error result. So even a jailbroken or
   confused model cannot reach beyond the read-only set.

3. **The loop is bounded and fail-closed.** Round 1 streams; a tool-use stop runs the requested read tools, feeds
   their results back, and loops via `CompleteAsync` to a hard round cap (**4**) → the turn fails closed. The model
   cannot drive an unbounded call/spend sequence, and any stop other than a clean completion is never surfaced as
   the co-pilot's answer.

4. **Every model call is metered and ledgered.** A tool-using turn makes several calls; each is priced
   (`AiUsageFeature.Chat`) and recorded fail-open, so the AI-spend governor's floor sees every billed call.

5. **Owner-scoped reads (R-20).** Each tool runs under the request's `ICurrentUser` via its scoped read services, so
   the model reads only the operator's own data. Market data is the documented global exception (R-20) and carries
   no tenant filter.

## Consequences

- The co-pilot becomes genuinely grounded (it can look things up) **without** widening the execution surface — the
  broker path is still reached only by an explicit operator UI action (R-11).
- A future **write** tool is a deliberate, separately-reviewed decision (its own increment), never an accident of
  adding a tool — and it too must propose, not execute.
- The round cap and the offered-set-only dispatch are the loop's safety backstops; both carry unit tests
  (round-cap fail-closed, unoffered-tool-never-dispatched).

## Follow-ups

- ✅ `read_positions` (venue-truth) landed as its own increment (gh#929), completing the
  `get_quote` / `query_journal` / `read_positions` read triad. It depends on a new **read-only**
  `IPositionReconciler` seam onto `PositionReconciliationService` (gh#193) — the tool injects the narrow read
  interface, not the concrete service (which does live venue I/O on the execution-recovery paths), so the tool is
  fakeable in unit tests and its dependency is read-only by its very type. An unreachable venue is reported
  **declared-unknown** (never a fabricated flat), and it reconciles the operator's own accounts (R-20) — **all** of
  them, deliberately not filtered by `IsActive`, because deactivation is a soft-delete that does not close open
  positions, so filtering would hide live exposure and fabricate a flat.
- ✅ `search_news` (gh#987) landed as a fourth read-only tool and the **first `IReranker` consumer** (gh#975): a
  semantic search over the operator's ingested news feed (embed the query → nearest-news recall → hydrate →
  rerank → top-k). It stays read-only **by its very construction** — it injects only read / compute seams (the
  embedding provider, the `INewsEmbeddingSimilarity` read, the `IReranker`), the scoped `TradingCopilotDbContext`
  used read-only, and the fail-open AI-spend ledger — reaching no order / execution / write type. News is the
  **R-20 global exception** (a `NewsRecord` is not `IUserOwned`), so the hydrate read carries no owner filter, like
  `get_quote`; the spend it bills is still stamped to the operator. Consistent with point 5 above, **retrieved news
  text is untrusted display data the model reads, never instruction** (enforcement stays below the model). It
  **degrades, never throws**: no embedding provider / a null query vector / a faulting pgvector read collapse to an
  empty result, and an unavailable rerank degrades to the recall's identity order (the tool reads the seam's list
  order) — and, per point 4, it ledgers its own embed (`Embed`) and rerank (`Chat`) spend
  ([ADR-0008](0008-ai-invocation-cost-model.md)). The read set is now
  `get_quote` / `query_journal` / `read_positions` / `search_news`.
- ✅ **Always-on grounding** (gh#995, inc 5, [ADR-0027](0027-always-on-retrieval-grounding.md)) extends this
  boundary from *model-elective* retrieval to *every* turn: the `search_news` embed → recall → hydrate → rerank
  pipeline is extracted into a shared, read-only-by-construction `INewsRetrievalService` (the tool becomes a thin
  adapter over it), and every turn retrieves a little news for the operator's message. The retrieved text stays
  **untrusted display data the model reads, never instruction** — this closing note made literal: it is placed as
  **user-role content** behind a fixed data-not-instructions envelope and **never** the fixed system prompt (which
  still holds no risk limits or account state), carrying the same injection-sentinel guard as the message-content
  path. It rides this increment's **single** governor gate + fail-open ledger, is threshold-skipped before the cap,
  and fails open to a history-only turn — so grounding never widens the execution surface or the instruction surface.
- Streaming a *tool-using* turn's final answer (removing the round-1 double-call) is inc 4b.
