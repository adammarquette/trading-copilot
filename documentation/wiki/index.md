# Wiki — index (front door)

Design-time **domain-knowledge base** for the trading co-pilot: vendor-API references, trading methodologies,
order-flow theory, instrument specifics — the reasoning behind the product requirements, kept in one maintained
place. **Not read by the product.** Conventions + trust tiers: [`SCHEMA.md`](SCHEMA.md). History:
[`log.md`](log.md). Read this file first at query time.

> **On third-party material.** These pages are **original summaries** — endpoint names, parameters, limits,
> rules, session times — written in our own words from publicly documented sources, each cited by URL and, where
> a claim is load-bearing, marked to confirm against the vendor. Facts and functional API details are summarised,
> **not reproduced**: no page carries substantial verbatim text, and no vendor document is redistributed here
> (the one that was has been removed). Short quotes are attributed inline. Trademarks belong to their owners and
> are used only to identify what is being described. Per-source terms posture is recorded in each page's
> `Access:` header — see [`SCHEMA.md`](SCHEMA.md) and gh#53.

## Pages
| Page | Authoritative for | Trust | Informs |
|---|---|---|---|
| [pages/projectx-gateway-api.md](pages/projectx-gateway-api.md) | ProjectX Gateway API (v1 venue; TopstepX) — REST + SignalR, orders, market data | authoritative | R-1, R-3, R-11, R-13, Q-1, Q-2, Q-4 |
| [pages/tradovate-api.md](pages/tradovate-api.md) | Tradovate API (future venue) — shape vs. ProjectX | authoritative | R-17, Q-14 |
| [pages/finnhub-api.md](pages/finnhub-api.md) | Finnhub API — first **data-only provider** (equities/indices + alt-data, no execution); free-tier limits | authoritative | R-1, R-2, R-17 |
| [pages/tiingo-api.md](pages/tiingo-api.md) | Tiingo API — data-only provider; first concrete **news feed** (R-2), REST-poll, free 3-mo history; also free EOD prices | authoritative | R-2, R-1, R-17 |
| [pages/quantconnect.md](pages/quantconnect.md) | QuantConnect / LEAN — a comparable algo-trading platform, catalogued for **insights only** (not a template to clone) | authoritative | design thinking (R-1/R-3/R-4/R-9/R-11) |
| [pages/quantower.md](pages/quantower.md) | Quantower — multi-broker discretionary terminal, catalogued for **insights only** (used it; not a template) | authoritative | design thinking (R-3/R-10/R-17/ADR-0006) |
| [pages/ninjatrader.md](pages/ninjatrader.md) | NinjaTrader — futures terminal + brokerage, catalogued for **insights only** (used it; not a template) | authoritative | design thinking (R-3/R-9/R-11/R-14) |
| [pages/prop-firm-rules.md](pages/prop-firm-rules.md) | Prop-firm **rules** (Topstep vs. Apex) — drawdown / loss-limit dynamics the R-5 gate enforces; trailing **mode** (EOD or intraday) is **per-account** | authoritative | R-5, R-14, R-17, Q-14 |
| [pages/topstep-brokerage.md](pages/topstep-brokerage.md) | Topstep **Brokerage** — **live real-money** futures brokerage (non-prop); the **self-imposed-floor** reference | authoritative | R-5, R-14, R-17 |
| [pages/take-profit-trader.md](pages/take-profit-trader.md) | Take Profit Trader — futures prop firm on **Tradovate** (reference); stage-varying drawdown (EOD→intraday→EOD), 50% consistency | authoritative | R-5, R-14, R-17, Q-14 |
| [pages/market-sessions-and-settlement.md](pages/market-sessions-and-settlement.md) | **Market sessions, the close & settlement** — equity EOD / MOC, CME futures close + settlement, venue forced-flatten times; the **auto-flatten** timing model | operator / design-time | R-13, R-5, ADR-0007, ADR-0013 |
| [pages/dotnet-coding-conventions.md](pages/dotnet-coding-conventions.md) | .NET / C# **coding conventions** — Microsoft baseline + our deviation (**fluent, not LINQ query-comprehension**) | authoritative | eng §4 · AGENTS |
| [pages/source-control-practices.md](pages/source-control-practices.md) | **Source-control** practices (MS playbook) — our **branch naming** `<type>/<id>_<title>`, develop→staging→main | authoritative | CONTRIBUTING · eng §10 |
| [pages/trading-methodologies.md](pages/trading-methodologies.md) | **Trading methodologies → strategy templates** — the methodology→template five-layer mapping; 13/48 + ICT flagged to-ingest | design-time | R-21, R-4, R-7 |

## By topic
- **Trading venues / APIs:** [ProjectX](pages/projectx-gateway-api.md) · [Tradovate](pages/tradovate-api.md) →
  feed the R-17 venue-capability matrix (Q-14).
- **Data-only providers (no execution):** [Finnhub](pages/finnhub-api.md) — free real-time equities/indices
  (SPY/QQQ) as cross-asset context for ES/NQ, plus alt-data (R-2) · [Tiingo](pages/tiingo-api.md) — free news +
  EOD prices. Both are the market-data/non-market slice of the R-17 abstraction.
- **News / soft signals (R-2 non-market template):** [Tiingo](pages/tiingo-api.md) — first concrete free
  **news-API** source (REST-poll, 3-mo history, ticker-tagged); the reference implementation of the news template.
- **Reference / example systems** (used / comparable, catalogued for insights — not dependencies, not templates to clone): [QuantConnect / LEAN](pages/quantconnect.md) (engine-first algo platform) · [Quantower](pages/quantower.md) (multi-broker discretionary terminal) · [NinjaTrader](pages/ninjatrader.md) (futures terminal + brokerage).
- **Prop-firm rules (risk model):** [Topstep vs. Apex](pages/prop-firm-rules.md) — the trailing-drawdown / daily-loss / consistency rules the **R-5 risk gate must enforce**; the load-bearing variable is the trailing **mode** (EOD vs. intraday), and it's **per-account** — Topstep is EOD-only, Apex offers **both** since Mar 2026 (Q-14). Second data point: **[Take Profit Trader](pages/take-profit-trader.md)** — drawdown mode varies by **stage** (EOD→intraday→EOD). The **consistency target** ("$1,500 and stop") drives the R-5 daily-target stand-down.
- **Live brokerage (self-imposed floor):** [Topstep Brokerage](pages/topstep-brokerage.md) — **real-money**, CFTC introducing broker with **no firm-imposed drawdown**, so R-5 enforces a **self-imposed** floor (the "hold $50K, risk $10K" case).
- **Market sessions & settlement (the close):** [market sessions & settlement](pages/market-sessions-and-settlement.md) — the day's key times (equity EOD 3 PM CT / **MOC**, CME futures close 4 PM CT + **settlement / maintenance**, venue forced-flatten ~3:10) that drive the **R-13 auto-flatten** (default ~2:30 PM CT, pre-MOC) and **ADR-0013** end-of-day resiliency.
- **Engineering practice (coding conventions):** [.NET / C# conventions](pages/dotnet-coding-conventions.md) — Microsoft's baseline we adopt, with the firm deviation that **queries use fluent / method syntax, not LINQ query-comprehension**. Backs engineering §4 and the `AGENTS.md` contracts.
- **Engineering practice (source control):** [source-control practices](pages/source-control-practices.md) — the MS playbook + our branch naming **`<type>/<work-item-id>_<title>`**; backs [`CONTRIBUTING.md`](../../CONTRIBUTING.md) and engineering §10.
- **Trading methodologies (→ strategy templates, R-21):** [methodologies](pages/trading-methodologies.md) — how a methodology (13/48, ICT) decomposes into a template's five layers; specifics **ingested from sources**, not invented.
- *(order-flow theory, instrument specs — to come)*

## Ingest & sources
**Both folders are local-only — deliberately untracked** (`.gitignore`), so they exist on the machine doing the
ingesting and not in a clone. They hold working material, not knowledge: raw drop-zone inputs and third-party
documents, some of it large or not ours to redistribute. Everything worth keeping is promoted into `pages/`,
which *is* tracked, and each ingest is recorded in [`log.md`](log.md) with its source URL — so the provenance
trail survives even though the raw material does not.

- `ingest/` — drop-zone for sources awaiting ingestion: URL-list files or documents. Living/editable — never
  deleted or moved. Workflow in [`SCHEMA.md`](SCHEMA.md).
- `sources/` — each source *as last ingested*; diff against `ingest/` to see what still needs ingesting.
