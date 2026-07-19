# ProjectX Gateway API

> **Trust tier:** authoritative
> **Verified:** against vendor docs, 2026-07-18 · **Sources:** https://gateway.docs.projectx.com/docs/intro
> **Informs:** R-1, R-3, R-11, R-13, R-17, Q-1, Q-2, Q-4

The REST + realtime API behind prop firms on the **ProjectX Gateway** (TopstepX is one firm on it). This is the
v1 trading venue behind `MarqSpec.Client.ProjectX` (see [engineering §3](../../trading-platform-engineering.md)).

## Base URLs (TopstepX instance)
- **REST:** `https://api.topstepx.com`
- **Realtime (SignalR over WebSocket):** `https://rtc.topstepx.com/hubs/user`, `https://rtc.topstepx.com/hubs/market`
- Hosts are **firm-branded** (`…topstepx.com`); other ProjectX firms likely use different hostnames — the R-17
  venue abstraction must accommodate per-firm hosts. Practice vs. live appears **account-level** (by `accountId`),
  not URL-level (docs list no separate sandbox host) → still open for **Q-4**.

## Authentication
- **Session token (JWT)**, valid **24h**; validate / refresh via the session endpoints. Passed as
  `access_token` on the realtime hubs.

## Market data
- **Historical (REST):** `Retrieve Bars` — OHLCV, multi-resolution (`unit` + `unitNumber`). Plus contract
  search (`search-contracts`, `search-contracts-by-id`, `available-contracts`). → the **clean historical** path in R-1.
- **Real-time (SignalR Market Hub `…/hubs/market`):** events `GatewayQuote`, `GatewayTrade`, `GatewayDepth`
  (DOM); subscribe `SubscribeContractQuotes|Trades|MarketDepth(contractId)`. → the **live** path in R-1.
- **Order flow (Q-2):** `GatewayTrade` carries a `type` (TradeLogType **Buy = 0 / Sell = 1**) and `GatewayDepth`
  gives DOM. So **footprint / delta is reconstructable** from trade direction + depth — there is no separate
  explicit aggressor field, but the buy/sell classification + DOM suffice. Feeds R-3. The stream also carries **trade volume**, so it supplies the data for **Bookmap-style** DOM / order-flow displays (a later feature).

## Orders / execution (Q-1)
- `POST /api/Order/place` — params: `accountId`, `contractId`, `size`, `limitPrice?`, `stopPrice?`,
  `trailPrice?`, `customTag?`.
- **Order types:** `1` Limit · `2` Market · `4` Stop · `5` TrailingStop · `6` JoinBid · `7` JoinAsk.
  **Side:** `0` Bid (buy) / `1` Ask (sell).
- **Bracket / OCO** (answers the P1 bracket item + part of Q-1): optional `stopLossBracket` and
  `takeProfitBracket` (`{ticks, type}`) attach an OCO stop/target on fill.
- Also `order-modify`, `order-cancel`, `order-search`, `order-search-open`. → R-11 execution, R-12 re-validation.
- **Positions:** search + close endpoints → R-13 auto-flatten. **Trades:** search. **Account:** search (balance / P&L) → R-5 risk layer.

## Accounts — one login, many (Q-4, R-17)
`POST /api/Account/search` (`{ onlyActiveAccounts }`) lists the login's trading accounts; **verified**, each account
is **just** `{ id, name, balance, canTrade, isVisible }` — **nothing prop-firm-specific**. **Buying power
(50K/100K/150K), evaluation vs. funded stage, status (active/passed/failed), daily-loss limit, and even
practice-vs-live** are **encoded in the account `name`** (`50KTC-V2-DLL-0000-…`, `PRAC-…`) or the firm portal — the
**adapter derives them from the name** (there is no mode / `simulated` field). **TopstepX is Topstep's own platform**
on the ProjectX gateway, so the login *is* the TopstepX login; other ProjectX firms run their own branded hosts
(R-17). Live account state also streams on the user hub (`GatewayUserAccount`).

## Realtime User Hub (`…/hubs/user`)
Events `GatewayUserAccount | Order | Position | Trade`; subscribe `SubscribeAccounts`,
`SubscribeOrders | Positions | Trades(accountId)`. → native fill capture (R-8 / R-9), live account state for the
risk layer (R-5).

## Open items
- Rate limits documented at `/docs/getting-started/rate-limits` — not yet extracted.
- **Q-4 (clarified):** practice vs. live is **name/account-level, derived** — account-search returns no mode / `simulated` flag; `PRAC-…` vs. `50KTC-…` in the `name` is the signal. Not URL-level.
- **Q-3 (auto-flatten guarantee):** a position-close endpoint exists; the failure-mode design is still ours.

## Relevant-link index
- Intro — https://gateway.docs.projectx.com/docs/intro
- Getting started (auth, validate session, connection URLs, rate limits) — https://gateway.docs.projectx.com/docs/category/getting-started
- Connection URLs — https://gateway.docs.projectx.com/docs/getting-started/connection-urls
- API reference (Account, Market Data ×4, Orders ×5, Positions ×3, Trades) — https://gateway.docs.projectx.com/docs/category/api-reference
- Place an Order (order types, brackets) — https://gateway.docs.projectx.com/docs/api-reference/order/order-place
- Retrieve Bars — https://gateway.docs.projectx.com/docs/api-reference/market-data/retrieve-bars
- Realtime overview (SignalR hubs, events) — https://gateway.docs.projectx.com/docs/realtime

Cross-venue comparison: [Tradovate API](tradovate-api.md) · venue matrix Q-14.
