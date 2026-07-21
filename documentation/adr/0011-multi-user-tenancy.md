# ADR-0011: Multi-user tenancy & data isolation

**Status:** **Superseded by [ADR-0017](0017-single-operator-data-isolation.md)** (2026-07-21) · **Date:** 2026-07-19 · **Deciders:** Adam (operator)

> **Superseded.** The product returned to **one operator per deployment**
> ([ADR-0015](0015-distribution-licensing-governance.md) → [ADR-0017](0017-single-operator-data-isolation.md)).
> The multi-user premise is dropped; the **data-layer mechanism below — owning identity plus default-deny query
> filters — is kept**, which ADR-0017 reframes as a fail-closed safety property. See ADR-0017 for the current
> decision and its reasoning; the text below is the historical record, preserved unedited.

**Extends / partially supersedes:** [ADR-0003](0003-authentication.md) (auth — its "single-operator now" premise).
**Relates to:** PRD `R-18` (multi-user auth), `R-20` (tenancy), `R-14` / `R-17` (accounts / venues), `R-15`
(removal); engineering §1 (BFF), §2 (data layer), §9 (audit); [ADR-0001](0001-event-backbone.md) (event log),
[ADR-0007](0007-order-execution-model.md) (execution). Data model: [data-dictionary](../data-dictionary.md).

## Context
The product moved from single-operator to **multi-user**: each user **joins by invitation** to create a login and owns an **isolated** set
of prop-firm connections, accounts, rules, risk profiles, suggestions, orders, and journal (R-18 / R-20). The auth
foundation (ADR-0003) was deliberately built **RBAC-ready** — a claims / policy layer, not hard-coded operator
checks — so identity was never the hard part. The hard part is **isolation**: with real money and real broker
credentials per user, one user reaching another's data or orders is a **critical** failure, so isolation must be
**enforced below the UI**, not merely presented.

## Decision
- **The User is the tenant root.** Every **operator-owned** entity carries an **owning `user_id`**; **reference &
  market data** (instruments, venues, data providers, firms, bars / ticks / quotes / DOM, indicators, raw news) is
  **shared / global**. The split is explicit in the [data dictionary](../data-dictionary.md) (Cross-cutting).
- **Isolation is enforced at the data layer, not the UI.** Every query is **scoped to the authenticated user** — via
  **EF Core global query filters** on `user_id` (**default-deny**) plus the authorization policy layer (ADR-0003). A
  UI bug or crafted request **cannot** read or mutate another user's rows; cross-user joins are impossible by
  construction.
- **Per-user broker credentials.** Each user connects their **own** TopstepX / Tradovate logins (Connection is
  per-user, R-17); credentials stay **server-side** (R-18). **No shared broker session** — execution, positions, and
  the kill switch / auto-flatten act only on the acting user's accounts.
- **Invitation-only onboarding + login** (R-18): the operator invites; the invitee **accepts to create an account** (email + credential hashed server-side) — **no open sign-up**; the JWT carries the user
  identity and every request authorizes against it.
- **Safety stays per-user and server-side.** The risk gate (R-5), auto-flatten (R-13), and kill switch (ADR-0007)
  operate within one user's tenancy; a user's kill switch never touches another's positions.
- **Data-provider keys are platform-level (default).** Market data (Finnhub / Tiingo) is a **shared platform**
  capability, not per-user keys; only a user's **watchlist / relevance config** is private. *(Revisit if a user must
  bring their own data entitlement.)*

## Alternatives considered
- **Schema / DB-per-tenant.** Strongest isolation, but heavy for many small single-trader tenants and awkward for
  shared market data + migrations. Rejected; **row-level (`user_id`) scoping with default-deny query filters** fits
  the shape (a modest number of power users, shared market data).
- **UI-only / service-layer filtering.** Rejected outright — one missed filter leaks another user's money data;
  isolation must live at the **query layer** (global filters), defense-in-depth with policies.
- **Shared broker session / pooled credentials.** Rejected — real-money accounts must be per-user and server-side.

## Consequences
**Positive** — real multi-user with strong, enforced isolation reusing the ADR-0003 claims / policy foundation; the
account model (operator → connection → accounts) was already per-user, so tenancy layers on cleanly; shared market
data stays single-copy.
**Negative / costs** — **every operator-owned entity needs `user_id` + a global query filter** (a new entity without
one is a leak — a lint / test guard is warranted); **the event log mixes shared market events with per-user decision
events**, so its tenancy (partition / tag / filter) is an **open design item** (ADR-0001); **cross-user analytics**
(aggregate benchmarks) would need an explicit, privacy-safe path outside per-user scope; **billing / team roles** are
deferred (the policy layer is ready).

## Follow-ups
- Define the **EF Core global query filter** pattern (`user_id`, default-deny) + a **guard** (test / analyzer) that
  every operator-owned entity has one.
- Resolve **event-log tenancy** (ADR-0001): how per-user decision events and shared market events coexist.
- Registration / account lifecycle (email verification, password reset, account deletion → R-15 tombstoning).
- Decide **data-provider entitlement** (shared platform keys vs. bring-your-own) if it ever needs to be per-user.
- **Isolation test suite** — cross-user access attempts must fail (safety-critical; engineering §5 / §9).
