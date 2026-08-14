# ADR-0024: Per-credential-set venue client lifetimes — one deployment serves several ProjectX logins

**Status:** Proposed · **Date:** 2026-08-14 · **Deciders:** Adam (operator/maintainer)
**Extends:** [ADR-0016](0016-venue-configuration.md) — makes good on its follow-up *"resolve the venue per account
rather than a process-wide singleton"*; the "adapters compiled in, firms in settings" decision stands.
**Relates to:** [ADR-0015](0015-distribution-licensing-governance.md) (composition-root-agnostic seams),
[ADR-0017](0017-single-operator-data-isolation.md) (one operator, several firm logins — **not** tenancy),
[ADR-0023](0023-venue-setup-contract.md) (the setup contract whose credential schema this scopes per key). PRD
`R-17` (venue abstraction), `R-18` (auth). Issues: `gh#95` (this), `gh#92` (the 409
guard), `gh#91` (env-referenced credentials), `gh#41` / `gh#66` (multi-*venue*, the thing this is **not**).

## Context

A deployment holds exactly **one** ProjectX credential set today. `AddProjectXApiClient` binds
`ProjectX__ApiKey` / `ProjectX__ApiSecret` **once** at DI registration, and the websocket client is a **process
singleton**. The data model already speaks multi-login — `Connection.CredentialKey` names which env entry a firm
login uses, one connection per firm × platform (ADR-0016) — but serving a *second* key is refused: discovery on a
connection whose `CredentialKey` differs from the process's configured key returns **409**, citing the singleton
(the guard that landed in `gh#92`, `ConnectionEndpoints.DiscoverAccountsAsync`).

The practical consequence: **two ProjectX-family firms — say Topstep and another — cannot be served by one
deployment.** ADR-0016 foresaw exactly this in its own follow-up, asking that the venue be *"resolved per account
rather than a process-wide singleton."* The 409 makes the gap loud rather than silently discovering the wrong
login's accounts, which is correct — but it is a stopgap, not the answer.

This is **not** urgent while Topstep is the only wired login; it becomes real the day a second ProjectX-family firm
is onboarded. It is recorded now because the design touches a decision (the singleton) that must be **superseded on
the record, not edited**, and because the setup contract (ADR-0023) is being designed alongside it — the two meet in
`ProjectXVenueFactory` and are cheaper to reconcile together than in sequence.

## Decision

Lift the one-credential-set constraint by making the venue client's lifetime **per credential set**, keyed by
`Connection.CredentialKey`. Most of the work is in the client library (`external/MarqSpec.Client.ProjectX`); the app
side is the factory.

**1. Client pairs are constructed per key, not bound once.** The ProjectX api + websocket clients are built for a
given credential key and **cached per key** for the process lifetime, rather than resolved as DI singletons bound at
registration. The websocket lifecycle becomes **explicit** — connect and dispose per connection, with a named owner
for reconnection — because a process singleton no longer hides it. `AddProjectXApiClient` registers a **factory**,
not a single bound client.

**2. Credentials resolve by key, still env-referenced, still never in the database.** A key `K` resolves its
secret from env entries scoped by that key — `ProjectX__<K>__ApiKey` / `ProjectX__<K>__ApiSecret` — falling back to
the unscoped `ProjectX__ApiKey` form for the single-login case so existing deployments are unchanged. This carries
`gh#91`'s decision forward (no secret in the DB; `Connection.CredentialKey` references the env entry) and scopes
ADR-0023's credential **schema** per login: the contract describes the fields; the key namespaces the values.

**3. `ProjectXVenueFactory` resolves the client pair for the connection's key.** It takes the connection's
`CredentialKey`, builds or returns the cached client pair, and hands it to the adapter. The `gh#92` guard then
**relaxes naturally**: the 409 stops meaning *"this deployment is pinned to one key"* and starts meaning *"no
credentials are configured for key X"* — a configuration error the operator can fix, not an architectural refusal.

**4. This supersedes the process-singleton websocket binding.** The singleton was the right first cut when one
login was the only case; it is what this lifts. Per ADR discipline that is a decision **superseded here**, not
rewritten where it was made. Reconnection ownership, hub-state liveness (the `ProjectXConnection` view, R-13's
orphan-guard input), and disposal all move from *"the process's one client"* to *"this connection's client,"* and
must stay correct per key — a dropped market hub on login A must not read as a drop on login B.

**5. Single-operator is unchanged.** This is several **firm logins** behind **one human** (ADR-0017), never several
humans behind one deployment. Firm records remain per-user data (ADR-0016 §Follow-ups); the per-key cache is keyed
by credential key, and must not reintroduce a process-wide singleton by another name — an account's client is
resolved from its connection, not from a static.

## Alternatives considered

- **Keep the singleton; run one deployment per firm.** Rejected as the *permanent* answer — a self-hosted operator
  with two prop firms would run two full stacks (two databases, two auto-flatten schedulers, two dashboards) to hold
  two API keys. It stays the honest **workaround until this lands**, which is why `gh#92`'s 409 is a clear refusal
  rather than a silent wrong-login discovery.
- **Multi-tenancy — several operators, per-user credentials.** Rejected, and not by omission: ADR-0017 retired it.
  Several people behind one deployment would share the **same broker sessions and credentials**, an account-integrity
  and terms-of-service problem, not an architectural convenience. This ADR serves one human's several logins only.
- **Resolve credentials through the configuration pipeline instead of a keyed factory.** Rejected: a client built
  from an arbitrary `IConfiguration` (ADR-0023 / the `gh#95` client work) cannot assume an environment-variable
  provider is present, and a *per-connection* websocket lifecycle is behaviour, not configuration — a generic config
  binding cannot own connect/dispose/reconnect per key.
- **A single client multiplexing several logins over one connection.** Rejected: ProjectX's user hub authenticates
  **as a login**; one socket cannot be two operators' order streams at once, and conflating them would cross one
  firm's fills into another's account view — the exact isolation R-18 and ADR-0017 exist to hold.

## Consequences

- **One deployment serves several ProjectX-family firms**, which ADR-0016 already modelled in data and only the
  runtime refused.
- **The websocket lifecycle stops being implicit.** Explicit per-connection connect/dispose is more code and more to
  get right (reconnection ownership, liveness per key), but it removes a hidden global whose single-login assumption
  was load-bearing.
- **Most of the change is cross-repo**, in `external/MarqSpec.Client.ProjectX` (per-key client lifetimes, keyed
  credential resolution), with `ProjectXVenueFactory` and the `gh#92` guard as the app-side surface. It is a
  **two-repo card** (per the venue-client pattern) and should be sequenced with ADR-0023's contract work, which
  touches the same factory.
- **`gh#92`'s guard becomes a config-error message rather than an architectural stop** — the same 409, a truer
  reason.
- **The single-login deployment is unchanged**: the unscoped `ProjectX__ApiKey` fallback means an operator with one
  firm sets nothing new.

## Follow-ups

- **Sequence with ADR-0023** (`gh#64`): the setup contract's credential schema and this ADR's per-key resolution
  meet in `ProjectXVenueFactory`; land the contract's shape first, then key its values, so neither reintroduces a
  process-wide singleton (ADR-0016 §Follow-ups).
- **The client-library work is the bulk** and belongs to `gh#95`'s ProjectX-repo half — per-key api + websocket
  client lifetimes, explicit connection lifecycle, keyed credential resolution — with the trading-copilot factory
  and guard as the consuming change.
- **Revisit the `gh#94` firm name-pattern table** on the same trigger this ADR names: the day a second
  ProjectX-family firm is onboarded.
- **Confirm reconnection and orphan-guard semantics per key** — R-13's protection reads market-hub liveness, and
  that signal must now be per connection, not per process.
