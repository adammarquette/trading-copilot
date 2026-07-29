# ADR-0014: News importance feedback & personalized weighting

**Status:** Accepted · **Date:** 2026-07-19 · **Deciders:** Adam (operator)
**Relates to:** PRD `R-2` (soft-signal ingestion + relevance), `R-6` (co-pilot / personalization), `R-9` (learning
loop); data dictionary (`SoftSignalFeedback`, `RelevanceConfig`); [ADR-0008](0008-ai-invocation-cost-model.md)
(retrieval / embeddings), [ADR-0007](0007-order-execution-model.md) (enforcement below the model),
[ADR-0011](0011-multi-user-tenancy.md) (per-user).

## Context
News volume is high and **not every item matters equally to a given operator**. R-2 already defers *sentiment* (a
👍/👎 **direction** rating) and provides a topic / relevance map; neither captures **importance** — "this is the
kind of thing I care about; weight items like it more heavily." Importance is a **distinct axis** from sentiment
(direction) and from topic-mapping (attachment): a per-user **salience** signal that should shape how much a
future, *similar* item influences the co-pilot. In a **multi-user** system (R-20) this must be **per user** — one
operator's priorities are not another's.

## Decision
- **Star = importance.** The user can **star** a news / soft-signal item as important. A star is a per-user
  **`SoftSignalFeedback`** record — the same entity that carries the deferred 👍/👎 **sentiment** and a **mute**
  inverse (so one feedback model spans all three).
- **Stars reweight *similar future* items.** A star raises the **weight / salience** of later items similar by
  **matched instrument / topic**, **source**, **named entity**, and **semantic similarity** (embedding neighborhood
  — pgvector + Cohere rerank, ADR-0008). The effect is a **personalized multiplier** on the item's base relevance,
  aggregated into per-user weights on **`RelevanceConfig`**.
- **It is a soft salience weight — never a risk control.** Importance changes *what the model attends to and how
  prominently an item surfaces*; it **cannot** move a risk limit, position size, or gate decision. Enforcement stays
  **below the model** (ADR-0007) — consistent with "the LLM only proposes."
- **Transparent and adjustable.** The user can **un-star**, and **mute** (the down-weight inverse); weighting is
  **explainable** ("weighted up because you starred similar FOMC items"), not a hidden black box.
- **A signal, not a rule.** With no stars, relevance is the R-2 base (topic-map + semantic match); stars only
  **adjust** it. Starring never **hard-filters** news in or out — a **salience floor** keeps material but unstarred
  items visible (no filter bubble).

## Alternatives considered
- **Only thumbs-up/down (sentiment).** Already planned, but conflates *direction* with *importance* — a bearish
  item can still be very important. Insufficient; importance is its own axis.
- **A hard include/exclude flag.** Simpler, but brittle and un-personalized — the user wants *weighting*, not binary
  filtering, and a hard filter risks hiding a material item. Rejected.
- **Let the LLM infer importance from history each call.** Non-deterministic, costly, and opaque; a stored per-user
  weight is cheaper, explainable, and composes with the existing retrieval / rerank path.

## Consequences
**Positive** — the co-pilot's news salience **personalizes** to the operator; reuses the existing embeddings /
rerank infrastructure; one `SoftSignalFeedback` entity unifies star / sentiment / mute; the soft-weight boundary
keeps it safely away from the risk gate.
**Negative / costs** — a feedback → weight **aggregation** to build and tune; must stay **explainable** and avoid a
**filter bubble** (the salience floor); **cold-start** has no personalization; weights are per-user data to store
and scope (R-20).

## Follow-ups
- Define the **aggregation**: how stars map to per-dimension weights (instrument / topic / source / entity /
  embedding), the **decay**, and the **salience floor** that prevents a filter bubble.
- Decide where the multiplier applies: **surfacing order**, **rerank score**, and/or the **context budget** the
  co-pilot spends on an item.
- Surface the **"why weighted"** explanation in the news UI (R-2 panel).

## Follow-up resolutions (gh#27)
The open follow-ups above are settled by the gh#27 implementation:
- **Aggregation.** A star/mute contributes to a weight per **(dimension, value)** along the three dimensions that exist today — **matched instrument, matched topic, source**. Each contribution is scaled by an **exponential recency decay** (half-life, default 14 days), so recent feedback dominates and old feedback fades. An item's multiplier is `1 + Σ(matched-dimension weights)`, **clamped** to a floor and cap. The **named-entity** and **semantic-embedding** dimensions (gh#377) **degrade off** — absent, they contribute nothing rather than failing.
- **Where it applies.** **Surfacing order**, via a new personalized read `GET /api/news` (the sole consumer). Rerank-score and context-budget application arrive with those consumers (the retrieval path gh#103; the co-pilot R-6). The weight is **computed on read** from the operator's stored `SoftSignalFeedback` — no materialized per-user weight table, so it is always fresh; a cache is a later optimization. The feed re-ranks a **recency-bounded window** (the most recent N items), so salience reorders *within* recent news; surfacing a heavily-starred but older item **regardless of age** would require folding salience into the candidate selection (or unioning feedback-matched items in) — a later refinement, noted so it is a deliberate v1 bound, not an oversight.
- **Salience floor.** A **multiplier floor > 0** (default 0.25×): a mute down-weights but can never drive an item to zero, and an unstarred item stays at its base — nothing is hard-filtered (no bubble).
- **Base relevance (v1).** Deliberately coarse — a topic-map-matched item outranks an unmatched one, which still surfaces at a lower base. A richer base (match-count / recency blend) is a later refinement.
- **Sentiment** stays **deferred** (R-2): the `SoftSignalKind` enum reserves room, but only **star + mute** ship now.
- **Safety.** The weight is structurally unreachable from the risk gate (ADR-0007): a unit guard asserts no `Domain.Risk` type references `Domain.Signals`, and the paired QA suite (gh#362) asserts a maximally-starred/muted signal leaves a gate outcome **byte-identical** to baseline.
