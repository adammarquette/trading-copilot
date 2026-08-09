# §7. Journal & outcomes

Part of the [data dictionary](../data-dictionary.md) — see it for the ERD, the **Conventions** that
govern every domain, and the routing table. Cited elsewhere as **data dictionary §7**.

> **Storage codes:** `REL` relational · `TS` time-series (Timescale hypertable) · `VEC` vector.
> **Traces** cites the requirement (`R-#`), ADR, or `gh#N` the row answers to.

| Entity | Key fields | Storage | Traces |
|---|---|---|---|
| **Trade** | originating suggestion, instrument, side, entry / exit, size, realized P&L, R multiple, strategy, fills, mode, annotations. **Implemented — journal spine** (gh#7): account FK + suggestion FK (null on deletion), instrument, side/size, entry/exit prices, realized P&L, mode (≠ `undeclared` CHECK — practice results never blend into live results — but **not** trigger-guarded: a trade closes after placement, when the declaration may have moved on; the placement-time guard lives on Order/Suggestion), closedAt. **Written in production** (gh#731): the `AccountEventStreamHost` flat `PositionEvent` reconstructs the round trip from its `Fill`s and writes one row with size-weighted entry/exit, a signed tick-value-aware realized P&L (`InstrumentSpec.RealizedPnL`), mode and suggestion FK **copied from the entry `Order`** (placement-time truth). `closingFillId` — FK to the closing `Fill`, **unique (filtered)** — is the natural key that makes the writer idempotent: a replayed flat event cannot double-count the day into the daily governor. Indexed `{accountId, closedAt}` for the two live readers (`DailyRealizedReader`, `ConsistencyWindowReader`). **Scope:** the balanced single enter→exit→flat round trip; scale-in with partial exit and stop-and-reverse are refused (no row) pending a pairing policy. **Deferred:** R multiple, strategy, per-fill composition, annotations (R-8/R-9) | REL | R-8, R-9 |
| **TradeFeedback** | trade, comment, tags, emotional state, **awaiting-review** flag, author (operator / AI), ts | REL | R-8, R-6 |
| **Outcome** | suggestion / trade, resolution (win / loss / **no-fill-scratch** / expired), simulated?, calibration (predicted vs realized), **`training_excluded`**, **`hidden_from_user`**, `deleted` — the three independent R-15 flags, defined once under *Conventions* above | REL | R-9, R-15 |

