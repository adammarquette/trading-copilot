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

- `read_positions` (venue-truth) is a read-only tool deferred from this increment (gh#925 follow-on).
- Streaming a *tool-using* turn's final answer (removing the round-1 double-call) is inc 4b.
