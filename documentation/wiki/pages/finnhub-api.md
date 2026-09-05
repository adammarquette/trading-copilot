# Finnhub API

> **Trust tier:** authoritative
> **Verified:** free-tier limits + candle restriction via web search & vendor issue [finnhubio/Finnhub-API#546](https://github.com/finnhubio/Finnhub-API/issues/546), 2026-07-18 · endpoint paths from Finnhub API knowledge (vendor docs are a JS SPA, not directly machine-fetchable) — **confirm exact wire formats in-console** when adapter work starts · **Sources:** https://finnhub.io/docs/api , https://finnhub.io/pricing
> **Access:** the vendor docs (a JS SPA) were never machine-read — nothing was taken from them; facts are grounded
> via web search and the vendor's own public GitHub issue tracker, endpoint paths from API knowledge (flagged
> confirm). No vendor text reproduced.
> **Informs:** R-1 (live path), R-2 (alt-data template), R-17 (data-only provider)

The **first data-only provider** — real-time equities/indices + alternative data, **no account, no execution**.
Finnhub implements just the **market-data slice** of the decomposed R-17 abstraction (a *trading venue* like
[ProjectX](projectx-gateway-api.md) implements market-data / account / execution; a *data-only provider*
implements market-data only). See [engineering §3](../../trading-platform-engineering.md) (Data sources — providers
vs. trading venues) and the [architecture](../../trading-platform-architecture.md) ingestion component. **Primary
use:** free real-time **SPY / QQQ** as **cross-asset context** for the futures we trade (SPY ↔ ES, NASDAQ/QQQ ↔ NQ).

## Base URLs
- **REST:** `https://finnhub.io/api/v1`
- **Realtime (WebSocket):** `wss://ws.finnhub.io?token=<API_KEY>`

## Authentication
- **Free API key** (register on finnhub.io). Passed as `token=<key>` **query param** on REST, or header
  **`X-Finnhub-Token: <key>`**; on the websocket, `token` is in the URL. Server-side only (Options/env, never in
  source — engineering §8).

## Market data
- **Live (WebSocket) — the R-1 *live* path for Finnhub.** Real-time **trades** for US stocks. Subscribe
  `{"type":"subscribe","symbol":"SPY"}` / unsubscribe `{"type":"unsubscribe","symbol":"SPY"}`; server pushes
  `{"type":"trade","data":[{"s":symbol,"p":price,"t":ms-epoch,"v":volume,"c":[conditions]}]}` plus periodic
  `{"type":"ping"}`. **Free tier: ≤ 50 symbols** concurrent. This is **cross-asset context**, not the historical
  system-of-record.
- **REST quote** — `GET /quote?symbol=SPY` → `{c,d,dp,h,l,o,pc,t}` (current / change / %chg / high / low / open /
  prev-close / time); real-time on free. Fits the **poller** fan-out for periodic snapshots.
- **REST historical candles** — `GET /stock/candle?symbol=&resolution=&from=&to=` (OHLCV). **PREMIUM — returns
  `403` on free keys** ([issue #546](https://github.com/finnhubio/Finnhub-API/issues/546)). ⇒ **the R-1 *clean
  historical* path is NOT available for US equities on the free tier.** Finnhub-free supplies live context only;
  historical equity bars would need a **paid Finnhub plan or a different source** — a deliberate decision, not an
  assumption.
- **Symbol lookup** — `GET /search?q=` · **US symbols** — `GET /stock/symbol?exchange=US`. Free.
- Tick (`/stock/tick`) and BBO (`/stock/bbo`) — **premium**.

### Symbols
- US stocks/ETFs by **plain ticker**: `SPY`, `QQQ`, `AAPL`. **SPY / QQQ ETFs are the free real-time proxies for
  the S&P 500 / NASDAQ-100** — the raw indices (SPX/NDX) are premium/limited, so the ETFs are how we get free ES/NQ
  correlates. Forex/crypto use exchange prefixes (`OANDA:EUR_USD`, `BINANCE:BTCUSDT`) — mostly premium/limited,
  out of scope for now.

## Alternative / non-market data (→ R-2 template)
Lands via the **R-2 non-market-data template** (per-type source adapter → common normalized model → structured
time-indexed row + pgvector embedding). Free and useful for AI-agent context:
- **News is multi-source:** Finnhub news is **wired and _augmented_ by [Tiingo](tiingo-api.md)** — both feeds combine
  under the **R-2 non-market template with cross-source dedup**. (Contrast *price* data, which stays single-source:
  Finnhub owns equities/indices; Tiingo prices are built-but-unwired.)
- **Market news** — `GET /news?category=general` · **Company news** — `GET /company-news?symbol=&from=&to=` (US).
- **Earnings calendar** — `GET /calendar/earnings` · **IPO calendar** — `GET /calendar/ipo`.
- **SEC filings** — `GET /stock/filings?symbol=` · **Recommendation trends** — `GET /stock/recommendation?symbol=`.
- **Basic financials** — `GET /stock/metric?symbol=&metric=all` (basic set free).
- **Premium** (not free): social/news **sentiment**, **economic** data/calendar, most **insider/institutional**
  detail, international coverage. Confirm tier at integration time — Finnhub shifts endpoints between tiers (candles
  moved to premium in 2022).

## Free-tier limits & caveats
- **Rate:** **60 API calls/minute** (plus a ~30 calls/second burst cap); `429` on exceed.
- **WebSocket:** **≤ 50 symbols** concurrent.
- **Covered on free:** real-time **US stocks (NYSE/NASDAQ)** trades + quotes; market/company news; basic
  fundamentals; symbol search; SEC filings; recommendation trends.
- **Not on free:** historical US-stock **candles** (`403`), tick/BBO, international stocks, sentiment/economic
  alt-data, higher rate limits.
- **Market-data (websocket trades) quality/coverage is UNVERIFIED** — free-feed completeness, latency, and
  trade-condition coverage for the trade-print stream (`FinnhubMarketDataSource`) haven't been validated against
  a known-good source. **Treat as unverified until checked** (the PRD open item; candidate **Q-15**). This is
  *why* Finnhub is worth wiring first — it exercises the data-only-provider path at zero cost before we trust it.
  The **news** half of the free tier is a separate, now-checked item: gh#1122's live-provider pass (engineering
  §"Data sources") validated article delivery, lookback-window age, and ticker tagging — that news-quality
  finding does **not** extend to this market-data caveat, which stays open.
- **What that news pass actually measured** (2026-09-05, gh#1122): ~13–37 articles/day from Reuters / CNBC /
  Bloomberg, every one carrying a URL and a headline, no duplicate URLs in a payload. Two limits: articles
  arrive **already older than a 60-minute lookback**, so that window admits only ~0–1% of them (gh#1123), and
  the `general` category carries **no tickers at all** (gh#1124).

## Fit / integration notes
- **Data-only provider** → implements the market-data interface of the R-17 abstraction; **no** account/execution
  code. Instruments are **source-tagged** (Finnhub) like venue instruments, so cross-asset joins stay honest.
- **Primary:** live **SPY/QQQ** (+ other US tickers) as cross-asset context for **ES/NQ** via the websocket
  ingestion path. **Secondary:** watched symbols and (later) equity **options**; **alt-data** (news/earnings/
  filings) for AI-agent strategies via R-2.
- **.NET:** Finnhub publishes official clients for several languages (Python, Go, JS, …); a first-party **C#/.NET**
  client is **not** established — plan to call REST (`HttpClient`) + websocket (`System.Net.WebSockets`) directly
  behind the market-data abstraction, or use a community wrapper (verify).

## Open items / to verify (in-console at adapter time)
- Exact websocket fields (trade `conditions`, volume semantics); whether a **quote** stream (not just trades)
  exists on free; whether a **news websocket** (`type:"news"`) is free or premium.
- Which alternative-data endpoints are free **at integration time** (tiers drift).
- Real-feed **market-data quality** benchmark (trade-print completeness/latency/conditions) vs. a known-good
  source (resolves the Q-15 caveat) — the news half is checked (gh#1122); this market-data half is not.

## Relevant-link index
- API docs (home) — https://finnhub.io/docs/api
- WebSocket trades — https://finnhub.io/docs/api/websocket-trades
- Quote — https://finnhub.io/docs/api/quote
- Stock candles (premium) — https://finnhub.io/docs/api/stock-candles
- Symbol search — https://finnhub.io/docs/api/symbol-search
- Stock symbols — https://finnhub.io/docs/api/stock-symbols
- Company news — https://finnhub.io/docs/api/company-news
- Market news — https://finnhub.io/docs/api/market-news
- Earnings calendar — https://finnhub.io/docs/api/earnings-calendar
- Rate limits — https://finnhub.io/docs/api/rate-limit
- Pricing (free vs premium) — https://finnhub.io/pricing
- Free-plan candle restriction (vendor issue) — https://github.com/finnhubio/Finnhub-API/issues/546

Related: data-only provider vs. trading venue — [ProjectX](projectx-gateway-api.md) · [Tradovate](tradovate-api.md).
