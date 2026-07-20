# ProjectX Gateway API

> **Trust tier:** authoritative
> **Verified:** against vendor docs, 2026-07-18 · **Sources:** https://gateway.docs.projectx.com/docs/intro
> **Informs:** R-1, R-3, R-11, R-13, R-17, Q-1, Q-2, Q-4
> **Corrected 2026-07-20** (gh#9, building the adapter): the account model **does** carry a required `simulated`
> flag — this page previously stated it did not. Verified against the gateway's own **`swagger.json`** (vendored in
> `MarqSpec.Client.ProjectX`), which outranks the prose docs this page was first written from.

The REST + realtime API behind prop firms on the **ProjectX Gateway** (TopstepX is one firm on it). This is the
v1 trading venue behind `MarqSpec.Client.ProjectX` (see [engineering §3](../../trading-platform-engineering.md)).

## Base URLs (TopstepX instance)
- **REST:** `https://api.topstepx.com`
- **Realtime (SignalR over WebSocket):** `https://rtc.topstepx.com/hubs/user`, `https://rtc.topstepx.com/hubs/market`
- Hosts are **firm-branded** (`…topstepx.com`); other ProjectX firms likely use different hostnames — the R-17
  venue abstraction must accommodate per-firm hosts. Practice vs. live is **account-level** — the account's
  `simulated` flag — and **not** URL-level: there is no separate sandbox host (**Q-4 answered**; see Accounts).

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

## The `live` flag — a market-data tier, distinct from account mode
Contract search and bar retrieval take a **`live` boolean** selecting which market-data universe answers. It is
**not** the same axis as an account's `simulated` flag: that says what an *account* is, this says which *data*
you are entitled to see. Measured against the gateway with **practice** credentials (2026-07-20, gh#9):

| call | `live: true` | `live: false` |
|---|---|---|
| `SearchContracts("ES")` | **0** | **6** — incl. `CON.F.US.EP.U26` (ESU6) |
| `AvailableContracts` | **0** | **51** |

**The wrong tier returns an empty result, not an error.** Nothing 4xxs; the universe is simply empty, and the
failure surfaces far away as "no contract matches ES". Practice credentials must pass `live: false`. The adapter
therefore takes the tier as a **required** constructor argument (`ProjectXDataTier`) rather than defaulting it —
a silent default here is indistinguishable from a missing instrument.

**Known broken:** `Retrieve Bars` currently **400s for every parameter combination** — the gateway rejects the
`unit` field (`"The JSON value could not be converted to …AggregateBarUnit"`), so the whole body fails to bind.
Ruled out: unit, window, limit, partial-bar flag, and the tier. Tracked in **gh#56**; the fix belongs in
`MarqSpec.Client.ProjectX`'s serialization, not in the adapter.

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
`POST /api/Account/search` (`{ onlyActiveAccounts }`) lists the login's trading accounts. Each account is
`{ id, name, balance, canTrade, isVisible, simulated }` — **nothing prop-firm-specific**. **Buying power
(50K/100K/150K), evaluation vs. funded stage, status (active/passed/failed), and daily-loss limit** are **encoded
in the account `name`** (`50KTC-V2-DLL-0000-…`, `PRAC-…`) or the firm portal — the **adapter derives those from the
name**. **Practice-vs-live is *not* among them:** `simulated` is a **required boolean** on the account model, so
the mode is read, never inferred (corrected 2026-07-20 — see the header note). The adapter deliberately ignores a
`PRAC-` prefix: trusting the name could only reclassify a **live** account as practice, the one direction that
risks real money (R-14). **TopstepX is Topstep's own platform**
on the ProjectX gateway, so the login *is* the TopstepX login; other ProjectX firms run their own branded hosts
(R-17). Live account state also streams on the user hub (`GatewayUserAccount`).

## Realtime User Hub (`…/hubs/user`)
Events `GatewayUserAccount | Order | Position | Trade`; subscribe `SubscribeAccounts`,
`SubscribeOrders | Positions | Trades(accountId)`. → native fill capture (R-8 / R-9), live account state for the
risk layer (R-5).

## Open items
- Rate limits documented at `/docs/getting-started/rate-limits` — not yet extracted.
- **Q-4 (answered):** practice vs. live is **account-level and explicit** — account-search returns a required `simulated` boolean. Not URL-level (there is no sandbox host), and not name-derived as this page first claimed.
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
