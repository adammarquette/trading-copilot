# Topstep Brokerage — live real-money futures brokerage (reference)

> **Trust tier:** authoritative (vendor help center) — but the landing help page is a **navigation hub**; specifics
> (margins, account minimums, exact risk tools) are **light and need a deeper pass / in-portal confirmation**.
> **Verified:** help.topstepbrokerage.com via WebFetch, 2026-07-19. **Source:** https://help.topstepbrokerage.com/en/
> **Informs:** R-5 (self-imposed floor on live accounts), R-14 (live vs. practice), R-17 (venue abstraction), the
> account model (data dictionary — `type = live-brokerage`). See also [prop-firm-rules.md](prop-firm-rules.md).

**What it is.** **Topstep Brokerage LLC** is a **live, real-money futures brokerage** — *"an introducing broker
registered with the Commodity Futures Trading Commission"* (CFTC). It is **distinct from the Topstep prop-firm
program** (Trading Combine / TopstepX funded accounts): here the operator trades **their own capital**, not a funded
evaluation. Catalogued as the concrete example of a **non-prop, live-brokerage account** — a reference and a possible
future integration, **not** a template to clone.

## Why it matters to us — the live-account risk model
- **No firm-imposed drawdown.** A live brokerage has **no trailing Max Loss Limit** and **no evaluation to pass** —
  nothing external ends the account for drawdown. So the **risk floor is entirely self-imposed** (R-5): the operator
  decides *"I hold $50K for margin but only risk $10K,"* and our gate enforces that **self-imposed floor → halt +
  flatten** using the **same trailing-floor machinery** as the prop firms ([prop-firm-rules.md](prop-firm-rules.md)).
  This is the canonical case behind the data-dictionary `source = self-imposed` floor.
- **Real money ⇒ production-only.** Per the deployment rules, a live real-money account is **wired only in
  production**; dev/staging use **practice** accounts (R-14). The execution path is identical; the stakes are not.
- **Standard brokerage mechanics** — real **margin requirements**, buying power, and the broker's own
  **auto-liquidation** on a margin breach (the site names "auto-liquidation alerts" / "risk tools"). Buying power
  here is a **margin** concept (how many contracts you can hold) — still **≠ the risk budget** (headroom to the
  self-imposed floor).

## What the help site states (light — hub page)
- Products: **futures** (a dedicated "Futures Trading" section); the disclaimer names *"futures, options, or forex"*
  generically — **which of those are actually offered is unclear** from the hub page.
- **Account types** and **margin requirements** exist as help topics (*"how much capital you'll need … before you
  apply"*) but **without specifics** on the landing page.
- Platform / risk: references **"auto-liquidation alerts"** and **"risk tools."**

## Open questions
- **Venue / adapter (R-17):** which platform / clearing does Topstep Brokerage route through, and does it expose a
  ProjectX-style API or a different one? (Determines the next adapter's shape.) — **unresolved.**
- Exact **account minimums, day-trade margins**, and whether **options on futures** are supported. — confirm in a
  deeper pass / in-portal.

## Relevant-link index
- Topstep Brokerage — help center (home) — https://help.topstepbrokerage.com/en/
