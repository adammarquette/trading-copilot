# Eulerpool API

> **Trust tier:** authoritative
> **Verified:** against public vendor developer docs + product/pricing page, 2026-09-12 (docs are server-rendered —
> directly read). **No live API probe** — `EULERPOOL__APIKEY` was not present in the environment; this card does not
> buy a plan. · **Sources:** https://eulerpool.com/developers/introduction , https://eulerpool.com/developers/authentication , https://eulerpool.com/developers/rate-limits , https://eulerpool.com/developers/errors , https://eulerpool.com/developers/quickstart , https://eulerpool.com/developers/llms.txt , https://eulerpool.com/llms-full.txt , https://eulerpool.com/developers/api/research/press/releases , https://eulerpool.com/financial-data-api
> **Access:** public developer docs + published `llms.txt` machine-read directly (no auth wall or paywall on the
> pages cited). Facts summarised, no vendor text reproduced. Free-tier **news JSON was not exercised** — entitlements
> below are what the docs claim, not a 200 from the wire (the [Tiingo](tiingo-api.md) news add-on 403s on a free key —
> gh#1125 — so a docs-only "all endpoints" claim is not enough).
> **Informs:** R-2 (primary — news / sentiment eval), R-17 (data-only provider); R-1 (quotes/fundamentals exist, but
> **not wired** — [Finnhub](finnhub-api.md) owns prices)

A **data-only provider** (equities-centric fundamentals + quotes + **news / sentiment**, **no account, no
execution**) — the market-data/non-market slice of the decomposed R-17 abstraction, like
[Finnhub](finnhub-api.md) / [Tiingo](tiingo-api.md). Evaluated here as a candidate **R-2 news** source, not as a
price feed.

> **Decision (2026-09-12, gh#1190) — (c) candidate to augment [Tiingo](tiingo-api.md) / fill gh#1125.** Proceed to
> a sibling client + `INewsSource` **after** a live free-key probe of market-news (the Tiingo-shaped trap). Do
> **not** treat Eulerpool as a clean Tiingo replacement: there is no batch `tickers=` news call and no `tags[]`.
> Primary wire, if the probe 200s: **`GET /equity-extended/market-news`** (general articles + optional `related`
> tickers). Company-news is one ticker per request. Sentiment endpoints are **scores, not stories** — they cannot
> implement `INewsSource` (no article URL = no `NewsItem` dedup identity). Quotes / fundamentals stay unwired
> (Finnhub owns R-1). **Next card should be filed** (not on this one): probe + `MarqSpec.Client.Eulerpool` +
> `INewsSource`. Update gh#1125 when that card exists.

## Base URL & auth
- **REST:** `https://api.eulerpool.com/api/1`
- **Auth:** API key as query **`?token=<key>`** or header **`Authorization: Bearer <key>`**. HTTPS only — plain
  HTTP is rejected. Server-side only (Options/env `EULERPOOL__APIKEY`, never in source — engineering §8).
- **Errors:** JSON `{error, message, status}`. `401` missing/invalid key · `404` endpoint/resource · `429` rate
  limited · `500` retry. No OAuth.

## News API (the focus — → R-2)

There is **no news websocket**. Everything below is REST-poll → the R-2 poller, not the live path. Official
first-party SDKs are Python / JS/TS / Go; no C# client — `HttpClient` behind `INewsSource` if we proceed.

### Market news (primary candidate)

- **Endpoint:** `GET https://api.eulerpool.com/api/1/equity-extended/market-news`
- **Params:** `limit` (default 50, max 200). No `since` / date-range documented — the adapter would filter
  `datetime` locally the way [Finnhub](finnhub-api.md) market-news already does.
- **Response per article (docs example):** `headline`, `summary`, `source`, `url`, `image`, `category`,
  `datetime` (**Unix epoch seconds**), `related` (comma-separated tickers, e.g. `AAPL,MSFT`).
- **→ `NewsItem`:** `url` → `Url` (drop rows with no URL — that is the dedup key) · `headline` → `Title` ·
  `summary` → `Summary` · `datetime` → `PublishedAt` via `FromUnixTimeSeconds` · `related`.Split(',') →
  `Tickers`. Same mapping the Finnhub adapter already uses for general market news.
- **Coverage / usefulness:** the docs example is a **macro** headline ("Fed Holds Rates Steady", CNBC) with
  **equity** `related` tickers. Market-coverage copy is stocks / fundamentals; **no futures contract ids**
  (ES / NQ / CL) appear on the news pages. Treat as Finnhub-class **cross-asset / macro context**, not
  futures-tape news. Relevance SPY/macro → ES/NQ stays downstream (gh#359).
- **Why it is interesting vs Finnhub general:** gh#1124 found Finnhub's `general` category carries **no
  tickers**. Eulerpool's docs example **does** fill `related`. That is load-bearing and **unverified on the
  wire** — a live probe must count how often `related` is actually populated before we call this a tagging win.

### Company news (secondary — expensive)

- **Endpoint:** `GET https://api.eulerpool.com/api/1/research/news/{ticker}`
- **Params:** `ticker` path only (example `MSFT`). One name per request — not a Tiingo-style comma list.
- **Response per article:** `headline`, `summary`, `source`, `url`, `datetime` (**ISO-8601 string**, not epoch —
  different from market-news), `category`, `image`. No `tickers[]` in the body; the instrument is the path.
- **→ `NewsItem`:** same URL/title/summary mapping; parse `datetime` as `DateTimeOffset`; `Tickers` = the path
  ticker (plus any future body field if the live payload adds one).
- **Use:** watchlist names only, and only if the monthly budget allows. Do **not** make this the primary poll.

### Other article feeds (do not lead with these)

- **`GET /api/1/datasets/news?limit=`** — "latest general market news, refreshed hourly"; default 100 / max
  1000. Public response schema is empty. Prefer `equity-extended/market-news` until a probe shows this is a
  distinct, richer payload.
- **`GET /api/1/news/feed.xml?language=&type=`** — RSS XML ("all news"), not JSON. Worse fit for `NewsItem`.
- **`GET /api/1/crypto-extended/news-feed`** — crypto outlets, refreshed **daily**. Out of scope for the
  futures desk.
- Analyst **grade-news** / **price-target-news** — equity-research wires, not the R-2 news template.
- **Press releases (News-category sibling — cannot be `INewsSource`):**
  `GET https://api.eulerpool.com/api/1/research/press-releases/{ticker}` (docs page
  `/developers/api/research/press/releases`). Official company releases; path ticker (example `MSFT`).
  Docs example fields: `symbol`, `datetime` (ISO-8601), `headline`, `description`. **No `url`.** Same rule
  as sentiment and as the Finnhub adapter: no URL → no `NewsDedupKey` → drop, do not invent one. Leave this
  feed unwired unless a later card finds a stable URL on the wire.

## Sentiment (scores — not `INewsSource`)

- **News sentiment:** `GET /api/1/sentiment/news-sentiment/{identifier}` — identifier is ISIN / ticker / CUSIP /
  SEDOL / WKN. Docs example fields: `symbol`, `company_news_score`, `sector_avg_news_score`, `bullish_percent`,
  `bearish_percent`, `articles_in_last_week`. **No article URL, no headline.** Cannot map to `NewsItem`.
- Sibling score endpoints (insider MSPR, social / Reddit, StockTwits) are the same class: aggregates on a
  security, not stories.
- A later alt-data card could persist these as a different R-2 row type. They are **not** the next news-client
  card.

## What we would *not* wire

The catalog is 375+ endpoints (quotes, OHLCV, fundamentals, options, calendars, ownership, macro series, …).
**Price / fundamentals stay with [Finnhub](finnhub-api.md)** — activating Eulerpool quotes would duplicate R-1
the same way Tiingo prices stay built-but-unwired. Futures-desk siblings that are **not** this news eval and
stay unwired: CFTC **COT** (`GET /api/1/alternative/cot/{symbol}`), **macro / economic calendars**, and CME
**settlements / crack spreads**. This page does not inventory that surface.

## Free-tier limits (docs, 2026-09-12)

- **100,000 requests / month** on Free (product page, quickstart, and the rate-limits page all agree on this
  fetch). Paid: Builder 1M · Startup 10M · Scale 50M, then metered. The gh#1190 issue text said **1,000** —
  that figure is **stale** relative to today's public pages; confirm again when a key is provisioned.
- Headers: `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` (UTC). Exceed → `429` until reset.
  Free is hard-capped; paid is metered rather than stopped.
- Product page: Free includes **all endpoints**, **15-minute delayed** market data, **non-commercial** usage, no
  credit card. Failed requests are not billed.
- **Binding constraint for news:** an hourly market-news poll is ~720 calls/month — well under 100k. The risk
  is not quota, it is a **hidden news paywall** (unprobed) and a thin `related` field.
- **Terms posture:** non-commercial on Free. A single-operator self-hosted copilot is personal use; confirm at
  key-provision time. Do not put a key in the repo.

## Fit / integration notes
- **Data-only** — no account/execution; instruments **source-tagged** (Eulerpool) so cross-source joins stay
  honest.
- **Poller, not ingestion.** REST-only news ⇒ the thin poller (fan-out, normalize, publish); the processor
  persists + embeds. Interval is Options (engineering §8).
- **Multi-source news:** if wired, Eulerpool **augments** [Finnhub](finnhub-api.md) (and [Tiingo](tiingo-api.md)
  if/when that news add-on is keyed). Same article from two feeds must collapse (R-2 dedup). It does **not**
  replace Tiingo's batch ticker-tagged news product.
- **.NET:** no first-party C# client. Plan `HttpClient` in a sibling `MarqSpec.Client.Eulerpool` behind
  `INewsSource` — **that client is out of scope here** (gh#1190 is the eval).

## Open items / to verify (at probe + adapter time)
- Live free-key status of `GET /equity-extended/market-news` (200 vs 403 / empty). **This is the go/no-go.**
- How often `related` is populated; whether it is equity-only; whether futures symbols ever appear.
- How far back market-news goes (the poller asks `since`; a "latest N" window that is already older than the
  lookback is the gh#1122 / gh#1123 shape).
- Confirm `datetime` epoch vs ISO across market-news vs company-news on the wire.
- Whether `/datasets/news` is a duplicate or a better general feed.
- Re-read the free-tier number and commercial-use clause on the day a key is created.

## Relevant-link index
- Introduction — https://eulerpool.com/developers/introduction
- Quick start — https://eulerpool.com/developers/quickstart
- Authentication — https://eulerpool.com/developers/authentication
- Rate limits — https://eulerpool.com/developers/rate-limits
- Errors — https://eulerpool.com/developers/errors
- Market coverage — https://eulerpool.com/developers/market-coverage
- API reference / `llms.txt` — https://eulerpool.com/developers · https://eulerpool.com/developers/llms.txt
- Full endpoint extract (`llms-full.txt`) — https://eulerpool.com/llms-full.txt
- OpenAPI 3.0 — https://api.eulerpool.com/api/1/documentation/yaml
- **Market news** — https://eulerpool.com/developers/api/equity/extended/market/news
- **Company news** — https://eulerpool.com/developers/api/research/news
- **Press releases** (no URL — not `INewsSource`) — https://eulerpool.com/developers/api/research/press/releases
- Datasets market-news feed — https://eulerpool.com/developers/api/datasets/news
- News RSS — https://eulerpool.com/developers/api/news/feed.xml
- **News sentiment** — https://eulerpool.com/developers/api/sentiment/news/sentiment
- Product / pricing (free vs paid) — https://eulerpool.com/financial-data-api

Related: data-only providers — [Finnhub](finnhub-api.md) (wired news + prices) · [Tiingo](tiingo-api.md) (news
template; free-key news 403s — gh#1125). Trading venues — [ProjectX](projectx-gateway-api.md) ·
[Tradovate](tradovate-api.md).
