# Tiingo API

> **Trust tier:** authoritative
> **Verified:** against vendor docs + pricing/news-product pages, 2026-07-18 (docs are server-rendered — directly
> read) · full news param list + auth header format from Tiingo API knowledge, **confirm in `/documentation/general/connecting`** · **Sources:** https://www.tiingo.com/documentation/news , https://www.tiingo.com/about/pricing
> **Access:** server-rendered public docs read directly (no auth wall or paywall); facts summarised, no vendor
> text reproduced.
> **Informs:** R-2 (primary — news template), R-17 (data-only provider); R-1 (market-data-capable, but **not wired** — Finnhub owns prices)

A **data-only provider** (market data + **news**, **no account, no execution**) — the market-data/non-market slice
of the decomposed R-17 abstraction, like [Finnhub](finnhub-api.md).

> **Decision (2026-07-18):** Tiingo is wired for its **News REST API only**. The client **builds out Tiingo's full
> API** (the "entire offering"), but the platform **ingests only news** from Tiingo — equities/indices prices come
> from [Finnhub](finnhub-api.md), and Tiingo's price/websocket feeds are implemented but **left inactive to avoid
> duplicating Finnhub price data**.
> **News is multi-source:** **Finnhub news is _augmented_ by Tiingo** — both feeds are wired and **combined with
> cross-source dedup** (R-2). So *price* data is single-source (Finnhub); *news* is deliberately multi-source.

**Primary (and only wired) use: the NEWS feed** — Tiingo is the **first concrete free news-API source** for **R-2**,
the reference implementation of "news is the template" for non-market feeds. News is **REST-poll** (no websocket) ⇒
it lands via the **poller** (R-2 soft-signal ingestion), not the live websocket path. See
[engineering §3](../../trading-platform-engineering.md) (non-market template) and the
[architecture](../../trading-platform-architecture.md) poller component.

## Base URL & auth
- **REST:** `https://api.tiingo.com`
- **Auth:** API token via header **`Authorization: Token <token>`** or query param **`?token=<token>`** (server-side
  only — Options/env, never in source). *(Header format from API knowledge; confirm on the connecting guide.)*

## News API (the focus — → R-2)
- **Endpoint:** `GET https://api.tiingo.com/tiingo/news`
- **Params:** `tickers` (comma list, e.g. `aapl,spy`), `tags`, `sources`, `startDate`, `endDate`, `limit`,
  `offset`, `sortBy` (`crawlDate` | `publishedDate`). *(Only `tickers` + `sortBy` seen in the fetched doc excerpt;
  the rest are standard Tiingo news params — confirm.)*
- **Response per article:** `id`, `title`, `description`, `url`, `publishedDate` (UTC), `crawlDate` (UTC),
  `source` (news domain), `tickers[]`, `tags[]`. → maps cleanly to the R-2 normalized model (source, timestamp,
  content, instruments-referenced, tags).
- **Coverage:** **65,000+** equity tickers (US equities, ETFs, mutual funds, Chinese A-shares), **75+** FX, **4,100+**
  crypto; **~700k articles/month**, added **real-time as discovered**. Tagging is by **slang / company mentions /
  product mentions**, not just ticker string-match — good recall, but tags need trust like any derived signal.
- **History (free):** **3 months** queryable + **all data going forward** (real-time). Commercial/institutional:
  25+ yrs back to 1995 (contact sales) — not needed for going-forward soft signals.
- **Transport:** **REST pull + bulk flat-files only** — **no news websocket** (confirmed: Tiingo's websockets are
  **crypto / forex / equity** only, below). ⇒ **poll** `/tiingo/news` on a configurable interval; instruments/time
  index the structured row, content embeds to **pgvector** (Cohere) for semantic retrieval — the R-2 two-way retrieval.

## Other data — built in the client, **not wired** (Finnhub owns market data)
The R-17 adapter implements these (the "entire offering"), but ingestion **does not activate** them — Finnhub is the
equities/indices source, so wiring Tiingo prices too would duplicate data. Kept complete so activation is later a
**config change, not new code**.
- **EOD "composite" prices** — `GET /tiingo/daily/<ticker>/prices` (free). **IEX intraday** — `/iex/<ticker>`.
  **Crypto** — `/tiingo/crypto`. **FX** — `/tiingo/fx`. **Fundamentals** — add-on (paid, third-party sourced).
  Dividends/splits/fund-fees also present.
- **Websockets (market-data only — confirmed):** **crypto**, **forex**, and **equity** — the equity side is two
  feeds, **IEX** (`wss://api.tiingo.com/iex`) and **Equity-Realtime**. **No news** feed. The **IEX websocket is free
  real-time** but **IEX-venue prints only** (partial volume — fine for a price signal, not a full tape);
  **Equity-Realtime** (full consolidated) is paid. Tiingo *could* supply **both** real-time (IEX) **and** free
  historical (EOD) **equity price** context — technically a fuller free option than [Finnhub](finnhub-api.md) (whose
  historical candles are premium) — **but per the decision above these stay inactive; Finnhub is the price source.**

## Free-tier limits ("Starter", $0/mo)
- **50 requests/hour** · **1,000 requests/day** · **1 GB bandwidth/month** · **500 unique symbols/month**.
- **The binding constraint is 50 req/hour** (~1 call / 72s). For news: **batch many tickers per call** (the
  `tickers` param takes a comma list) and **poll modestly** (e.g. one watchlist-wide news poll every ~90s), well
  under the cap. 500 unique symbols/mo and 1 GB/mo are ample for a single operator's watchlist; news JSON is small.

## Fit / integration notes
- **Full client, news-only activation.** The adapter implements Tiingo's **entire** API surface (news, EOD,
  IEX/Equity-Realtime + crypto/FX websockets, fundamentals) as a complete, test-covered client — but ingestion
  **wires only the News REST endpoint**. *Which feeds are activated is a wiring/config choice*, made here to **avoid
  duplicating [Finnhub](finnhub-api.md) _price_ data** (equities/indices). Finnhub also exposes news, and **both news
  feeds are wired — Finnhub news is _augmented_ by Tiingo — combined under R-2 cross-source dedup** (news is
  deliberately multi-source; only price data is single-source). Activating a Tiingo price feed later is config, not
  new client code.
- **First R-2 news source.** Validates the non-market template end-to-end: source adapter (Tiingo) → normalized
  event → structured (instrument + time) row **+** pgvector embedding → structural & semantic retrieval for
  suggestion/chat context. A second news source is another adapter, not a new pipeline.
- **Poller, not ingestion.** REST-only news ⇒ the **thin poller** (fan-out, normalize, publish to the event log);
  the processor persists + embeds. Interval is a config/Options setting (engineering §8) bounded by 50 req/hr.
- **Data-only** — no account/execution; instruments **source-tagged** (Tiingo) so cross-source joins stay honest.
- **.NET:** no first-party C#/.NET client (the maintained client is Python, `hydrosquall/tiingo-python`) — call
  REST via `HttpClient` behind the non-market source interface, or use a community wrapper (verify).

## Open items / to verify (at adapter time)
- Full news param set (`tags`/`sources`/date range/`limit`/`offset`) and default page size / max `limit`.
- News **latency** and **tag precision/recall** vs. a known feed (the R-2 relevance question); **cross-source dedup
  across Finnhub + Tiingo news** (now an active pair — same article from both feeds must collapse to one) + future
  sources.
- Exact free-tier reset semantics for the hourly cap (rolling vs. fixed window).

## Relevant-link index
- Documentation home — https://www.tiingo.com/documentation/
- General overview — https://www.tiingo.com/documentation/general/overview
- Connecting (auth) — https://www.tiingo.com/documentation/general/connecting
- **News API docs** — https://www.tiingo.com/documentation/news
- News product page (coverage, history) — https://www.tiingo.com/products/news-api
- End-of-Day prices — https://www.tiingo.com/documentation/end-of-day
- Websockets (overview) — https://www.tiingo.com/documentation/websockets/documentation
- IEX websocket (free real-time equity) — https://www.tiingo.com/documentation/websockets/iex
- Pricing (free vs. paid) — https://www.tiingo.com/about/pricing

Related: data-only providers — [Finnhub](finnhub-api.md) (market data). Trading venues — [ProjectX](projectx-gateway-api.md) · [Tradovate](tradovate-api.md).
