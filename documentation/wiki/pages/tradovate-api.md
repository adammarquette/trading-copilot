# Tradovate API

> **Trust tier:** authoritative
> **Verified:** against vendor docs + web search, 2026-07-18 · **Sources:** https://partner.tradovate.com , https://api.tradovate.com/
> **Access:** vendor docs fetched directly + web search; the vendor explicitly invites automated/agent consumption
> (it publishes an LLM index at `partner.tradovate.com/llms.txt` and an MCP server). Nothing reproduced.
> **Informs:** R-17, Q-14

A **future** trading venue (the R-17 venue abstraction). Captured now mainly to compare against
[ProjectX](projectx-gateway-api.md) for the venue-capability matrix (**Q-14**). Note: `api.tradovate.com` is the
API *host*, not docs — the developer docs live at **`partner.tradovate.com`**, which also publishes an LLM index
at `partner.tradovate.com/llms.txt` and an MCP server at `partner.tradovate.com/_mcp/server`.

## Shape vs. ProjectX (for Q-14)
| | ProjectX (TopstepX) | Tradovate |
|---|---|---|
| Surfaces | REST + SignalR/WebSocket | REST + WebSocket |
| Websockets | one realtime host, two hubs (user / market) | **two separate** websockets: **market-data** (`wss://md-{demo,live}.tradovateapi.com/v1/websocket`) and **trading / order** (`wss://{demo,live}.tradovateapi.com/v1/websocket`) |
| Env split | account-level (by `accountId`) | **host / URL-level** (`demo` vs `live` hosts) |
| Auth | session JWT (24h) | **Access Token** (standard Access procedure) |
| Market data | quotes, trades, DOM | quotes, DOM, charts — one subscription per type per contract; by symbol or `contractId` |

## Notes
- The main integration deltas from ProjectX: **separate** market-data vs. trading sockets, and **host-level**
  demo/live. The R-17 venue abstraction must not bake in ProjectX's shape (single realtime host, account-level
  environment).
- Extraction here is intentionally shallow (future venue). The `llms.txt` index is the pointer for a fuller pass
  when Tradovate work actually begins.
- **Platform vs. firm (accounts).** Tradovate is a **platform used by many prop firms** (e.g. **Apex**); a firm
  **provisions a Tradovate login** for the trader and **manages it in the firm's super-account**. So the operator has
  a **Tradovate login _per firm_** (Apex, Take Profit Trader, TradeDay, The Funded Trader, …) — **several Tradovate
  logins** — unlike TopstepX, where the firm (Topstep) *is* the platform (one login). Same account shape otherwise (many accounts; size / stage / status name- or portal-derived) — keep
  it venue-neutral (R-17).

## Relevant-link index
- Partner API docs (home) — https://partner.tradovate.com
- LLM doc index — https://partner.tradovate.com/llms.txt
- Market data over websockets — https://partner.tradovate.com/overview/core-concepts/web-sockets/market-data/market-data
- API host (not docs) — https://api.tradovate.com/
- Example code / FAQ — https://github.com/tradovate/example-api-faq
- Community forum (API developers) — https://community.tradovate.com
- API access (support) — https://support.tradovate.com/s/article/Tradovate-API-Access
