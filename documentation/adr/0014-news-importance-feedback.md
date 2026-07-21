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
