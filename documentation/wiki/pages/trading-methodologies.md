# Trading methodologies → strategy templates (reference)

> **Trust tier:** design-time reference. **Status:** structure + a to-ingest list — the **specifics of each
> methodology are ingested from a source** (a course, a book, the operator's notes), **not invented here**.
> **Access:** no external source ingested yet — the concept vocabulary below is common domain knowledge. When the
> 13/48 course or an ICT model is ingested, record that source's terms here **first**: paid course material —
> check its ToS before publishing derived rules in this public repo.
> **Informs:** R-21 (strategy templates), R-4 / R-7 (suggestions, rulebook), R-5 (risk), R-9 (per-strategy analytics).

How a named trading methodology becomes a **StrategyTemplate** (R-21). Every methodology decomposes into the same
**five layers** our pipeline already has — so encoding one is *filling in these slots*, not new plumbing:

| Layer | What the methodology specifies | Maps to |
|---|---|---|
| **Data / features** | the indicators / structures it reads | R-1 / R-3 / R-22 (some need **custom detectors**) |
| **Setups** | the machine-checkable conditions for a valid setup | R-7 → triggers (ADR-0008) |
| **Suggestion shape** | how a setup becomes entry / stop / target / size | R-4 |
| **Risk defaults** | per-trade %, target R:R, session / kill-zone limits | R-5 |
| **Rules** | plain-language do's / don'ts (rationale + suppression) | R-7 |

A user **installs** a template and **personalizes** it (per-user, R-20); outcomes feed **per-strategy analytics**
(R-9) and the **AI refinement loop** (R-6 / ADR-0009).

## To ingest (first templates)
- **13 / 48 EMA crossover** — a moving-average-crossover system (13 & 48 EMAs, likely with a trend / VWAP filter).
  *The operator has a course on it — **ingest the exact entry / stop / target / filter rules from that source**
  before encoding the template.* Confirm: EMA vs SMA; the confirmation condition; stop placement; the target rule.
- **ICT (Inner Circle Trader / smart-money concepts)** — a popular structure-based methodology. The **concept
  vocabulary** a template would encode: **kill zones** (session timing), **market-structure shift** (BOS / CHoCH /
  MSS), **fair-value gaps (FVG)**, **order blocks**, **buy / sell-side liquidity** + sweeps, **displacement**,
  **optimal trade entry (OTE)**. These are **non-trivial custom feature detectors**, not standard indicators
  (R-21) — a real build cost. *Ingest a specific model (e.g. "Silver Bullet", the "2022 model") from a grounded
  source before encoding; the vocabulary above is widely known, but the **exact entry rules must come from a
  source, not memory**.*

## Relevant-link index
- *(to add: a source for 13/48 — the operator's course; a grounded ICT model reference.)*
