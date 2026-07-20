# Wiki — log (append-only)

Chronological ingest / lint history. Prefix: `## [YYYY-MM-DD] <op> | <title>`.

## [2026-07-19] operator-input | Auto-flatten timing + end-of-day settlement resiliency
**Source:** operator (Adam) — session / close mechanics; no external URL (exact times flagged **confirm** vs. CME rulebook / venue help).
**Created:** `pages/market-sessions-and-settlement.md`. **Updated:** R-13 + several stale "3:00 PM CST" refs (PRD), ADR-0013 (auto-flatten guarantee + a settlement bullet), ADR-0007, data-dictionary RiskProfile (`auto-flatten deadline`) + time note, engineering §7, wireframe safety-control copy, index.
**Why:** operator corrected the auto-flatten model — it is **our feature**, fires **ahead of** the venue's forced flatten, and the hard problem is **resiliency / fail-over** across the CME settlement close.

**Key takeaways:**
- **Times (CT):** ~**2:30** our default flatten (pre-**MOC**) · **3:00** cash-equity EOD (MOC volatility) · ~**3:10** venue forced flatten (Topstep — prop-only backstop) · **4:00** CME futures close → **4–5 settlement / maintenance** (settlement price struck).
- **Auto-flatten is a configurable system feature** (default ~2:30 CT), fired **before** the venue backstop — and a live brokerage has **none** — so it needs **redundancy** (the ADR-0013 watchdog earns its keep), not a lean on the venue.
- **Per instrument:** the deadline is set **per instrument** (GC / CL / ES / NQ) — **crude & gold settle earlier** than equities — defaulting from each instrument's session close (Instrument gains a `settlement/close time`; RiskProfile deadline is per-instrument).
- **Carryover trap:** a position held through settlement is **re-marked** at the settlement price → end-of-day handling **reconciles from the venue as source of truth**, is maintenance-window aware, and reconciles the re-mark (ADR-0013).
- **Doc correction:** 3 PM CT is the **equity EOD**, *not* the CME close (4 PM) — fixed the mislabelled references.

## [2026-07-19] ingest | Microsoft source-control playbook — branch-naming convention
**Source:** https://microsoft.github.io/code-with-engineering-playbook/source-control/ (WebFetch ✓, copied to `sources/urls`).
**Created:** `pages/source-control-practices.md` + root **`CONTRIBUTING.md`**. **Updated:** engineering §10, `AGENTS.md`, index.
**Why:** operator asked to formalize source-control practices in a CONTRIBUTING file, with a specific **branch-naming convention**.

**Key takeaways:**
- **Our branch naming (authoritative in `CONTRIBUTING.md`): `<type>/<work-item-id>_<title>`** — `type ∈ {feature, bug,
  hotfix}`, `work-item-id` = the GitHub issue #, `title` = short kebab (e.g. `feature/42_risk-gate`). **Work-item-oriented**
  — deviates from the playbook's `user/<alias>/<feature>` example (issue-first traceability).
- Playbook practices adopted: lock the default branch, PR-only merges, clean/traceable history, a deliberate merge
  strategy, ship a `CONTRIBUTING.md`. Reinforces `develop → staging → main` + rebase/squash-before-merge (engineering §10).

## [2026-07-19] ingest | Microsoft C# coding conventions — engineering baseline
**Sources:** https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions (canonical)
+ the World-Locking-Tools coding guidelines (Unity-oriented — universal parts only). Both WebFetch ✓, copied to `sources/urls`.
**Created:** `pages/dotnet-coding-conventions.md`. **Updated:** engineering §4, root + `src/` `AGENTS.md`, index.
**Why:** operator wants these **universal .NET conventions** adopted as our baseline, and a firm rule that
**queries use fluent / method syntax, not LINQ query-comprehension**.

**Key takeaways:**
- **Adopt Microsoft's C# conventions as the baseline** (naming, `var`-when-obvious, raw strings, collection
  expressions, Allman braces, one-type-per-file, `DateTime.UtcNow`, …); enforce via analyzers + `.editorconfig` +
  `dotnet format` + warnings-as-errors.
- **Deliberate deviation — fluent over LINQ.** The MS doc *shows query-comprehension syntax* (`from … select …`);
  we **override** it — **method / fluent `.Where(…).Select(…)` everywhere, EF Core included** (operator: comprehension
  "pollutes" the code). Recorded in eng §4 + both `AGENTS.md` contracts.
- The WLT doc is **Unity-centric** — kept only universal parts; **excluded** `for`-over-`foreach`, `[SerializeField]`,
  material caching, `#if` platform compilation, license banners.

## [2026-07-19] ingest | Topstep consistency + Take Profit Trader — consistency / daily-target
**Sources:** https://help.topstep.com/en/articles/8284208-consistency-at-topstep (WebFetch ✓) ;
https://takeprofittraderhelp.zendesk.com/hc/en-us (Zendesk **403 → web-search grounded**). Both copied to `sources/urls`.
**Created:** `pages/take-profit-trader.md`. **Updated:** `pages/prop-firm-rules.md` (new **Consistency** section),
PRD R-5 + data-dictionary RiskProfile (**daily profit target + consistency target → governor stand-down**), index.
**Why:** operator flagged the **consistency target** ("$1,500 and stop") as an untouched concept + added TPT as a
second Tradovate-firm reference.

**Key takeaways:**
- **Consistency = `best day ÷ total net profit`.** Topstep: **Combine ≤ 50% of target**, **Express ≤ 40%** (resets
  after payout); **TPT ≤ 50%**; **Apex ~30%** (confirm). Topstep's own fix is a **Personal Daily Profit Target →
  stop / auto-liquidate** — the exact "$1,500 and done" habit. ⇒ **R-5 daily target + stand-down governor.**
- **TPT (Test → PRO → PRO+):** drawdown **EOD → intraday → EOD** (PRO+ reverted May 2026); CQG feed; NinjaTrader /
  Tradovate / TradingView; live clears via **Tradovate**; splits 80/20 → 90/10. **Reinforces trailing mode is per
  _stage / account_, not per firm** — hardens the data-dictionary Account model.

## [2026-07-19] ingest | Topstep Brokerage (live brokerage) + Apex EOD refinement
**Sources:** https://help.topstepbrokerage.com/en/ (WebFetch — nav hub, light) ; Apex EOD pages
(`…/eod-trailing-drawdown-accounts/eod-evaluations`, `…/eod-drawdown-explained/`) **403 on direct fetch → web search
grounded** (all copied to `sources/urls`).
**Created:** `pages/topstep-brokerage.md`. **Updated:** `pages/prop-firm-rules.md` (Apex reframed), `index.md`
rows/topics, data-dictionary Account (live-brokerage + switcher fields). **Why:** operator added the Topstep
Brokerage link as the **live, non-prop** account reference, plus tighter Apex EOD-drawdown URLs.

**Key takeaways:**
- **Topstep Brokerage = live real-money futures brokerage** (CFTC introducing broker), **separate from** the
  TopstepX prop program. ⇒ the concrete **`type = live-brokerage`** account: **no firm-imposed drawdown**, so R-5
  enforces a **self-imposed** floor (the "$50K held / $10K risked" case) with the same trailing machinery.
- **Apex rebuilt 2026-03-01:** now **two drawdown types per account — EOD Trail _or_ Intraday Trail** (was framed
  intraday-only). **EOD** recalcs at close (4:59:59 PM ET), **has a DLL** ($50K → $1,000); **Intraday** tracks the
  real-time peak, locks at start + $100, **no DLL**. Rebuild also removed the MAE rule, 5:1 R:R, and 7-day minimum
  (one-time payment now).
- **Load-bearing correction:** trailing **mode (EOD | intraday) is per _account_, not per firm** — Apex sells both.
  Validates the data-dictionary Account `trailing mode` field; `prop-firm-rules.md` reframed accordingly.
  Consistency (~30%) post-rebuild flagged **confirm**.

## [2026-07-19] ingest | Topstep + Apex prop-firm rules — risk-model input
**Source:** `ingest/urls` — https://help.topstep.com/en/collections/5836609-topstep-program , https://apextraderfunding.com/help-center/additional-helpful-items/new-products/ (copied to `sources/urls`).
**Created:** `pages/prop-firm-rules.md` (Topstep vs. Apex comparison); index row + a "Prop-firm rules" topic.
**Method:** direct fetch failed (Topstep link = help *index*; Apex = 403) → **web search** grounded the mechanics; exact per-size $ amounts flagged **confirm in-portal**. Trust: authoritative (firm help docs). These are **rules the R-5 gate must enforce** (Topstep = v1 firm).

**Key takeaways:**
- **The load-bearing difference — how the drawdown trails:** **Topstep = EOD** (Max Loss Limit moves only at close on realized balance; intraday unrealized doesn't move it) **+ a daily loss limit**; **Apex = intraday** (Trailing Threshold follows the peak in real time incl. unrealized PnL → auto-liquidate on touch) **+ no daily limit + 30% consistency**.
- **Numbers (Topstep):** 50K/100K/150K → target $3K/$6K/$9K, trailing MLL $2K/$3K/$4.5K, DLL $1K/$2K/$3K; the trailing MLL is the only account-ending rule.
- **Risk-model impact:** R-5 carries a trailing **mode** (EOD | intraday); **intraday ⇒ real-time peak + moving-floor tracking** (being up unrealized raises the floor). Self-imposed live-account floors reuse the same machinery.

## [2026-07-19] ingest | Quantower + NinjaTrader — reference/example terminals
**Source:** `ingest/urls` — https://www.quantower.com/assets-and-brokers-features , https://ninjatrader.com/trading-platform (copied to `sources/urls`).
**Created:** `pages/quantower.md`, `pages/ninjatrader.md`; added rows + extended the "Reference / example systems" topic in `index.md`.
**Method:** vendor-site fetch → concise extract. Framed (per the operator) as **example systems the operator has used — insights only, NOT integration targets and NOT templates to clone**, like QuantConnect.

**Key takeaways (UX / feature insights):**
- **NinjaTrader ATM strategies** — automated brackets / stop-loss / self-tightening trailing stops set once on a live position → informs our stop model + order ticket (R-11, ADR-0007).
- **SuperDOM / Quantower order-flow + DOM** — ladder order entry + footprint / volume → R-3 order-flow pane (ADR-0004) + the order ticket.
- **Quantower multi-broker / multi-source + synthetic symbols** → echoes the R-17 venue / data-source abstraction and cross-asset combining.
- **Customizable panel workspaces** (both) → the detachable multi-screen / preset-layout direction (ADR-0006).
- **Market replay + paper trading** (NinjaTrader) → replay (R-9) + practice accounts (R-14).
- **Distinction:** both are **manual / user-scripted terminals**; ours adds the AI suggestion → approval → learning layer + enforcing risk gate they don't have.

## [2026-07-18] ingest | ProjectX Gateway + Tradovate API docs
**Source:** `ingest/urls` — https://gateway.docs.projectx.com/docs/intro , https://api.tradovate.com/
**Created:** `pages/projectx-gateway-api.md`, `pages/tradovate-api.md`; bootstrapped `index.md`, `SCHEMA.md`, this log.
**Method:** followed the relevant doc sections (auth, market data REST + realtime, orders, positions, account) —
indexed + extracted key facts, not a full scrape. Trust tier: authoritative (vendor docs).

**Key takeaways feeding open questions:**
- **Q-1 (ProjectX order API):** full place / modify / cancel / search; order types Limit, Market, Stop,
  TrailingStop, JoinBid, JoinAsk; **bracket/OCO supported** (stopLoss + takeProfit brackets) → the P1 bracket/OCO
  item is feasible on ProjectX.
- **Q-2 (order-flow granularity):** market-hub `GatewayTrade` carries a Buy/Sell `type` and `GatewayDepth` gives
  DOM → **footprint/delta is reconstructable** (no explicit aggressor field, but buy/sell + DOM suffice).
- **R-1:** clean **historical** = `Retrieve Bars` (REST, multi-resolution); **live** = SignalR market hub
  (quote / trade / depth) — matches the two-path R-1 design.
- **Q-4 (practice vs. live):** ProjectX appears account-level (by `accountId`), no separate host; Tradovate splits
  demo/live at the **host** level — the venue abstraction must handle both.
- **Q-14 (venue matrix):** Tradovate uses **two** websockets (market-data vs. trading), host-level demo/live, and
  Access-Token auth — a different shape from ProjectX (single realtime host, account-level env, session JWT).

**Follow-ups:** extract ProjectX rate limits + bar resolutions; deeper Tradovate pass via
`partner.tradovate.com/llms.txt` when venue work starts; consider updating PRD Q-1 / Q-2 with these answers.

## [2026-07-18] ingest | Tiingo API — first concrete news source (R-2)
**Source:** `ingest/urls` — https://www.tiingo.com/documentation/general/overview (copied to `sources/urls`).
**Created:** `pages/tiingo-api.md`; added an index row + a "News / soft signals" topic to `index.md`.
**Method:** Tiingo docs are **server-rendered** (directly read, unlike Finnhub's SPA) — overview, news docs,
pricing, and news-product pages fetched; free-tier facts are vendor-stated. Full news param list + auth header from
API knowledge (flagged). Trust tier: authoritative. Operator created a **free** Tiingo account for **news feeds**.

**Key takeaways feeding the architecture:**
- **First R-2 news source.** Tiingo is a **data-only provider** whose **news feed is FREE** (contrast Finnhub) —
  the first concrete implementation of the "news is the reference template" non-market pipeline. Article shape
  (`title/description/url/publishedDate/crawlDate/source/tickers/tags`) maps directly to the R-2 normalized model.
- **News is REST-poll, not websocket** → lands via the **poller** (R-2 soft-signal ingestion), not the live
  ingestion path. Endpoint `GET /tiingo/news?tickers=…&sortBy=crawlDate`.
- **Free-tier binding constraint: 50 requests/hour** (also 1,000/day, 1 GB/mo, 500 unique symbols/mo). ⇒ **batch
  tickers per call** + poll modestly (~90s watchlist-wide). Ample for one operator.
- **News history free = 3 months** rolling + all going-forward (real-time). Deep archive (25+ yrs) is commercial —
  not needed for going-forward soft signals.
- **Secondary:** free **EOD composite prices** + IEX/crypto/FX (a lighter equity-price path than Finnhub, where
  historical candles are premium). Fundamentals are a paid add-on. Websockets cover market data only, not news.

**Follow-ups:** confirm full news params; benchmark news latency + tag precision (the R-2 relevance question) and
cross-source dedup; decide if Tiingo also serves as the equity-price source.
**Resolved (2026-07-18):** news-websocket question — Tiingo's websockets are **crypto / forex / equity** only
(equity = IEX + Equity-Realtime), **no news** feed → news stays **REST-poll**. Note the **IEX websocket is free**
real-time (IEX prints only) and **EOD history is free**, so Tiingo is a viable free **equity-price** source too
(fuller than Finnhub-free, whose historical candles are premium).
**Decision (2026-07-18):** Tiingo is wired as a **news source only** — the client **builds out the full Tiingo API**
("entire offering"), but ingestion **activates only the News REST feed**. Equities/indices come from **Finnhub**;
Tiingo's price/websocket feeds are built-but-inactive to **avoid duplicating Finnhub data**. Corollary: Finnhub also
has news → to hold "no duplication," **Tiingo owns news and Finnhub news stays unwired** (confirm). The
"full-client, feeds-activated-by-wiring" principle is now in engineering §3 (data-source adapters).
**Refined (2026-07-18):** the news corollary is superseded — **news is multi-source**: **Finnhub news is _augmented_
by Tiingo**, both wired + **deduped (R-2)**. Only *price* data stays single-source (Finnhub owns equities/indices;
Tiingo price feeds built-but-unwired). Engineering §3 updated to split the two: single-source prices vs. multi-source
deduped news.
Consider wiring Tiingo into PRD **R-2** + the architecture poller as the first named news source (offered — the
operator flagged non-market feeds as their own review pass).

## [2026-07-18] ingest | Finnhub API — first data-only provider
**Source:** `ingest/urls` — https://finnhub.io/docs/api (copied to `sources/urls`).
**Created:** `pages/finnhub-api.md`; added an index row + a "Data-only providers" topic to `index.md`.
**Method:** vendor docs are a JS SPA (not machine-fetchable) — free-tier facts corroborated via web search and the
vendor's own GitHub issue [#546](https://github.com/finnhubio/Finnhub-API/issues/546); endpoint paths from Finnhub
API knowledge, flagged to confirm in-console at adapter time. Trust tier: authoritative (with that caveat stated in
the page header).

**Key takeaways feeding the architecture:**
- **Data-only provider (extends R-17):** Finnhub has **no account/execution** — it implements only the market-data
  slice of the decomposed venue abstraction. Validates the "providers vs. trading venues" split (engineering §3).
- **R-1 caveat — historical candles are PREMIUM:** `/stock/candle` returns `403` on free keys, so the **clean-
  historical path is unavailable for US equities on free**. Finnhub-free is a **live cross-asset context** source
  (websocket trades + `/quote`), **not** a historical system-of-record — historical equity bars need a paid plan
  or another source.
- **Free tier:** 60 API calls/min, websocket ≤ 50 symbols, real-time US stocks (NYSE/NASDAQ). Use **SPY/QQQ ETFs**
  as free real-time proxies for S&P/NASDAQ (raw indices SPX/NDX are premium) → correlate with **ES/NQ**.
- **R-2:** free alt-data (market/company **news**, earnings/IPO calendars, SEC filings, recommendation trends)
  rides the non-market template; sentiment/economic are premium.

**Follow-ups:** confirm websocket wire fields + free-vs-premium alt-data endpoints in-console; benchmark free-feed
**data quality** vs. a known-good source (the candidate **Q-15**); decide whether historical equity bars are worth
a paid tier or a second source.

## [2026-07-18] ingest | QuantConnect Local Platform (Python) — reference/example system
**Source:** `ingest/Quantconnect-Cloud-Platform-Python.pdf` → archived to `sources/` (QuantConnect Local Platform manual, ~107 pp).
**Created:** `pages/quantconnect.md` — trust tier authoritative, framed as a **reference/example system** (for insights, *not* a template to clone). Added a "Reference / example systems" topic to `index.md`.
**Method:** full-PDF read delegated to a cheaper model (Sonnet); extracted overview, architecture (LEAN event model, subscriptions/consolidators, backtest-vs-live parity, research + deploy), and insights worth *considering*.
**Key takeaway:** QuantConnect is **engine-first / autonomous** (the algorithm submits orders; the human is an offline developer; post-hoc Liquidate only). Ours is **approval-first / human-in-the-loop** (real-time suggestion + explicit approval, pre-suggestion risk gate). So we reuse structural ideas — subscription/consolidator split, one-path historical+live, typed event streams, order-event vocabulary, performance stats — but need an approval gate, per-suggestion explainability, and an execution-state machine QC never had to build.
