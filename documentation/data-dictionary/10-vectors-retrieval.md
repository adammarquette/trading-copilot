# §10. Vectors & retrieval

Part of the [data dictionary](../data-dictionary.md) — see it for the ERD, the **Conventions** that
govern every domain, and the routing table. Cited elsewhere as **data dictionary §10**.

> **Storage codes:** `REL` relational · `TS` time-series (Timescale hypertable) · `VEC` vector.
> **Traces** cites the requirement (`R-#`), ADR, or `gh#N` the row answers to.

| Entity | Key fields | Storage | Traces |
|---|---|---|---|
| **Embedding** | owner type + id (suggestion / rule / soft-signal / snapshot), vector, model (Cohere), created ts **Implemented** (gh#109, migration `AddEmbeddingStore`) as `EmbeddingRecord` / table `Embeddings`. Keyed on **owner kind + owner id + model** — model is IN the key because vectors from different models are not comparable, and the key doubles as the idempotence guard so a re-embed **updates** rather than appends (embedding is a paid call; re-ingestion is normal). Carries `Dimensions` and a `ContentHash` so an unchanged owner is recognised and the paid re-embed skipped. `vector(1024)` (Cohere embed-v3), **HNSW cosine** index. **Global / not `IUserOwned`** (R-20), following its owners. **Relational-only in the EF model** — `Vector` has no in-memory mapping, so coverage is integration-tier. **Skipped entirely where pgvector is unavailable** (gh#109 degrade: the app still starts, trading is unaffected, and the absence is declared rather than silent) — and since gh#474 the provider actually reports unavailable, so no embed is paid for a vector with nowhere to go. Providers: the keyless default ships here, Cohere in gh#403; first consumer gh#377 | VEC | eng §2, R-4, R-8, R-20 |

