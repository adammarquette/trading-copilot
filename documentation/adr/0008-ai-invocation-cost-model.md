# ADR-0008: AI invocation & cost model — deterministic triggers, LLM at the edges

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-4` (suggestions / continuous scanning), `R-6` (chat / follow-ups), `R-7` (rulebook —
triggers as rules), `R-8`/`R-9` (journal / feedback loop), `Q-10` (cost); engineering §1 (patterns), §2
(`ILlmProvider`, Cohere), §5 (evals); [ADR-0001](0001-event-backbone.md) (event log / pre-computed indicators),
[ADR-0002](0002-observability.md) (tracing), [ADR-0007](0007-order-execution-model.md) (gate below the model).
Wireframes: [`../design/`](../design/) (day-detail follow-ups).

## Context
The co-pilot continuously watches the market and can proactively surface suggestions, alerts, and follow-up
questions (R-4/R-6). Doing that by running an **LLM over the live market** — every tick, or on a short poll — would
be **unbounded in cost, too slow for scalp decisions, and rate-limited**. We already hold two related principles:
**pre-compute the numbers because LLMs are weak at (and expensive for) numeric work** ([ADR-0001](0001-event-backbone.md)),
and **enforcement lives below the model** ([ADR-0007](0007-order-execution-model.md)). This ADR extends that
discipline to **how and when the LLM is invoked at all**, so cost is bounded and predictable (`Q-10`).

## Decision
- **The LLM is never in the continuous hot loop.** A **deterministic trigger layer** — stream processors /
  consumers evaluating **conditions over the pre-computed indicators, order-flow, price, and other state**
  ([ADR-0001](0001-event-backbone.md)) — does the always-on watching. It is CPU-cheap, low-latency, and scales to
  any tick rate. This is where R-4's "continuous scanning" actually runs.
- **Triggers are structured rules the AI helps author (R-7).** At **setup time** the operator states intent in
  natural language ("alert when ES reclaims VWAP with rising buy delta"); the co-pilot **compiles it into a
  concrete, machine-evaluable trigger** and persists it as a **rulebook** entry (R-7), with explicit confirmation.
  The model is used to *compile* intent into a deterministic condition — **not** to evaluate it repeatedly.
- **A trigger routes to one of two outcomes.** A fully-specified **mechanical** setup fires a **deterministic
  alert / suggestion with no LLM**. A setup needing judgment **wakes an agent to review / enrich / decide** — **one
  LLM call per event**, not per tick. Which route a trigger takes is part of its definition.
- **The LLM works only at the edges, event-driven:**
  1. **Author** triggers (setup-time, one-off).
  2. **Review / enrich** on a trigger fire — generate the suggestion + rationale, or decide it isn't worth
     surfacing.
  3. **Review & follow-ups** — the "did you like this / here's a pattern across your trades" conversation (R-6/R-8)
     is **batched or on-demand** (on journal open, or a daily digest), never continuous.
- **Cost scales with triggers-fired + operator interactions — not with market-data rate.** This is the crux: an
  unbounded continuous bill becomes a small, predictable one proportional to how often setups actually fire and how
  much the operator engages.
- **Model tiering + spend controls.** A **cheaper model** (Haiku / Sonnet via the `ILlmProvider` seam) triages "is
  this worth surfacing?" and only **escalates to the top model** for genuinely hard synthesis. Triggers are
  **debounced / rate-limited** so a flickering condition can't fan out many reviews. An optional **AI-spend
  governor** — mirroring the daily *risk* governor (R-5 / ADR-0007) — can cap or throttle agent invocations against
  a budget (`Q-10`); in the **multi-user** model (R-20) it is a **platform-level** cap the **operator** sets — one
  shared LLM + embeddings account funds every user, so usage & spend is reported in **Grafana, not surfaced to end
  users** *(revisit per-user if invitees later bring their own LLM accounts; gh#4)*. Every invocation is **traced** ([ADR-0002](0002-observability.md)) so cost and latency are
  observable. Spend is **metered** (gh#403): every Cohere embed call records tokens, estimated cost and latency on
  the `MarqSpec.TradingCopilot.Ai` meter, by model and outcome, and a failover is counted so a degrade is visible.
  Cost is estimated from a single pinned rate. The **persisted `AIUsage`** record — the in-app spend meter and the
  durable governor ledger — **landed** (gh#431): the agent-review reviewer records one per-call token / cost /
  latency row under the firing owner's scope. It is a **floor, not a complete accounting** (fail-open, lossy on host
  death). The **governor landed** (gh#448) and enforces on that ledger floor; the gh#448 follow-up records **why it
  cannot reconcile against the meter in-process** (that meter is export-only — Prometheus, not app-readable — and
  covers embeddings only today) and what that means for the effective cap.
- **The strategy-agent → executor flow attaches here.** Strategy agents are the "review / enrich on fire"
  consumers; the executor synthesizes their outputs into a timely suggestion — invoked **on triggers, not
  continuously**. Their proposals still pass the deterministic risk / execution gate ([ADR-0007](0007-order-execution-model.md)):
  this ADR governs **when** the model runs; ADR-0007 governs **what happens to what it proposes**.

## Alternatives considered
- **Continuous LLM evaluation** (poll the model on a tick / interval). Rejected — unbounded cost, latency unfit for
  scalping, and rate limits. The thing this ADR exists to avoid.
- **All-deterministic, no LLM in the loop.** Cheapest, but loses the judgment / enrichment and the conversational
  refinement (R-6/R-7) that make it a *co-pilot*. Kept the LLM for the edges only.
- **One always-on "market-watcher" agent** (a single long-running LLM context streaming market state). Still
  continuous-cost and context-bloated; the deterministic layer + event-driven wake is strictly cheaper and more
  scalable.
- **Top model everywhere.** Simpler routing but wasteful — cheap-model triage + selective escalation captures most
  of the value at a fraction of the cost.

## Consequences
**Positive**
- **Bounded, predictable cost** (`Q-10`) — scales with events, not data rate; controllable by model tier, debounce,
  and a spend cap.
- **Low latency on the hot path** — deterministic triggers fire in the stream; the LLM's slower step is off the
  critical path (or absent for mechanical setups).
- **Reuses what we built** — pre-computed indicators (ADR-0001), the `ILlmProvider` seam + Cohere fallback
  (engineering §2), tracing (ADR-0002), and the rulebook (R-7).
- **Clean division of labor** — deterministic where determinism belongs (watching, math, enforcement); the LLM
  where judgment / language belongs (authoring, review, conversation).

**Negative / costs**
- **Trigger authoring is a real feature** — the NL→condition compiler (R-7) must produce correct, testable
  conditions; a mis-compiled trigger is a silent miss or a false alert.
- **A trigger DSL / condition model** to design (indicator thresholds, price levels, order-flow, cross-asset,
  time-of-day, composites).
- **Two-tier routing + escalation logic** to build and tune (when does a mechanical alert escalate to an agent?).
- **Debounce / rate-limit + the spend governor** are new controls to design and validate-on-start.
- **Coverage-vs-cost tradeoff** — cheap-model triage may under-surface; needs evaluation and is tunable.

## Follow-ups
**Update (2026-07-28, gh#385) — the deterministic mechanical route landed.** A structured **indicator-threshold**
condition (typed columns + a `ConditionKind` discriminator — *not* a DSL, since one kind serves no DSL yet), a pure
**edge-debounce** state machine (`Unseeded → Armed → Fired`: fire once on the arming edge, re-arm on the opposite,
seed pre-existing truth silently, and hold fail-closed on a null indicator) — **and, since gh#469, say so**: the hold was right but silent, so `UnmeasurableSince` tracks how long a trigger has been unevaluable and the scan warns once past 30 minutes, naming the missing indicator. Duration is the only thing that separates a late bar from a dependency that stopped being produced; it never disables the trigger, because a pipeline hiccup must not silently disarm an alert, and a **periodic evaluator** that fires
a **mechanical alert** through the notification channel — no LLM. Structural authoring via the API stands in for the
compiler for now; a nullable `SourceRuleId` is the seam the compiler will fill. Still open below: the NL→condition
compiler + the `Rule` entity, the **agent-review** route, order-flow / composite / cross-asset / time conditions,
the wall-clock **rate-limit** (a `LastFiredAt` seam shipped), and the **AI-spend governor**.

**Update (2026-07-28, gh#402) — the agent-review route landed, seam-first.** A trigger routing to **agent-review**
now, on a fire, wakes a reviewer (**one LLM call per fire**) behind a provider-neutral **`ILlmProvider`** seam that
**proposes a `Suggestion` or suppresses** — the first place an LLM enters the system, at the edges. **Enforcement
stays below the model, proven structurally**: the review path reaches no order / venue / gate type (a
constructor-graph test guards it), and a malformed or hostile output is stopped by **three layers** — a fail-closed
reviewer (a non-complete stop, bad JSON, unknown decision/direction, or a missing price → suppress, never a
suggestion; the wire uses a *string* direction so a missing field can't read as Buy), a pure geometry sanity check,
and the take-time risk gate. **Size is the operator's trigger's, never the model's; mode is read live from the
account.** The real **Anthropic client + `AIUsage` cost tracking** are still A2 — a **stub** stands in, so production
never fabricates geometry, and an honest inert reviewer *tells* the operator a setup fired needing review. Still
open: the NL→condition compiler + `Rule` entity, order-flow / composite conditions, model-tiering escalation, and
the AI-spend governor.

**Update (2026-07-28, gh#423) — the real Anthropic adapter landed; the seam is now live.** `ILlmProvider` binds a
real **`AnthropicLlmProvider`** when a key is present (the *same* switch that picks the reviewer), and the **stub**
now stands in **only when unconfigured** — so a configured deployment wakes a live model and an unconfigured one
still cannot fabricate a suggestion. It is a **raw `HttpClient` adapter, not an SDK** (dependency-minimal +
permissive-licence posture + trivially faked; mirrors `PushoverNotificationChannel`), one POST to the Messages API
per fire. **Model tiering is settled**: `LlmModelTier.Triage → claude-haiku-4-5`, `Deep → claude-sonnet-5`,
operator-overridable via `LlmOptions` (non-secret). The **key rides the `x-api-key` header, never the body, never a
log** (redacted from the factory's request-header logging too). **Fail-closed reuses gh#402's chain by
construction**: a non-2xx, a transport fault, a timeout, or an unparseable / wrong-shape body **throws**, which the
reviewer maps to `Suppress(ReviewerUnavailable)`; a `refusal` maps to a non-complete stop → suppress. Ships
**inert** — no live call in code/tests/CI; the operator activates it by adding their key to `.env`. Still open (now
the whole of "AI-spend"): the **`AIUsage`** per-call token+cost+latency record → Grafana, and the **platform-level
spend governor** — plus **model-tiering escalation policy** (both tiers resolve to real models; *when* triage
escalates to deep is a follow-up).

**Update (2026-07-28, gh#431) — the persisted `AIUsage` ledger landed; the spend signal's LLM half is durable.**
The agent-review reviewer now returns an `AgentReview` (its `ReviewOutcome` **plus** an `AiCallCost` — feature,
model / tier, tokens in/out, an estimated USD cost at the pinned per-tier rate, an `AiUsageOutcome`, and latency),
and the scan — **the single tenancy authority** — records one `AiUsageRecord` **stamped with the firing owner**
(R-20), with the trace id (ADR-0002) and the caller's clock. **Fail-open in depth:** the write runs in its own
owner-scoped context and a fault is logged + swallowed at **both** the ledger internals **and** the scan boundary
(guarded exactly like the reviewer and advisory-notify seams), so a spend-bookkeeping blip can never roll back a
committed fire or a co-owner's already-sent alert. A **provider fault is a real `Failed` zero-token row** (billable
latency the governor must see), not an absence. **Crucial caveat — the ledger is a FLOOR, not a complete
accounting:** nothing is recorded if the host dies between the call and the write, so the **spend governor treats it
as a floor** (gh#403, ADR-0002) — *superseded by the gh#448 update below*, which found the aggregate meter to be
export-only and settled that the governor enforces on the floor and reconciles operator-side in Grafana. Still open
(the last of "AI-spend"): the **platform-level spend governor** (cap / throttle vs.
budget) and the **triage→deep escalation policy**.

**Update (2026-07-29, gh#436) — embed-path AIUsage owner settled; the live write awaits gh#377.** The question
gh#431 deferred ("who owns a *global* embed's spend row under R-20?") is resolved: **global** embedding spend —
embedding deployment-global news / market snapshots (gh#377) — is **deployment infrastructure cost, not an operator
trading decision**, so it is stamped to a **deployment sentinel owner** (`SystemOwner.Id`, a well-known **non-empty**
Guid). Not the operator (that would inflate the per-decision spend meter, gh#62, and contradict "embeddings reported
in Grafana, not surfaced to end users"); not `Guid.Empty` (the data layer's "no user ⇒ read nothing" deny sentinel —
a real row there would be anonymously readable); not a nullable owner (that would weaken the single default-deny
filter for the safety-relevant LLM rows too). `AIUsage` stays strictly `IUserOwned` and needs **no seeded `User`
row** — an owned row's owner column is a query-filter scope, not a foreign key. The owner is stamped **by the
consumer** (per gh#431): a *global* embed → the sentinel; an *owner-scoped* embed (e.g. an operator's own chat query)
→ the operator. The **live write is deferred to the first embed consumer (gh#377)** — gh#431's "until an owner-scoped
embed consumer exists" condition is still unmet (nothing calls `EmbedAsync`), the metric half already gives
visibility (gh#403), and forcing a write now would be dead code plus a singleton→scoped captive dependency or a
fire-and-forget on the degrade-never-throw retrieval path. The mapping is pinned: `EmbeddingOutcome` → `AiUsageOutcome`
(Embedded → Succeeded, RateLimited → RateLimited, Failed → Failed), with `Feature = Embed`, `Tier = null`,
`OutputTokens = 0`. **Embed attribution is now closed**; the live embed write rides gh#377. Still open: the
**platform-level spend governor** and the **triage→deep escalation policy**.

**Update (2026-07-29, gh#448) — the platform-level AI-spend governor landed.** A **pure, deterministic**
`AiSpendGovernor` (Domain/Ai) mirrors the R-5 daily *risk* governor line-for-line — `budget − spent ≤ 0 ⇒ Block` —
and the trigger scan consults it **before** waking the agent-review reviewer: on a spent budget it short-circuits to
`Suppress(BudgetExhausted)` with **no LLM call and no `AIUsage` row** (it caps *whether* a call is made, not just
records it), telling the operator one "review paused — budget reached" advisory per arming edge, plus a once-per-day
threshold heads-up. The budget is **static, validated, opt-in `GovernorOptions`** ("Governor"; `DailyBudgetUsd`
absent ⇒ inert, the no-cap status quo), not a per-account entity — a platform cap has no per-account home (contrast
`RiskProfile`). The window is the **daily Central trading day** (via `MarketClock`, mirroring the risk governor +
auto-flatten — a UTC reset would split a live CME session). **Honest amendment to the gh#431 note above ("reconcile
against the aggregate meter, never hard-cap on the ledger alone"):** that is **impractical as written** — the
`MarqSpec.TradingCopilot.Ai` meter is `System.Diagnostics.Metrics` → OTel → Prometheus, **export-only, not readable
in-process**, and covers **embeddings only** today. So the governor enforces on the **`AIUsage` ledger floor** (a
platform-wide `IgnoreQueryFilters` window-sum across every owner + the `SystemOwner` embed rows — the only
app-queryable spend signal), and **reconciliation is operator-facing in Grafana**, not an in-app read; until an
LLM-side meter lands, that Grafana view shows embeddings only, so LLM-spend is reconciled by reading the ledger
directly *(the LLM-side meter **landed** in gh#477 below — that Grafana view now covers both halves)*. Because the floor can only *under*-count (a row is lost only if the host dies between the call and its
own-context write), the **effective cap is the budget plus at most one in-flight call's un-recorded spend** — it
fails toward *allow* (never a spurious block), so the operator sets `DailyBudgetUsd` with a little headroom.
**Fail-closed on the cap** (the point); **fail-open on an unavailable spend signal** (a read fault logs and runs the
pass un-gated) — the deliberate *inverse* of the fail-closed trade-safety gate, because this guards a soft-dollar
budget, not capital-at-risk, and must never be conflated with it. **Hard-cap only** for now (ADR-0008's "cap *or*
throttle" — throttle deferred). Still open: ~~an **LLM-side meter** so Grafana shows true LLM spend~~ *(landed, gh#477
— and as predicted it did **not** change the governor's read, which is still the ledger floor)*, **Prometheus-based
reconciliation** and a **runtime-editable** budget.

**Update (2026-07-30, gh#377) — the live embed producer + call-site gating landed.** `NewsEmbeddingService` (the
news-embedding backfill pass) is the first `EmbedAsync` consumer: it records the embed `AIUsage` rows the gh#436
owner was readied for — one per attempted call, success or failure, stamped to `SystemOwner`, `Feature = Embed` — and
**gates each embed on the same deployment-wide daily budget** the trigger scan reads, re-checked before every call so
a long pass stops mid-page once exhausted. This closes the *embed-call-site gating* the list above left open. It
**fails open** — a spend-read fault runs the pass un-gated — a soft-dollar budget, the deliberate inverse of the
fail-closed trade gate. To ledger honestly, `IEmbeddingProvider.EmbedAsync` now rides the call's spend facts back on
an `EmbeddingResult` (mirroring `LlmUsage` on a completion); gh#377 is its only consumer.

**Update (2026-07-30, gh#449) — the triage→deep escalation policy landed.** The triage tier may now return a third
`decision: "escalate"` — a **reviewer-private control signal**, never a public `ReviewOutcome` — when a fired setup
is genuinely too hard for a quick judgment. On escalate the reviewer makes ONE **second, deep-tier** call
(`claude-sonnet-5`) whose schema offers only `suggest`/`suppress` and whose prompt demands a **final** answer, and it
uses that outcome. It **cannot loop** (triple-defended: the deep schema omits `escalate`; the deep result is mapped by
the terminal path where a stray `escalate` reads as an unknown decision → suppress; and escalate is honoured only on
the triage path) — a review makes **at most two calls**. **Both calls ledger and both accrue to the governor tally:**
`AgentReview` now carries `IReadOnlyList<AiCallCost> Costs` (was a single nullable `Cost`), and an escalated fire
records **two `AIUsage` rows** — a Triage-tier row and a Deep-tier row, **both `Feature = Triage`** (the whole
two-call flow is one agent-review of one fired trigger; `Tier` is the dimension that separates triage spend from deep
spend), each priced at its own per-tier rate. The **escalated triage call is billed `Succeeded`** (it completed and
was billed even though it deferred); a deep throw / refusal / malformed output / second-escalate all fail closed to
`Suppress` but **both costs are still recorded** (a Failed deep row included) — no spend regime is uncounted. A
**missing or unknown decision never escalates** (fail-closed by construction). **Escalation is driven by the triage
signal alone;** the reviewer stays **pure of the governor** — cost-awareness is the *pass-level* gh#448 gate (a spent
budget short-circuits the whole review to `BudgetExhausted` before any call; and because both costs accrue to the
within-pass tally, a later fire this pass can be blocked). **Note — this purity is by design, not test-enforced:**
`AgentReviewGateBelowModelTests` forbids only *execution* types and explicitly *allows* `AiSpendGovernor`, so a future
"thread the budget into the reviewer" refactor would keep that gate test green while breaking the reviewer's purity;
reject it on principle (if a per-call budget-aware skip is ever wanted, the *scan* — which holds the tally — passes a
plain `bool allowEscalate`, keeping the budget out of the reviewer). This **amends the gh#448 effective-cap note
above:** the cap is now "budget plus at most one in-flight triage→deep **pair**," and the deep call is the expensive
one ($3/$15 vs. $1/$5 per 1M tokens) — size `DailyBudgetUsd` with headroom for at least one pair; still a bounded
loosening that fails toward *allow*, never a spurious block. **Scope caveat:** escalation upgrades the **model**, not
the **information** — the deep call reuses the same `TriggerReviewContext`, so **deep-context enrichment is a deferred
follow-up** *(landed gh#476, below)* (it also widens the prompt-injection surface), and until it lands the deep tier may
re-derive the triage answer at higher cost; the prompt says escalate *sparingly* and an escalation is logged for
operability. Still open: ~~per-call budget-aware escalation skip~~ *(landed, gh#478 — below)*, an escalation-rate metric
+ a when-to-escalate eval suite, activating `AiUsageFeature.Suggestion` for the deep row, ~~an LLM-side meter~~
*(landed, gh#477)*, a
runtime-editable budget, and throttle.

**Update (2026-07-29, gh#470) — confirm-before-live: the LLM is out of the hot loop of the _rule_, not only the firing.**
"The LLM is never in the continuous hot loop" was already true of *firing* (the deterministic scan fires; the reviewer
only proposes). It was **not** true of *authorship*: a trigger — including one an agent-review proposal or a future R-7
compiler writes — became armed the instant it was persisted with `Enabled = true`, so a machine-authored rule could
begin paging the operator or waking the reviewer with **no human step between authorship and armed**. This adds that
step. A new **`TriggerConfirmation`** {`Unconfirmed = 0` → `Confirmed = 1`} on `TriggerRecord` is **distinct from
`Enabled`** (live/paused): an **unconfirmed trigger is inert regardless of `Enabled`**, and both scan query sites
(owner discovery + per-owner load) evaluate only `Confirmed` triggers. `CreateTrigger` persists **`Unconfirmed`**;
acceptance is the separate, deliberate **`POST /api/triggers/{id}/confirm`** (idempotent, R-20-scoped, leaves the
debounce untouched so a confirmed trigger still seeds silently and fires only on an observed edge). The zero is
`Unconfirmed` on purpose — a defaulted/corrupt row is inert (fail-closed), the same zero-is-the-safe-default posture as
`TriggerArmState.Unseeded` and `DefaultEntryAction.ApproveAndArm`, and mirroring the execution path's arm → review →
send (R-11, ADR-0007). Enforcement stays **below the model**: this is a DB column + a scan predicate + a `CHECK`
(`"Confirmation" IN (0, 1)`), never prompt text. The migration backfills **existing** rows to `Confirmed` — a schema
change must not silently disarm an alert already in service (the gh#380 lesson; here the safe value is *not* the zero,
so it is an explicit `UPDATE`, not a `defaultValue`). This is the gate that makes gh#15's *"plain rules → **confirmed**
deterministic triggers"* true of the rule.

**Update (2026-07-30, gh#476) — deep-context enrichment landed; the deep tier now gets more than the model upgrade.**
This closes the gh#449 scope caveat above ("escalation upgrades the *model*, not the *information*"). On escalation the
deep call now carries a **numeric market-context payload** — a bounded window of recent **OHLCV bars** and recent
values of the **fired indicator's own series** — so the deep tier can reason about price and the setup's recent
trajectory, which the base `TriggerReviewContext` (the fired reading + its threshold, **no price**) cannot supply. Key
decisions, each keeping the ADR's principles intact:
- **Assembled in the deterministic scan, not the reviewer.** A new **pure `IReviewEnrichmentSource`** seam
  (`ReviewEnrichmentSource`, an EF read over the **global** `Bars` / `IndicatorValues` projections — R-20 shared-side
  market data, read **venue-agnostically** over the covering index, mirroring `StoredIndicatorSource`) reads **as of
  the fire** (`BucketStart <= FiredAt` — the same bar-close-aligned cutoff the fired reading itself came through, so
  the enrichment is consistent with the decision; strictly no-look-ahead on a live fire, and on a replay the boundary
  bucket reflects its completed bar exactly as the fired value does), oldest-first, and the scan attaches the result
  to the context before waking the reviewer. Keeping it on the scan
  **preserves reviewer purity** (enforcement-below-the-model): the reviewer never gains a data-access dependency. This
  also **answers the gh#449 "purity is by design, not test-enforced" note** for this coupling — a new **constructor-
  pinning test** now asserts the reviewer's ctor is exactly `{ILlmProvider, IOptions<LlmOptions>, ILogger}`, so wiring
  the enricher (or a `DbContext`) into the reviewer fails the build.
- **Deep-only, and additive by a trailing-optional field.** Enrichment rides a **trailing optional** `Enrichment` on
  `TriggerReviewContext` (null for triage and the un-enriched path), so the **triage render stays byte-for-byte
  unchanged** — only the escalated deep call sees the extra block, and the ~56 existing construction sites keep
  compiling. It is rendered **deep-only** inside a `<market-context>` **DATA fence**, with a deep-system-prompt sentence
  telling the model to treat the block as reference data, **never instructions**.
- **Near-zero-injection invariant preserved.** The payload is **numeric only** (decimals, a volume `long`, timestamps —
  no free text); **news remains deferred** as the free-text injection surface. The fence + system-prompt caveat are
  **forward-hygiene** for when it lands.
- **Bounded by a constant, not config** (20 bars / 20 indicator values) — a predictable deep-prompt input size and the
  added spend **capped by construction**. Cost flows through the existing `AiCallCost` / governor path (deep input
  tokens rise, bounded); no new spend regime.
- **Fail-open assembly.** A read fault leaves the context **un-enriched** (the deep call, if it happens, uses the base
  render) — enrichment adds context, it must **never cost a fire**; a **budget-blocked** fire skips enrichment entirely
  (no wasted read before a call that will not happen).
- **No schema / migration / `.env` / compose change** — it reads existing projections. Purely a read + render + wiring
  change. Still open (unchanged from gh#449, minus this item): ~~per-call budget-aware escalation skip~~ *(landed,
  gh#478 — below)*, an escalation-rate metric + when-to-escalate eval suite, `AiUsageFeature.Suggestion` for the deep
  row, a runtime-editable budget, throttle, and — the injection surface this defers — **news/free-text enrichment**.

**Update (2026-07-30, gh#478) — the per-call budget-aware escalation skip landed; the pair-overrun is closed.**
The gh#448 governor caps **whether the review runs at all** (pass-level, before any call), and the gh#449 update
amended the effective cap to *"budget plus at most one in-flight triage→deep **pair**"* — because a triage that fit the
remaining budget could still escalate into a deep call that overran it. That **partial-budget** case is now handled: the
**scan** — which already holds the `GovernorPass` tally and the budget — decides affordability
(`spent + estimatedDeepCost <= budget`) and passes the reviewer a **plain `bool allowEscalate`**. When triage defers but
the bit is false, the **deep call is skipped**: only the triage cost is billed, and the review returns the new
`SuppressReason.EscalationDeclined`.

**The reviewer stays pure of the governor** (the gh#449 constraint, which that update warned was *by design, not
test-enforced*). It receives a **permission bit, never a budget**, and it references no governor or budget type at all —
the affordability arithmetic lives entirely in the scan. What the reviewer *does* own is the **cost of its own deep
call**: it exposes `EstimatedDeepCallCostUsd`, a deliberately **conservative** estimate (a high input-token count at the
deep rate, paired with `MaxOutputTokens`), because the token shape and rates belong where the call is made.
Overestimating is the **safe direction** for an affordability gate — it declines an escalation slightly early rather
than letting one overrun.

**Fail-open and honest-inert, consistent with the rest.** An **inert** governor (no budget configured) or a **fail-open**
spend read leaves `allowEscalate` true, so behaviour is byte-for-byte pre-gh#478. A declined escalation is **not
silent**: the scan — which knows the reason is *budget*, since the reviewer only got a neutral bit — raises one
operator advisory per arming edge (*"a quick review flagged it for deeper analysis, but the daily AI-spend budget could
not afford the deeper look"*), the same honest-inert posture as `NoReviewerConfigured` / `ReviewerUnavailable` /
`BudgetExhausted`. **This amends the gh#449 effective-cap note:** the cap is again **the budget**, not budget-plus-a-pair
— an escalation is only made when the deep call's conservative estimate still fits. Still open from that list: an
escalation-rate metric + when-to-escalate eval suite, `AiUsageFeature.Suggestion` for the deep row, a
runtime-editable budget, throttle, and news/free-text enrichment. *(The LLM-side meter on that list landed separately as
gh#477, below — with it, Grafana now sees the deep-call spend this gate withholds.)*

**Update (2026-07-30, gh#477) — the LLM-side meter landed; Grafana now sees true total AI spend.** The embed meter
(gh#403) had no LLM counterpart, so Grafana showed embedding spend but **not** LLM spend — the gh#448 "reconcile in
Grafana" note was partly vapor for the LLM path. Now a **`LlmMetrics`** (Api/Ai) emits `ai.llm.{calls, input_tokens,
output_tokens, cost_usd, latency}` on the **same `MarqSpec.TradingCopilot.Ai` meter** as embeddings (so it exports with
**no exporter change**), dimensioned by **model + tier + outcome** — all low-cardinality; the reviewed text is never a
tag. It is fed the **same `AiCallCost`** the ledger gets, recorded in the scan's cost loop **alongside**
`IAiUsageLedger.RecordAsync` (metered **first**, outside the ledger's fail-open try, since the two are independent
sinks), so an **escalated fire meters two** calls (triage + deep) and a **`Failed` call is metered** at zero tokens (a
degrade is visible, not an absence). A **required** dependency (the gh#403 posture — an unmetered call is invisible
spend). **Crucially this is observability, not enforcement** (the gh#448 finding): a `System.Diagnostics.Metrics` meter
is export-only / not app-readable, so the governor's read is **unchanged** — it still enforces on the persisted
`AIUsage` ledger floor; this closes the **Grafana-visibility** gap only. Amends the gh#448/gh#449 "still open:
LLM-side meter" notes above — **landed**.

- Define the **trigger / condition model** (DSL or structured schema) and how R-7 rules compile to it; unit-test the
  compiler. *(gh#385: the structured schema + the first condition kind shipped; the R-7 compiler is still open.)*
- Spec the **deterministic trigger evaluator** as a processor / consumer over the event log (ADR-0001) — inputs
  (indicators / order-flow / price / state), outputs (fire → alert or agent-review). *(Landed gh#385; `TriggerScanHost`
  / `TriggerEvaluationService`, plus the confirm-before-live gate gh#470.)*
- Design the **agent-review path** (strategy agents → executor) invoked on fire; define its contract and output
  (suggestion + rationale, or suppress). *(Landed gh#402; the reviewer wakes behind `ILlmProvider`, stages a `Suggestion`
  or suppresses — see the dated update above.)*
- Define the **model-tiering + escalation** policy and the **debounce / rate-limit** parameters. *(Landed: triage→deep
  escalation gh#449, the real Anthropic provider gh#423; the arm-edge debounce is the `TriggerArmState` machine. A
  wall-clock rate-limit is still open.)*
- Decide the **AI-spend governor** (budget; throttle vs. cap) — mirror R-5's governor (`Q-10`). *(Landed gh#448 as a cap
  on the `AIUsage` ledger floor — see the dated update above; a runtime-editable budget + throttle remain open.)*
- **Observability + evals:** per-invocation cost / latency / token traces (ADR-0002); a **deterministic-eval** suite
  for trigger correctness and triage quality (engineering §5). *(Traces + the `AIUsage` cost/latency ledger landed
  gh#431; the deterministic-eval suite is **still open**.)*
