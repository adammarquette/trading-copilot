# Take Profit Trader (TPT) — futures prop firm (reference)

> **Trust tier:** authoritative-ish — **direct help-center fetch 403s** (Zendesk); facts **web-search-grounded**
> (2026-07-19) and flagged **confirm in-portal**. **Source:** https://takeprofittraderhelp.zendesk.com/hc/en-us
> **Access:** the help centre **refused a direct fetch (403)**; nothing here was taken from it. The facts below are
> public rules restated from web-search results, and no help-centre text is reproduced. **Terms not yet reviewed —
> the decision to ground the page this way was implicit and is open (gh#53).**
> **Informs:** R-5 (drawdown mode + consistency), R-14 (stage/mode), R-17 / Q-14 (a firm on Tradovate), the account
> model (data dictionary). Companion to [prop-firm-rules.md](prop-firm-rules.md).

**What it is.** A **futures prop firm** (evaluation → funded), catalogued as **reference / context** — the operator
uses firms like this; it is **not** an integration target of its own (it rides **Tradovate**, already the R-17
"second venue" candidate). Included because Apex's rules are "kind of funky," and a second Tradovate firm sharpens
the **platform-vs-firm** picture.

## Model — three stages: Test → PRO → PRO+
| Stage | What | Drawdown | Notes |
|---|---|---|---|
| **Test** | one-step evaluation | **EOD** trailing | profit target **~6%**, **50% consistency**, **no daily loss limit**, 5 min trading days |
| **PRO** | simulated funded | **Intraday** trailing | drawdown **tightens** here (EOD → intraday — a known disadvantage); **80/20** split |
| **PRO+** | live capital (invite) | **EOD** trailing (reverted **May 2026**) | **90/10** split; **Tradovate** is the regulated broker for live |

- **Platforms / data:** **CQG** data feed; connects **NinjaTrader, Tradovate, TradingView**. Live (PRO+) clears
  through **Tradovate**.
- **Consistency:** **≤ 50%** — no single day ≥ 50% of net profit (`highest day ÷ net P/L`) to progress. Feeds the
  R-5 daily-target discipline ([prop-firm-rules.md](prop-firm-rules.md) › Consistency).

## Why it matters to us
- **Trailing mode is per _stage_, not per firm** — TPT is the clean proof: **EOD → intraday → EOD** across
  Test / PRO / PRO+. That is exactly the data-dictionary Account `trailing mode (EOD | intraday)` (per account,
  resolved per stage), and it hardens the R-5 rule that the gate keys off the **account**, never the firm name.
- **Another Tradovate firm** — like Apex, TPT is a **firm on Tradovate**, so it's a **separate login (Connection)**
  under the same platform adapter (R-17). Confirms operator → login-per-firm → accounts.
- **Reference only** — no adapter work implied beyond the shared Tradovate venue.

## Open / confirm
- Exact **per-size** profit targets, trailing amounts, and the current **consistency %** — **confirm in-portal**
  (Zendesk blocked the direct read).

## Relevant-link index
- Take Profit Trader — help center — https://takeprofittraderhelp.zendesk.com/hc/en-us
- Take Profit Trader — site ("best payout policy") — https://takeprofittrader.com/
