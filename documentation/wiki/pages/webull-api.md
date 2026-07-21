# Webull API

> **Trust tier:** authoritative
> **Verified:** direct WebFetch of `developer.webull.com/apis/docs/**`, 2026-07-21 — overview, about-open-api,
> getting-started, authentication/overview, market-data-api (overview / getting-started / data-streaming-api),
> reference/market-data-streaming, reference/futures-market-data, reference/futures-products, trade-api/futures,
> sdk. Signature algorithm (SHA1 vs SHA256) and several other points flagged **confirm** below. ·
> **Sources:** https://developer.webull.com/apis/docs/ , https://developer.webull.com/apis/docs/authentication/overview/ , https://developer.webull.com/apis/docs/market-data-api/overview/ , https://developer.webull.com/apis/docs/reference/futures-market-data/
> **Access:** direct WebFetch, all `200` — no auth wall or paywall on the docs themselves (contrast Finnhub's
> JS-SPA docs). Three guessed paths 404'd — `reference/market-data/`, `quickstart/streaming-market-data/`,
> `reference/futures-symbols/` — and were re-grounded via the correct sibling paths (`market-data-api/overview/`,
> `market-data-api/data-streaming-api/`, `trade-api/futures/`), located via web search, then fetched directly.
> **Informs:** R-1 (market-data augmentation + live path), R-17 (cross-asset data-only provider; future execution
> venue), Q-14 (venue-capability matrix, future) — options execution is the future R-11 dimension

A **cross-asset market-data source** — US stocks/ETFs, options, futures, crypto, and event contracts, all behind
one official Webull **OpenAPI** — that augments the co-pilot's primary futures feed from
[ProjectX](projectx-gateway-api.md)/[Topstep](topstep-brokerage.md) (R-1). It plays the same role
[Finnhub](finnhub-api.md) plays for SPY/QQQ-as-ES/NQ-proxy context, but goes one step further: Webull can quote
the **futures contracts themselves** (`ESZ5`, `NQZ5`, …) directly — *once* its paid futures market-data
subscription is actually available, which per the vendor docs it is **not yet, generally** (see below — this is
the load-bearing caveat on this page).

Webull is also, uniquely among the providers catalogued here, a full **brokerage**: it exposes a **Trading API**
(orders, account, a gRPC order-event stream) behind either the direct HMAC auth described here or a separate
OAuth 2.0 **Connect API** (built for third-party platforms acting on behalf of many end users — not this
single-operator use case). **That execution surface is out of scope for this page.** For this platform, Webull
implements only the **market-data slice of the decomposed R-17 abstraction** — same as
[Finnhub](finnhub-api.md) / [Tiingo](tiingo-api.md), contrasted with the trading venues
[ProjectX](projectx-gateway-api.md) / [Tradovate](tradovate-api.md) (see
[engineering §3](../../trading-platform-engineering.md), data sources: providers vs. trading venues). Futures
**execution** stays exclusively on ProjectX/Topstep (R-11) — futures on Webull are **market-data only**.
**Options trading via Webull is a plausible future increment** (a second R-11 execution dimension), noted here
only lightly.

## Base URLs
- **HTTP, production:** `https://api.webull.com`
- **HTTP, sandbox:** `https://api.sandbox.webull.com` — shared test accounts, **no application required**.
- **MQTT streaming, production:** TCP `data-api.webull.com:1883` · WebSocket `wss://data-api.webull.com:8883/mqtt`.
- This page covers the **US** OpenAPI (`developer.webull.com`) only. Regional doc mirrors exist
  (`developer.webull.com.my`, `.hk`, `.au`, `.co.th`, `.co.jp`, …) — whether those are separate API deployments
  or just localized docs for the same US host is **unconfirmed** (open item; likely irrelevant to a US-market
  operator either way).

## Transports
| Transport | Purpose | Relevance here |
|---|---|---|
| **HTTP** | Request/response — trading, account, and on-demand market-data queries (the "Data API") | **Primary** — REST calls for snapshot/tick/quotes/historical bars |
| **MQTT** (v3.1.1) | Real-time market-data push (the "Data Streaming API") | **Primary** — the live path, parallel to Finnhub's websocket |
| **gRPC** | Server-streaming order-status / account-event subscriptions | **Out of scope** — a trading/execution concern; only relevant if Webull execution is ever wired (future) |

## Authentication
**Not OAuth.** Every request is signed per-call with an **HMAC signature** derived from an **App Key + App
Secret** (the App Secret is never transmitted — it's used client-side to compute the signature only). Required
headers:
- `x-app-key` — the developer's App Key.
- `x-timestamp` — ISO 8601, UTC (e.g. `2026-07-21T00:00:00Z`).
- `x-signature` — the computed HMAC.
- `x-signature-algorithm` — documented as **`HMAC-SHA1`** on both the `/authentication/overview/` and
  `/about-open-api/` pages (confirmed via direct fetch of both, independently, same value). **However**,
  secondary/aggregated sources (marketing copy, SDK-adjacent write-ups surfaced via web search) describe
  **`HMAC-SHA256`** instead. **Flagged confirm** — resolve the true algorithm before hand-rolling a .NET signer;
  the official SDKs hide this entirely.
- `x-signature-version` — e.g. `1.0`.
- `x-signature-nonce` — a unique random string per request.
- `x-version` — interface version, e.g. `v2`.
- `x-access-token` — required for trading/account calls; obtained via an out-of-band approval flow in the
  **Webull mobile app** (2FA-style), then reused across calls. **Token lifetime is not stated** in the pages
  fetched — open item.

A separate **Connect API** exists and uses standard **OAuth 2.0** — for third-party platforms acting on behalf of
many end users. Out of scope for a single-operator data consumer; noted only so it isn't confused with the HMAC
path above.

## Access & onboarding
- **Gated behind an application.** An individual/institutional application through the Webull developer
  website; review is quoted at **~1–2 business days**; approval issues an **App Key + App Secret** from the
  developer portal.
- **Sandbox needs no application** — `api.sandbox.webull.com` ships **shared test accounts**, the fast path to
  see wire shapes before applying.
- Onboarding funnels toward the **official SDKs** (Python 3.8–3.14, Java JDK 8+), which handle signing, tokens,
  and protocol details automatically. A .NET consumer gets none of that — see Fit notes.

## Market data

### HTTP (Data API) — on-demand / request-response
- **Tick** — trade-by-trade records (time, price, volume, direction) for a specified range.
- **Snapshot** — latest price/quote/volume. Example: `GET /openapi/market-data/stock/snapshot?symbols=AAPL&category=US_STOCK&extend_hour_required=false&overnight_required=false`
  → JSON with `symbol`, `instrument_id`, `price`, `open`, `high`, `low`, `volume`, `change`, `change_ratio`,
  `pre_close`, `last_trade_time` — **numeric values are returned as strings**, to preserve precision.
- **Quotes / Depth of Book** — order-book / level-2 bid-ask at a specified depth; a dedicated Depth-of-Book
  interface is documented specifically for **futures**.
- **Historical Bars** — single-symbol and batch OHLCV candles at multiple granularities (`M1`, `M5`, `D`, …).
  **Daily-and-above bars are forward-adjusted; minute bars are unadjusted.**
- **Footprint** — order-flow / volume-profile-style records at configurable time granularity — directly relevant
  to this platform's footprint/delta methodology (see [trading-methodologies.md](trading-methodologies.md)).
- **Rate limits are inconsistent across the pages fetched:** the general Market Data API overview states
  **300 requests/minute**; the futures-market-data and streaming-subscribe reference pages instead state
  **600 requests/minute**, with **Historical Bars capped at 1 call/second/App Key**. Unresolved — see Open
  items.

### MQTT (Data Streaming API) — real-time push
- **MQTT v3.1.1**, over TCP (`data-api.webull.com:1883`) or WebSocket (`wss://data-api.webull.com:8883/mqtt`).
- MQTT-level "auth" is nominal: **ClientId** (unique per session, not reusable across connections),
  **Username** = App Key, **Password** = any value — the real authorization is carried by the HTTP-signed
  subscribe/unsubscribe calls, not the MQTT credentials.
- **Subscribe/unsubscribe are HTTP calls, not MQTT-native** — a signed HTTP request starts/stops the push; data
  then arrives over the already-open MQTT connection.
- **Topics:** `quote` (real-time order book, Protobuf), `snapshot` (Protobuf), `tick` (Protobuf),
  `event-quote` / `event-snapshot` / `event-tick` (event contracts, Protobuf), `notice` (server notifications,
  JSON), `echo` (heartbeat, empty payload). **Protobuf message schemas were not published** on the pages
  fetched — open item.
- **Limits:** max **5 concurrent connections per App Key** (else error `105`); server retains state for ~1
  minute after a drop but **does not auto-resubscribe** — the client must re-subscribe after reconnecting; push
  rate capped at **3 messages/second/connection**.
- Asset coverage: stocks, ETFs, futures, crypto, event contracts (US market).

### Symbols
- **Futures:** `<product code><month letter><year digit>` — e.g. **`ESZ5`** = E-mini S&P 500, December 2025
  (confirmed directly on the [Futures Trading](https://developer.webull.com/apis/docs/trade-api/futures/) page,
  which is itself an execution-focused page — symbology carries over to the data-only endpoints). Month letters:
  `F`=Jan `G`=Feb `H`=Mar `J`=Apr `K`=May `M`=Jun `N`=Jul `Q`=Aug `U`=Sep `V`=Oct `X`=Nov `Z`=Dec.
  `GET /openapi/instrument/futures/products` lists underlying products and their product codes.
- **Stocks/ETFs:** plain ticker + a `category` param, e.g. `symbols=AAPL&category=US_STOCK` (`US_STOCK` /
  `US_ETF` confirmed; options/crypto/event-contract category values not confirmed in the pages fetched).
- **Crypto / event contracts:** documented only as using "their own dedicated paths" — specific symbol formats
  weren't detailed in the pages fetched. Open item.

## Free tier / sandbox vs. paid subscriptions — the load-bearing caveat
**Almost everything here needs an additional paid market-data subscription on top of an approved App Key:**
- **Crypto & event contracts** — **free**, no additional subscription. The one clean "free" lane.
- **US stocks/ETFs** — need **Nasdaq Basic or TotalView Non-Display**; the getting-started guide explicitly warns
  of a **`403`** response without an active subscription.
- **US options** — need **OPRA Real-Time Non-display** access.
- **Futures — need a separate, paid OpenAPI market-data subscription.** Per the vendor docs, that subscription
  module is still being built and had **not yet been generally released** as of this verification
  (2026-07-21) — it's unconfirmed whether it can even be purchased today. **This means the core use case for
  this page (augmenting the ES/NQ/etc. futures feed with Webull) may not be actionable yet, regardless of App
  Key approval — re-check before any integration work.**
- The **sandbox** (shared test accounts, no application) is the cheapest way to confirm request/response wire
  shapes, but whether it surfaces real (even if delayed) market data or only canned/mock responses is
  **unconfirmed** — open item.

## Fit / integration notes
- **Market-data-only slice of R-17, for now** — implements just the HTTP (tick/snapshot/quotes/historical
  bars/footprint) and MQTT (streaming) interfaces; **no account/execution wired**, so — like
  [Finnhub](finnhub-api.md) / [Tiingo](tiingo-api.md) — **no R-13 auto-flatten exposure**. Instruments sourced
  from Webull should be **source-tagged**, the same discipline applied to Finnhub/ProjectX instruments
  (engineering §3), so cross-venue joins stay honest.
- **Options execution is a plausible future increment** (a second R-11 dimension, via Webull's Trading API +
  gRPC order-status stream) — **not part of this ingest**. Futures **execution** stays on
  [ProjectX](projectx-gateway-api.md)/Topstep; Webull futures data is supplementary/comparative only, never a
  second order-placement path.
- **No .NET SDK** (Python + Java only) — a C#/.NET adapter must: (1) implement the HMAC request signer by hand
  (confirm SHA1 vs SHA256 first — see Authentication), (2) call the HTTP Data API via `HttpClient`, and (3) hold
  an MQTT v3.1.1 client (e.g. **MQTTnet**) for the streaming path — all behind the market-data abstraction, the
  same shape as the Finnhub REST+websocket adapter. gRPC client generation is unnecessary unless/until execution
  is ever wired.
- **Value vs. Finnhub:** Finnhub gives free SPY/QQQ as an ES/NQ *proxy*; Webull can (once/if the futures MD
  subscription actually ships) quote **the futures contracts themselves** — a strictly better signal for the
  traded instruments, at the cost of an application + paid subscription that Finnhub doesn't require. Until that
  subscription is confirmed available, Webull's practical near-term value skews toward **stock/ETF context**
  (itself gated behind Nasdaq Basic/TotalView) rather than futures.

## Open items / to verify
- **Signature algorithm:** `HMAC-SHA1` (direct WebFetch of `/authentication/overview/` and `/about-open-api/`,
  consistent across both) vs. `HMAC-SHA256` (secondary/aggregated web-search summaries) — confirm before
  hand-rolling a signer.
- **Futures market-data subscription:** cost, availability, and whether it's purchasable at all yet (module
  described as still under active development as of 2026-07-21).
- **Rate limits are inconsistent across pages:** `300 req/min` (Market Data API overview) vs. `600 req/min`
  (futures-market-data + streaming-subscribe pages), Historical Bars `1/sec`, unsubscribe `1/sec` — confirm the
  actual per-endpoint numbers at adapter time.
- **MQTT Protobuf schemas** for `quote` / `snapshot` / `tick` / `event-*` topics — topic names are known; wire
  format `.proto` definitions weren't found on the pages fetched.
- Whether an approved App Key with **no** paid MD subscription sees **any** live data (even delayed/limited) vs.
  a hard `403` across stocks/options/futures.
- `x-access-token` **lifetime/expiry** — not stated in the pages fetched.
- Whether the regional doc mirrors (`developer.webull.com.my`, `.hk`, `.au`, `.co.th`, `.co.jp`) reflect separate
  API deployments/eligibility pools, or are just localized docs for the same `api.webull.com`.
- Crypto / event-contract **symbol formats** (only futures' `product+month+year` and stocks' `symbol+category`
  were confirmed in the pages fetched).

## Relevant-link index
- Overview — https://developer.webull.com/apis/docs/
- About Webull OpenAPI — https://developer.webull.com/apis/docs/about-open-api/
- Getting started — https://developer.webull.com/apis/docs/getting-started/
- Authentication overview — https://developer.webull.com/apis/docs/authentication/overview/
- SDKs and tools — https://developer.webull.com/apis/docs/sdk/
- Market Data API overview — https://developer.webull.com/apis/docs/market-data-api/overview/
- Market Data API getting started — https://developer.webull.com/apis/docs/market-data-api/getting-started/
- Data Streaming API (MQTT) — https://developer.webull.com/apis/docs/market-data-api/data-streaming-api/
- Market data / streaming reference (subscribe/unsubscribe) — https://developer.webull.com/apis/docs/reference/market-data-streaming/
- Futures market-data reference — https://developer.webull.com/apis/docs/reference/futures-market-data/
- Futures products reference — https://developer.webull.com/apis/docs/reference/futures-products/
- Futures trading (symbology + execution) — https://developer.webull.com/apis/docs/trade-api/futures/
- Connect API (OAuth 2.0, out of scope) — https://developer.webull.com/apis/docs/connect-api/about-connect-api

Related: data-only provider vs. trading venue — [Finnhub](finnhub-api.md) · [Tiingo](tiingo-api.md) ·
[ProjectX](projectx-gateway-api.md) · [Tradovate](tradovate-api.md).
