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
  observable.
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
seed pre-existing truth silently, and hold fail-closed on a null indicator), and a **periodic evaluator** that fires
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

- Define the **trigger / condition model** (DSL or structured schema) and how R-7 rules compile to it; unit-test the
  compiler. *(gh#385: the structured schema + the first condition kind shipped; the R-7 compiler is still open.)*
- Spec the **deterministic trigger evaluator** as a processor / consumer over the event log (ADR-0001) — inputs
  (indicators / order-flow / price / state), outputs (fire → alert or agent-review).
- Design the **agent-review path** (strategy agents → executor) invoked on fire; define its contract and output
  (suggestion + rationale, or suppress).
- Define the **model-tiering + escalation** policy and the **debounce / rate-limit** parameters.
- Decide the **AI-spend governor** (budget; throttle vs. cap) — mirror R-5's governor (`Q-10`).
- **Observability + evals:** per-invocation cost / latency / token traces (ADR-0002); a **deterministic-eval** suite
  for trigger correctness and triage quality (engineering §5).
