# Prop-firm rules — Topstep vs. Apex

> **Trust tier:** authoritative
> **Verified:** Topstep + Apex help centers via web search, 2026-07-19 (Topstep link is a help *index*; Apex direct
> fetch 403s — so **exact per-size $ amounts confirm in-portal**). **Apex rebuilt its programs on 2026-03-01**
> (one-time payment; two drawdown types; several legacy rules removed). · **Sources:**
> https://help.topstep.com/en/collections/5836609-topstep-program ,
> https://apextraderfunding.com/help-center/eod-trailing-drawdown-accounts/eod-evaluations ,
> https://apextraderfunding.com/help-center/eod-trailing-drawdown-accounts/eod-drawdown-explained/
> **Access:** Topstep's help centre read directly; **Apex refused a direct fetch (403)** and its figures are
> restated from web-search results, not taken from the site. No help-centre text is reproduced. **Apex's terms not
> yet reviewed — grounding the page this way was an implicit decision and is open (gh#53).**
> **Informs:** R-5 (the enforcing risk model must **implement** these), R-14 (mode/account), R-17 / Q-14 (firm
> differences), the account model (data dictionary), [topstep-brokerage.md](topstep-brokerage.md) (self-imposed floors).

The **rules the risk gate (R-5) must enforce** — the drawdown / loss-limit dynamics behind "buying power ≠ risk
budget." Every funded account has the same **shape** (trailing drawdown + profit target + a daily-limit-or-not +
consistency); the **load-bearing variable is _how the trailing drawdown moves_ — EOD vs. intraday — and it is a
property of the _account_, not the firm.**

## The one variable that matters — how the drawdown *trails* (EOD vs. intraday)
- **EOD (end-of-day) trailing** — the floor moves **only at market close**, on the **closing (realized) balance**;
  it is **fixed during the next session** (intraday unrealized profit does **not** move it), yet still **enforced in
  real time if touched**. Your floor is known at the open and doesn't budge — simpler intraday.
- **Intraday trailing** — the floor **follows the peak balance in real time, including unrealized PnL**, a fixed
  distance behind, and **never decreases**. Being **+$1,000 unrealized raises your floor by $1,000** — give it back
  and you're nearer (or through) the floor even at flat realized balance. The gate must track the **intraday peak +
  a moving floor in real time.**

**Who offers which:** **Topstep = EOD only.** **Apex (since 2026-03-01) lets you pick _per account_** — an **EOD
Trail** or an **Intraday Trail** account. ⇒ **R-5 carries a trailing `mode` (EOD | intraday) _per account_** (the
data-dictionary Account entity), keyed off the account, not the firm.

| | **Topstep** (Combine) | **Apex — EOD Trail** | **Apex — Intraday Trail** |
|---|---|---|---|
| Trailing drawdown | **EOD** on realized close | **EOD** on realized close (recalcs once at **4:59:59 PM ET**) | **Intraday** peak, incl. unrealized |
| Enforcement | hit MLL → **account closes** | touch threshold (real-time) → **auto-liquidate** | touch threshold (real-time) → **auto-liquidate** |
| Locks at | starting balance | trails highest **EOD** balance | **start + $100** |
| Daily loss limit | **yes** — deactivates the *day* | **yes** — e.g. $50K → **$1,000** (pauses the day) | **none** |
| Consistency | best day **< 50%** of target | historically **30%** — *confirm post-2026-03-01* | historically **30%** — *confirm post-2026-03-01* |

## Topstep — Trading Combine parameters
| Account | Profit target | Trailing Max Loss Limit (EOD) | Daily Loss Limit |
|---|---|---|---|
| $50K | $3,000 | $2,000 | $1,000 |
| $100K | $6,000 | $3,000 | $2,000 |
| $150K | $9,000 | $4,500 | $3,000 |

The **trailing MLL is the only account-ending rule**; the DLL only ends the *day*. Consistency (Combine): best day
< 50% of the profit target. Express / Live-Funded stages add their own payout + consistency rules.

## Apex — key rules (post 2026-03-01 rebuild)
- **Two drawdown types, chosen per account:**
  - **EOD Trail** — threshold **recalculated once/day at close (4:59:59 PM ET)** on the closing balance, **fixed the
    next session**, enforced real-time; trails the **highest EOD balance**, never down. **Has a Daily Loss Limit**
    (e.g. $50K → **$1,000**) — hitting it **pauses the day**, the account survives.
  - **Intraday Trail** — threshold follows the **real-time peak (incl. unrealized)**, locks at **start + $100**;
    **no daily loss limit.**
- **The 2026-03-01 rebuild** replaced the monthly-subscription eval with a **one-time payment**, added the two
  drawdown types, automated payouts (Deel), and **removed** the MAE rule, the **5:1 R:R** requirement, and the
  **7-day minimum** trading period.
- More account tiers than Topstep; per-size trailing amounts vary — **confirm in-portal**. Consistency at payout
  (historically **30%**) — **confirm current.**

## Consistency — the daily-target discipline
The rule that shapes daily behavior most: **no single day may be too large a share of total profit** — funding is
earned by steady trading, not one lucky session. Computed **`largest single day ÷ total net profit`**:
- **Topstep** — **Combine:** best day **≤ 50% of the profit target** (recommended). **Express Funded:** consistency
  **% ≤ 40%** to be **payout-eligible**; it **resets to $0 after each payout**. *Worked ($100K Combine, $6K target):*
  keep best day **< $3,000** → e.g. `$1,200 / $2,800 = 43% ✓`.
- **[Take Profit Trader](take-profit-trader.md)** — **≤ 50%** (best day < 50% of net profit) to clear Test → PRO.
- **Apex** — historically **~30%** at payout — **confirm post-2026-03-01**.

**How traders comply — the load-bearing UX.** Set a **personal daily profit target** and **stop when it's hit**.
Topstep's own guidance: *"set a Personal Daily Profit Target to lock in gains before exceeding the Consistency
Target"* (optionally auto-liquidating at the limit). That's the operator's *"$1,500 and done"* habit ⇒ **R-5 carries
a per-account daily profit target + consistency cap; on reach the governor stands down** — suppress suggestions
(R-4), tighten sizing, optional **stop-for-day + flatten**. Adherence surfaces in the journal (best-day vs. cap,
P&L-by-day, R-9).

## For our design
- **R-5 enforces the floor** — parameters (per account): trailing **mode** (EOD | intraday), trailing amount,
  current floor, DLL (if any), profit target, consistency. **Intraday ⇒ real-time peak + moving-floor tracking**;
  **EOD ⇒ floor fixed intraday, recompute at close.**
- **`mode` is per account, not per firm** — Apex proves it (one firm sells both). This is exactly the
  data-dictionary Account field `trailing mode (EOD | intraday)`.
- **Self-imposed floors** (live / brokerage accounts, R-5 — e.g. **[Topstep Brokerage](topstep-brokerage.md)**)
  reuse the same machinery: the operator sets a fixed (or trailing) floor; breach → **halt + flatten**. No
  firm-imposed drawdown exists on a live account.
- **Q-14 (firm matrix):** Topstep = **EOD** + **DLL**; Apex = **EOD _or_ intraday** (EOD adds a **DLL**, intraday
  has **none**) + consistency.

## Relevant-link index
- Topstep — program (help center) — https://help.topstep.com/en/collections/5836609-topstep-program
- Topstep — Maximum Loss Limit — https://help.topstep.com/en/articles/8284204-what-is-the-maximum-loss-limit
- Topstep — Trading Combine Parameters — https://help.topstep.com/en/articles/8284197-trading-combine-parameters
- Topstep — Consistency — https://help.topstep.com/en/articles/8284208-consistency-at-topstep
- Apex — EOD Evaluations — https://apextraderfunding.com/help-center/eod-trailing-drawdown-accounts/eod-evaluations
- Apex — EOD Drawdown Explained — https://apextraderfunding.com/help-center/eod-trailing-drawdown-accounts/eod-drawdown-explained/
- Apex — Intraday Trailing Drawdown Explained — https://apextraderfunding.com/help-center/intraday-trailing-drawdown-accounts/intraday-trailing-drawdown-explained/
- Apex — help center (new products, superseded by the EOD/intraday pages above) — https://apextraderfunding.com/help-center/additional-helpful-items/new-products/
