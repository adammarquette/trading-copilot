# ADR-0022: Trade round-trip pairing — FIFO, per-leg, split-on-span

**Status:** Accepted · **Date:** 2026-08-12 · **Deciders:** Adam (operator, ruling 2026-08-10)
**Relates to:** gh#759 (this decision), gh#731 (the `Trade` writer and the source of the deferral, PR #734); PRD
`R-8`/`R-9` (the journal), `R-4`/`R-5` (the daily-loss and consistency governors the trades feed);
[ADR-0007](0007-order-execution-model.md) (the one enforcing gate + daily governor),
[ADR-0013](0013-failure-recovery-model.md) (idempotent replay). Data dictionary:
[`07-journal-outcomes.md`](../data-dictionary/07-journal-outcomes.md).

## Context
gh#731 shipped the production `Trade` writer and **deliberately scoped it to the balanced single
enter → exit → flat round trip**. Two shapes were **refused** — no `Trade` row written at all:

- **scale-in with a partial exit** (a leg built or unwound in several fills), and
- **stop-and-reverse** (flat → re-open opposite).

That refusal was correct for gh#731 — `TradeRoundTrip` refuses rather than guesses, because a wrong
`RealizedPnL` fed to the governor is worse than none. But it is **safety-critical, not a reporting gap**: a
refused round trip writes no `Trade`, and `Trade` rows are exactly what the R-4/R-5 enforcement path reads
(`DailyRealizedReader`, `ConsistencyWindowReader`). So a scaled-in or reversed trade's realized **loss never
reaches the daily-loss governor** — it under-counts the day and the operator keeps headroom they have actually
spent (the same hazard class as gh#748). The refusal is instrumented, so it is *detectable*, not silent; this
decision makes it **rare and correct** instead. The pairing policy gh#731 deferred is decided here.

## Decision
The pairing relation (Adam, 2026-08-10): **one opening fill → many closing fills; one closing fill → exactly one
opening fill.**

- **Per-leg trades.** A `Trade` is **one opening fill** plus the closing fills that retire it. An entry of 10
  exited 5 + 5 is **one** `Trade` — the exits are partial retirements of a single leg, not separate trips. The
  entry price is that opening fill's own price (exact); the exit price is the size-weighted average of the
  retiring allocations.
- **FIFO.** A closing fill retires the **oldest open leg first** — what broker statements and IRS lot accounting
  show, so the journal reconciles against the operator's own ProjectX statements without translation.
- **Not average-cost.** Blending scaled-in legs into one entry price would make a closing fill answer to several
  opening fills, which the ruling forbids.
- **Split a spanning closing fill, don't refuse it.** ProjectX can emit a single 10-lot exit against openings of
  5 and 5. Allocate it 5 against each leg under FIFO, producing **two** `Trade` rows. Refusing was the
  alternative and was **rejected**: a refused row means a real filled trade's realized P&L never reaches the
  daily governor — the exact hazard this decision closes.
- **Stop-and-reverse needs no special case.** Exposure returns to flat between the closing fills that retire the
  old legs and the opening fills that establish the opposite ones, so a reversal is just the next opening fill —
  a single venue fill can be both the **closing** fill of the retired legs and the **opening** fill of the new one.
- **Refuse-don't-guess is kept only for genuine ambiguity:** a fill whose side is neither leg; an
  **open-from-flat whose direction is not decidable** (opposite-side fills tied at the boundary timestamp, with no
  venue sequence to order them — guessing would flip the sign of the realized P&L); or a window that does **not**
  reconcile to flat. These still write no row.

### The structural cost — a new natural key
`ClosingFillId` is today a **unique filtered** index (`IX_Trades_ClosingFillId`) and the writer's idempotency key
(gh#731). Once one closing fill can be allocated across two legs, **`ClosingFillId` alone is no longer unique**.
The natural key becomes **`(ClosingFillId, OpeningFillId)`** — where `ClosingFillId` is the leg's **final**
retiring fill — and that **requires an EF migration** replacing the single-column index, adding an `OpeningFillId`
column. **Idempotent replay must be preserved** across the change: replaying the same flat event must write
nothing new — it is the property that stops a re-delivered flat double-counting the day into the governor.

## Consequences
- **Multiple `Trade` rows per flat cycle** (one per opening leg) instead of one-or-none. The composer
  (`TradeRoundTrip`) returns a list; the writer loops, deduping each leg on the composite key.
- **The governor sees the full day, invariant _within_ a Central day.** `RealizedPnL` is linear in size and in
  `(exit − entry)`, so the per-leg rows **sum to the cycle's total**; both readers already **SUM** `RealizedPnL`, so
  a cycle that opens and closes on **one** Central day is fully invariant to the split. A cycle that **straddles** the
  Central-day boundary now attributes each leg to the day it **actually** closed (per-leg `ClosedAt`) rather than one
  `max(ClosedAt)` day — the honest per-day measure, but it does shift `DailyRealized`'s per-day attribution and
  `ConsistencyWindow`'s daily-max there. That sits inside the day-boundary zone **gh#11** owns (reader and enforcing
  gate ~0 there today; the flat-before-CME-close workflow never straddles) — a straddle test belongs with gh#11.
- **Idempotent replay holds under a stable or append-only fill set, not a re-paired one.** The key
  `(ClosingFillId, OpeningFillId)` is stable only while the fills are; a late, **out-of-order** fill landing before an
  already-journalled close makes FIFO re-pair the same fills into different-keyed legs the per-leg dedup cannot
  recognise as the old row. A **pre-write guard** fails closed — a window fill already journalled under a *different*
  pairing refuses the flat rather than double-count into the governor (the safe, under-reporting direction).
  Reconciling the orphaned row is a settlement-reconcile concern (gh#193). A **pre-#759 row** carries only the old
  single-column closing key (null `OpeningFillId`); the guard and the per-leg pre-check recognise it by `ClosingFillId`
  **alone** as the already-journalled version of the same trip, so replaying a legacy flat is the ordinary idempotent
  skip — **not** a false re-pairing that would pollute `JournalBoundaryMergeRefused` (pre-#759 windows were only ever
  balanced single trips, so a legacy `ClosingFillId` belongs to exactly one recomposed leg).
- **The idempotency backstop moves to the composite key:** the pre-check `AnyAsync`, the narrowed
  `DbUpdateException` catch, the pinned index-name constant, and the metadata test that pins it all follow the
  index rename.
- **The composite index is filtered to both keys non-null, so legacy null-`OpeningFillId` rows lose DB-level
  uniqueness — deliberately, and safely.** `IX_Trades_ClosingFillId_OpeningFillId` is
  `WHERE "ClosingFillId" IS NOT NULL AND "OpeningFillId" IS NOT NULL`, so two rows that share a `ClosingFillId`
  but both carry a null `OpeningFillId` (every pre-#759 legacy row) are **not** caught at the DB level as the
  dropped single-column `IX_Trades_ClosingFillId` caught them unconditionally. This narrowing is safe on both
  fronts: no post-#759 code path writes a null-`OpeningFillId` row (`fe30ff8` populates the opening key on every
  new leg), so no *new* null-keyed duplicate can arise; and the pre-existing legacy rows were already unique
  under the old index at migration time, so no duplicate can arise *among* them either. A new write whose
  `ClosingFillId` matches a legacy row is still caught app-side by the `ClosingFillId`-alone pre-check above. The
  two real-Postgres write-fault tests that exercised the old single-column collision were re-pointed at the
  composite invariant to match.
- **`Down` may legitimately fail on real data** (composite → single can collide), documented in the migration, as
  the outbox-dedup narrowing already is.
- **Real-Postgres coverage** — the composite-key uniqueness, idempotent replay, and the governor equality across
  the split — is a **QA-tier follow-up** (the gh#751 pattern), since a DB CHECK/UNIQUE is not exercised by the
  in-memory unit gate.
