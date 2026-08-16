# ADR-0026 — Confluence is assembled at scan time, from detected levels, on repeated-signal alignment

**Status:** Accepted (gh#728, of gh#593, epic #17 [A3], R-4)

## Context

R-4 wants a suggestion to cite **why** it fired, and gh#593 wants that "why" to be more than one factor: the same
read agreeing across timeframes, or an entry sitting where a price level already mattered. gh#593 sat in Planning
because three questions had to be settled before either build sub-issue — the multi-cited-factor model, and the
confluence assembly — was buildable at all. Each has a cheap answer and an expensive one, and picking the expensive
one by accident is the failure this ADR exists to prevent.

The operator was asked (2026-08-16) and accepted all three proposals as put.

## Decision

### 1. Levels come from the existing detector, not a second source

Confluence consumes the levels gh#595 already detects — swing pivots, ATR zones, session boundaries, round numbers
— each of which already carries its own timeframe and significance. **No parallel level source is built for this.**

Operator-drawn levels are a later layer, when the R-10 chart (gh#24) exists to draw them on. They are not a
prerequisite: a drawn level enters the same input, it does not need a different model.

*Consequence, stated because it orders the work:* confluence's level input **is** gh#595's output shape, so that
contract must freeze before the **assembly** sub-issue starts. The **model** sub-issue does not wait — it stores a
level *reference*, not a detector.

### 2. Confluence is assembled at scan time, after a signal fires — not inside the trigger

Triggers stay **single-condition**, exactly as they are today. When one fires, the scan corroborates it against the
other signals and levels current at that instant: the fired signal is the **primary**, the corroborators are
**supporting**.

The alternative — a trigger that itself demands several conditions before firing — was rejected. It rewrites a
working part, and it changes *when a suggestion exists* rather than only *how it is described*. Scan-time assembly
also leaves the single-signal path (gh#592) untouched as the **degenerate case**: one primary, no corroborators, is
a confluence set of one rather than a separate code path.

### 3. Alignment is a repeated signal across timeframes, or an entry near a ranked level

**Indicators.** The *same* signal — same `ConditionKind`, same direction — firing on **two or more timeframes**
within a lookback window. The **smallest timeframe is the headline** (gh#592's `min` rule); the larger ones are
supporting.

**Levels.** The setup's entry sits within a proximity band of a level, and the band is
**`min(k ticks, f × ATR)`** with `k` and `f` configurable — so it scales with the instrument's volatility rather
than meaning something different on every contract. A level is ranked by **its own** timeframe and significance: a
daily level outranks a 5-minute one.

Requiring two *different* indicator kinds was considered and rejected for now: it corroborates more strongly per
suggestion but fires far less often, and it discards the multi-timeframe evidence the current model already
produces. Nothing here forecloses it — it is a stricter alignment rule over the same assembled set, so it can be
added later without re-opening this decision.

## Consequences

- gh#593's two build sub-issues are unblocked; the **assembly** one additionally waits on gh#595's output contract.
- The suggestion model gains a **cited-factor set** with one primary and zero-or-more supporting factors. Zero
  supporting factors is normal and is what every suggestion looks like today.
- `k` and `f` are tuning knobs, so they belong in options rather than as constants — the same lesson gh#836 drew
  when the news fuzzy thresholds needed a redeploy to change.
- The proximity band and the lookback window are both **judgement calls that will want tuning against real feeds**.
  They are the first thing to revisit if confluence either never fires or fires on everything.
- This ADR settles the *shape*. It does not decide how a confluence set is **presented** to the operator (R-4's
  rationale text) or **scored** — those follow the model sub-issue, and neither is constrained by anything above.

## Notes

gh#728 says "this becomes ADR-0021". **0021 was already taken** (the realtime hub contract) by the time the
decision was made; the ADR numbers had reached 0025, so this is **0026**. Recorded because the card's own text
still points at the wrong number.
