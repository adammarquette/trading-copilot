# ADR-0025 — The chat co-pilot's tools are read-only, by construction

**Status:** Accepted (gh#925, chat epic #18 inc 4, R-6) · extended by the first write tool (gh#1134) — see *Update*

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

## Update — 2026-09-05: the first write tool (gh#1134 of gh#1059)

`generate_suggestion` landed. **The decision above is not superseded — it is exercised.** Point 1 said the write
tools were deliberately out of that increment and that "even those, when they land, propose rather than execute",
and the Consequences said a write tool would be "a deliberate, separately-reviewed decision (its own increment),
never an accident of adding a tool". This is that increment, so it is recorded here rather than as a new ADR: no
prior decision changed, and a second record would leave two documents claiming to define one boundary.

What the increment establishes, and what a future write tool inherits:

1. **A write tool stages an artifact that is inert until the operator acts.** `generate_suggestion` writes an
   `Active` `Suggestion` and pushes a card. It is **never auto-taken**: only the operator's own take reaches the
   execution path, and the risk gate runs then, below the model, exactly as it does for a scan-issued suggestion
   (R-11). *A proposal is not an execution*, and *staging is not taking* — the second half is now asserted
   directly (zero `SuggestionDisposition` rows in every case of the gh#930 suite, the write cases included).

2. **Nothing the model would be enforcing is in its schema.** The input carries no size, mode or expiry property
   at all: size is the operator's configured `Suggestions__ChatProposalSize`, mode is read live off the account
   (R-14), and the expiry is the configured window clamped against the market's auto-flatten deadline (R-13).
   Prices go through the same `SuggestionGeometry` check the scan applies. This is the general rule for a write
   tool: **remove the choice from the schema rather than validating the model's answer to it**, because a schema
   the model never sees a field in is a stronger guarantee than a check it might argue past.

3. **Fail closed leaves nothing behind — and a refusal must be *answerable*.** An incoherent geometry, a
   malformed argument, an undeclared / untradable / inactive account, or an *ambiguous* one all stage **nothing**
   and return an error string the model reads. Ambiguity is deliberately a refusal rather than a default: the
   account is which money the setup is proposed against, and that choice does not become the model's by default.
   The review of this increment found the sharp edge that comes with it: a fail-closed refusal the model **cannot
   resolve by any input** is a functional dead end, not a safe default. `Account.Name` is not unique, so two
   connections carrying same-named venue accounts produced *"name the account explicitly — X, X"* forever. So the
   rule a write tool inherits is stronger than "fail closed": **whatever a refusal asks the model to send, the tool
   must accept**. Here each candidate carries a label unique within the operator's proposable set.

4. **A second producer of an existing entity owes that entity a producer field.** `generate_suggestion` made
   `Suggestion` two-producer, and everything downstream had been written when it was one: the operator's card
   rendered a chat proposal through the scan's citation line as `cited signal · (0) · 0m`, and the R-4 throttle's
   per-account window counted every row regardless of who wrote it, so chat could silence the scan for a trading
   day. Neither was reachable before. `Suggestion.Origin` is stored rather than inferred from the absent trigger
   link, because the absence was already overloaded — an empty cited-factor set is *also* a read bug — and a
   surface that cannot tell a new producer from a defect will render the defect as the producer.

5. **A write tool writes in its own owner-scoped transaction**, not the turn endpoint's request context that the
   read tools inject. Otherwise a refused turn would commit the proposal anyway when the endpoint saved, and a
   `Suggestion` CHECK violation would surface as a failure of the endpoint's conversation write — a constraint
   backstops only its own transaction's owner.

6. **The boundary is now pinned twice, and the read-only suite was extended rather than replaced.** The
   structural half enumerates **every** `IChatTool` by reflection — so the *next* tool is covered on sight rather
   than when someone remembers a list — and pins a write tool's constructor dependency set *exactly*, because a
   forbidden-fragment scan over direct parameters is defeated by one helper indirection. The behavioural half is
   the gh#930 suite: its order tripwire is unchanged, its staged-suggestion count is now **stated per case**
   (still zero everywhere it was, the theory rows included, so merely *offering* a write tool stages nothing),
   and zero dispositions joins it unconditionally.

**What did not change:** points 2–5 of the Decision stand verbatim. The model still runs only tools from the
fixed offered set, an invented order-shaped name is still never dispatched, the loop is still bounded and
fail-closed, every model call is still metered and ledgered, and every tool is still R-20 owner-scoped. The
title's "read-only" is now the *read set's* property rather than the whole layer's; the invariant the title was
protecting — **no tool reaches an order, venue or gate type** — is unchanged and is what the structural guard
enumerates.

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
