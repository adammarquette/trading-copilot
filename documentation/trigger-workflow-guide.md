# How a trigger becomes an alert — an operator's guide (R-7, R-4)

**Companion to:** the *Trigger / condition engine* section of
[`trading-platform-architecture.md`](trading-platform-architecture.md), which carries two diagrams of this same
flow for an engineer or agent reader — a lifecycle flowchart and a fire sequence. This page answers the same
questions in prose, for the person who will actually be paged and has not read the code: how does something you
care about become a standing alert, what has to happen for your phone to buzz, and — critically — when the
co-pilot will deliberately say nothing, so a quiet phone doesn't get mistaken for a broken one.

**Everything below is true of the system as shipped today.** Where a phrase in the wireframes or the product
requirements describes something not yet built, this page says so rather than describing it as if it works.

## The two halves, and which one exists

The rulebook is designed as two halves. **Only one is built:**

- **Built — structural triggers.** You author a trigger directly: instrument, indicator, period, resolution, a
  comparison (above/below), and a threshold. That's the whole vocabulary today — one indicator against one number.
  Order-flow, multi-condition, cross-asset and time-of-day conditions are designed and deferred.
- **Not built — plain-language rules.** The idea of stating a practice in your own words in chat and having it
  compile into a trigger (the `Rule` entity, the NL → condition compiler) does not exist yet. That work is tracked
  as [gh#660](https://github.com/adammarquette/trading-copilot/issues/660), which is blocked on the still-undecomposed
  playbooks epic. `SourceRuleId` and `SourceConversationId` already exist on a trigger record as empty seams, waiting
  for that compiler to fill them in.
- **Chat cannot write a trigger for you today**, in either direction — the chat tools are read-only by
  construction ([ADR-0025](adr/0025-chat-tool-read-only-boundary.md)). Authoring happens through the trigger form on the
  Rulebook surface, over `POST /api/triggers`.

The in-app Rulebook surface already carries a one-line version of this same honesty: *"Standing conditions that
alert you when an indicator crosses your threshold. A trigger does nothing until you confirm it — being switched
on is not enough. Plain-language rules are not built yet."* This page is the longer explanation that line can
point a curious operator at.

## The walkthrough: from authoring to what lands in front of you

### 1. You author a trigger — it does nothing yet

Filling in the form and saving it creates a trigger record, but that record starts **unconfirmed**, and
unconfirmed is a state entirely separate from the on/off switch. A trigger reading "Enabled: yes" can still be
completely inert — unconfirmed is the *zero value* the system defaults to, on purpose, so a bug that drops the
confirmation column, a bad migration, or a corrupted row all fail toward silence rather than toward firing
something you never reviewed. The same rule applies to a trigger a future compiler or an agent proposes on your
behalf: authorship, by anyone or anything, arms nothing.

### 2. You confirm it — now it's live

A separate, deliberate action — confirming the trigger — is what makes the scan start evaluating it at all. Until
you do, the condition can be true or false, crossing back and forth all day, and the scan literally never looks
at it. This mirrors the same posture the order path uses elsewhere in the system — an arm step separate from the
step that actually commits to something — where nothing autonomous acts on your behalf without an explicit step
that only you take.

Editing a trigger you've already confirmed does **not** un-confirm it — but it does quietly reset its internal
"have I seen this condition before" memory. That matters for the next section: an edit is treated the same way as
a brand-new trigger for the purpose of not firing on stale truth.

### 3. The scan runs continuously, with no LLM anywhere in it

A background process re-reads every **confirmed and enabled** trigger on a fixed cadence, pulling the same
pre-computed indicator values the chart uses (never recomputed on the fly). This is cheap, deterministic code —
not a model — which is exactly why it can run continuously without spending anything. The LLM only shows up
later, and only for a subset of fires (see the next step).

**A condition that's already true the moment a trigger is confirmed does not fire.** The scan silently records
that the condition currently holds and waits for it to clear and then cross again — the crossing is what fires,
never the mere presence of an already-true condition. This is the same rule that applies after an edit: if editing
a trigger's threshold makes an already-static condition newly true, that does not produce a surprise alert either.
You'll only ever be alerted on an observed crossing, never on a truth that was there when the trigger (re)started
watching.

Once a trigger has fired, it holds — a continuously-satisfied condition does not alert you again and again. It
only re-arms, ready to fire again, once the condition clears and the opposite crossing is observed.

### 4. On a genuine crossing, one of two routes fires

Each trigger is configured for one of two routes, decided when it's authored:

- **Mechanical.** No LLM involved at all. The moment the crossing is observed, the alert goes straight to your
  notification channel. The record of the fire is committed to the database *after* the alert is sent — so in the
  rare case something goes wrong writing that record, you were still told. Nothing is held back waiting on
  storage.
- **Agent-review.** The crossing wakes an AI reviewer that looks at the fired setup and decides whether it's worth
  turning into an actual trade suggestion — entry, stop, target and a written rationale — or whether to say
  nothing. This route is slower and costs money per look, so two gates run **before any model is called at all**:
  a personal risk throttle (are you near your daily drawdown governor, or have you already hit your profit target
  and chosen to stand down?) and a platform-wide daily AI-spend budget. If either says no, the review never
  happens — no cost is incurred, and you are told why (see the table below). If both say yes, a cheap model does
  a first pass; only if that first pass finds the setup genuinely hard to judge, *and* the day's remaining AI
  budget can still afford it, does a second, more expensive pass with more context run. A review is **at most two
  model calls**, ever, per fire — one cheap, optionally one more.

  If the reviewer proposes a suggestion, it's written to the database first and you're notified about it
  afterward (the opposite ordering from the mechanical route) — the suggestion has to actually exist before
  anything tells you it does.

On the agent-review route, the trigger's arm state advances the same way whether the review ends in a suggestion
or in silence — a fire is a fire whether or not anything visible came out of it, and that's what stops a
persistently-true condition from being re-reviewed (and re-billed) every single pass. The one exception is the
mechanical route's own failure mode: if an alert genuinely can't be delivered, the trigger deliberately stays
armed rather than quietly marking itself fired, so the next pass tries again instead of the alert being lost.

### 5. What arrives, and what you can do with it

A **mechanical alert** is just that — a notification that a condition you defined has crossed. There's nothing to
act on inside the app beyond reading it and deciding what to do at the venue or on the order ticket yourself.

An **agent-review suggestion** is a fully specified trade idea: side, entry, stop, target, a plain-language
rationale, and a 0–100 confidence score. Two things about it are guaranteed regardless of what the model proposed:

- **Size, the account, and the trading mode it's issued for are never the model's.** They come from the trigger
  you authored, not from anything the AI decided — the model is never even shown which account or mode a fire
  belongs to. Confidence is display-only — even a suggestion the model scored near zero still becomes a row you
  can look at; it never self-censors by number, and the number never resizes anything.
- **The suggestion cannot outlive today's auto-flatten.** Its validity window is clamped to whichever comes first
  — the configured window, or the time remaining until this instrument's scheduled auto-flatten — so you will
  never be handed a "still valid" suggestion for a position the system is about to close out from under you.

From there you can **take** it (it re-enters the same order gate every other order goes through — sizing, risk
limits and everything else still apply exactly as if you'd typed the ticket yourself; taking a suggestion does
not bypass any guard) or **pass** on it (recording why, which feeds the longer-term learning loop). The reviewer
itself never reaches an order, a venue, or the risk gate directly — proposing and executing stay two fully
separate steps.

## "Why didn't I get an alert?"

A firing can legitimately stop at several different points before anything reaches you. The table below is meant
to answer the question in the title precisely: which of these are normal, silent-by-design behavior, and which of
these do tell you something happened even though no suggestion resulted. Every row below is now one or the
other — this page no longer carries a gap it has to paper over.

| What happened | Is this normal? | Are you told? |
| --- | --- | --- |
| The trigger is unconfirmed | Yes — it was never armed in the first place, not "didn't fire" | No — and it shouldn't be; the Rulebook surface shows this state at a glance |
| The condition just hasn't crossed yet | Yes — nothing has happened | No — correctly nothing to say |
| The condition was already true the moment the trigger (re)started watching, after being confirmed or edited | Yes, by design — it seeds silently so you're never alerted on stale truth | No |
| The condition is sitting in a hysteresis dead-band, between the threshold and its buffer | Yes — treated like "no reading yet"; it never fires and never resets an existing fire | No |
| The indicator behind the trigger has stopped producing values (a data-pipeline gap) | No — this is a fault. The trigger holds exactly like the hysteresis case above (never fires, never re-arms) and, unlike almost every other fault below, it never disables itself over it | **Yes.** Past 30 minutes in that state, the outage is named in a structured log line an engineer can find **and** a Notify-level advisory reaches your notification stream, once per outage — the same debounce ([gh#1045](https://github.com/adammarquette/trading-copilot/issues/1045)) that already kept the log to one line per outage rather than one per scan. There is still no separate Alertmanager rule for this condition; the in-app advisory is the whole mechanism. |
| Your daily-drawdown governor or profit-target stand-down suppressed a new entry | Yes — a deliberate risk decision, made *before* any model call so it costs nothing | Yes — "Suggestions paused," quoting the reason |
| The platform's daily AI-spend budget was already spent | Yes — the same fail-closed-but-not-silent posture | Yes — you're told review is paused for the day |
| The cheap pass flagged the setup as hard enough to want the expensive pass, but the day's AI budget couldn't afford it | Yes — an intentional budget guard | Yes — told a setup fired and needs a manual look |
| No reviewer is configured yet, or the configured one failed to respond | No, in the "something's not right" sense — but it fails closed, not silently | Yes — told a setup fired that couldn't be reviewed |
| The model looked at it and judged it genuinely not worth surfacing | Yes — this is the one legitimate case where an actual review happened and produced nothing on purpose | No — this is intentional silence, not a fault |
| The model's response couldn't be parsed into a valid suggestion, or proposed an incoherent trade (wrong-side stop, a non-positive price) | No — this is a real fault | **Yes.** It's logged server-side for an engineer *and* a Notify-level advisory tells you a setup fired that needed review but couldn't be used — a generic message, deliberately: the model's own words are untrusted display data, so the advisory never re-surfaces its raw output or the parsed reason ([gh#1042](https://github.com/adammarquette/trading-copilot/issues/1042)). |

The staleness row above and this last row **were** the two honest exceptions to the "you're always told" pattern
the rest of this table establishes — both now closed. [gh#1045](https://github.com/adammarquette/trading-copilot/issues/1045)
routed the staleness report through the same advisory mechanism the table's other "Yes" rows already used, and
[gh#1042](https://github.com/adammarquette/trading-copilot/issues/1042) did the same for a malformed or incoherent
model response. Silence now means either nothing happened or the co-pilot told you why, without exception, on
every row above.

## What this page deliberately doesn't cover

- **How to author a trigger, step by step** — the form itself is the reference; this page explains what happens
  once you've used it, not its fields.
- **The plain-language rulebook, playbooks, or anything else on gh#660 / gh#19** — unbuilt, and covered above only
  to say so.
- **The mechanics of the risk gate itself** — see the architecture doc's *Risk gate* section and
  [ADR-0007](adr/0007-order-execution-model.md); this page only establishes that taking a suggestion re-enters it
  unchanged.

## See also

- [`trading-platform-architecture.md`](trading-platform-architecture.md) — *Trigger / condition engine* section,
  for the engineer-facing diagrams this page narrates.
- [ADR-0008](adr/0008-ai-invocation-cost-model.md) — the cost model and rationale behind the two routes.
- [`trading-platform-prd.md`](trading-platform-prd.md) — [R-4](trading-platform-prd.md#r-4) (suggestion engine)
  and [R-7](trading-platform-prd.md#r-7) (durable rulebook).
