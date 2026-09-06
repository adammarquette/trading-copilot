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

## Update — 2026-09-05: the write tools (gh#1134 and gh#1135 of gh#1059)

`generate_suggestion` landed, and then `edit_rulebook` beside it (see *The second write tool* below). **The
decision above is not superseded — it is exercised.** Point 1 said the write
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

### The second write tool — `edit_rulebook` (gh#1135)

Recorded here rather than as ADR-0029: it is the same boundary, and a second document defining one boundary is how
two documents come to disagree about it. Points 1–6 above are the rules it inherited; four more it establishes.

7. **A write tool may write into an ARMED subsystem, provided what it writes is in a state that subsystem
   refuses.** `edit_rulebook` writes a `TriggerRecord` — a row the scan reads on a timer and can turn into a page.
   That is a materially louder artifact than a `Suggestion`, which sits inert until read. What makes it safe is not
   the tool's restraint but gh#470's confirmation gate: the scan's due-set predicate takes only `Confirmed` rows,
   so an authored rule is inert **regardless of `Enabled`**, and the operator's separate `POST /{id}/confirm` is the
   only thing that arms one. *"Written by the model" and "armed" must never be the same act.* The generalisation:
   **inertness must be a state the reader enforces, not a promise the writer makes** — a tool that merely declined
   to set a flag would be one careless edit away from arming.

8. **The amend path is where that gate leaks, so an amend disarms.** Authoring an Unconfirmed rule is obviously
   safe; editing one the operator had *already confirmed* is the path by which a model change reaches the live
   firing set, because the confirmation was given to the **old** condition. So every amend returns the rule to
   Unconfirmed, and re-seeds the debounce (a condition that became true under the old definition must re-seed
   silently, and a fresh incident cycle stops the next genuine crossing being suppressed as a duplicate). The route,
   account and size are never touched at all: chat authors the mechanical route only, so a chat edit can never turn
   an alert into a sized proposal against an account. **That disarm only fires when something actually changed
   (gh#1155):** an amend naming an identity field (symbol / indicator / period / resolutionMinutes — those name
   *which* rule this is, so changing one is authoring a different rule) or a present-but-wrong-typed value (a
   string where a number belongs, which a parser had been reading as *absent* rather than as the malformed
   argument it is) both refuse before the row is touched, and an amend naming no amendable field at all refuses
   too — a model can no longer report `"amended"`, and disarm a confirmed rule, for a change it never made.

9. **A second author of an existing entity shares the first author's rules — the code, not a copy.** The condition
   half's refusals moved out of `TriggerEndpoints` into `TriggerAuthoring`, one refusal per check so each caller
   keeps its own evaluation order and no endpoint behaviour moved. gh#1007 is the precedent: the same threshold gap
   had to be fixed twice, at create *and* at patch — and a model-authored row is exactly the one you least want
   validated by the older copy. This is the write-side twin of point 4: a new **producer** owes the entity a
   producer field; a new **author** owes it the existing author's validation.

10. **A write tool gets exactly the turn identity it needs, and fails closed without it.** A chat-authored rule
    stamps the conversation it came from (`SourceConversationId`, gh#471), which is what makes *"why does this rule
    exist?"* answerable at the read path. `IChatTurnScope` carries that one `Guid` and nothing else — deliberately
    not a general per-turn bag, since a seam that accumulates state is how a tool eventually reaches something it
    should not, and the pinned allow-list can only vouch for what a seam can hand out. It is a **required**
    dependency: a null scope means the endpoint never entered it, i.e. the wiring is broken, and an optional
    dependency defaulting to "no provenance" would write an *unattributed* rule and look like it worked. The scope
    is entered **after** the R-20 owner check, never before.

**And one thing the boundary test learned.** The pinned constructor allow-list is now **one set per write tool**,
not a shared union. A union lets each write tool inherit the other's collaborators for free — `edit_rulebook` would
have silently acquired the deadline source and realtime notifier it has no business holding, and the *next* write
tool would start from everything shipped. The allow-list is a guard only while it is the narrowest true statement
about each tool. A companion assertion catches a write tool with **no** entry at all (identified structurally, by
the `DbContextOptions` write handle a read tool never holds), because a guard that silently applies to nothing is
worse than none.

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
- **The instrument a write tool is handed is syntax-checked, not validated (gh#1134 review; carded as gh#1153).**
  `generate_suggestion` parses the model's symbol through `InstrumentId.TryParse` but does not confirm it names a
  configured, tradable contract, so a hallucinated symbol stages a card the take path refuses at spec resolution and
  the drift sweep re-resolves once a pass until it expires. It is fail-closed and cosmetic today, and it is recorded
  here rather than in a merged PR's description because it is the one place a model-chosen string transitively
  reaches a venue call — which is worth knowing beside the boundary claim above. Closing it means giving the tool an
  instrument-spec read, which **widens the write tool's pinned constructor allow-list**: a deliberate, separately
  reviewed act, not a rider on the increment that introduced the tool. **gh#1135 inherited it rather than closing
  it**: `edit_rulebook` parses its symbol the same way, and a hallucinated one there writes a rule whose indicator is
  never measurable — which surfaces as the gh#469 staleness advisory rather than as an authoring refusal. It is also
  at **parity with the operator's own `POST /api/triggers`**, which has always accepted any parseable symbol, so
  closing it for the tool alone would make the model's authoring stricter than the operator's — a decision gh#1153
  has to make rather than inherit.
- Streaming a *tool-using* turn's final answer (removing the round-1 double-call) is inc 4b.
