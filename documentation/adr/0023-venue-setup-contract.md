# ADR-0023: The venue setup contract — a compiled-in adapter declares its own onboarding, discovery stays compile-time

**Status:** Accepted · **Date:** 2026-08-14 · **Deciders:** Adam (operator/maintainer)
**Extends:** [ADR-0016](0016-venue-configuration.md) — resolves its deferred plugin contract (`gh#64`); the "adapters
are compiled in, firms are configured in settings" decision stands unchanged. **Relates to:**
[ADR-0015](0015-distribution-licensing-governance.md) (fork-first distribution),
[ADR-0007](0007-order-execution-model.md) (the gate that stays the enforcement backstop); PRD `R-17` (venue
abstraction), `R-14` (practice vs. live), `R-18` (auth), `Q-14` (capability matrix). Issues: `gh#64` (this
contract), `gh#41` (Tradovate — the first implementation), `gh#60` (mode declaration), `gh#61` (firm registry
wireframe).

## Context

ADR-0016 settled *where a venue's setup lives* — **adapters are compiled in, firms are configured in settings** —
and deliberately **deferred the descriptor/plugin contract to `gh#64`**, because a hand-written credential form per
adapter is "cheap at two or three adapters" and a descriptor only earns its keep "once adapters come from outside
this repo." It named the trigger to revisit precisely: *when the hand-written forms become the bottleneck, or a
third-party adapter is a real prospect.*

Two things now reach that trigger, and one clears up a confusion the word "plugin" invites.

**The fixed onboarding form cannot serve the second adapter.** ProjectX takes **2** credential fields; Tradovate
(`gh#41`, the next adapter, and the first real test of any contract) takes **7** — username, password, appId,
appVersion, deviceId, cid, secret. The firm-registry wireframe (`gh#61`) shows a fixed *Username / API key* form; it
cannot render both, and hand-writing a bespoke form per adapter in the UI is the friction ADR-0016 said would be the
signal to act.

**A credential's *name* is folklore today, and folklore is a trap on a live account.** ProjectX's two fields do not
mean what they are called — from `.env.example`, `ProjectX__ApiKey` is the TopstepX **username** (sent as
`"username"`) and `ProjectX__ApiSecret` is the **API key** (sent as `"apikey"`) — and getting it wrong fails as a
`200` carrying `success=false`, surfaced as *"Authentication failed: Unknown error."* That labelling lives in an env
comment the operator never sees.

**"Plugin" must not be read as "dynamically loaded."** `gh#64`'s own analysis (preserved on ADR-0016) already
answers the discovery question: a venue participates in order placement and **auto-flatten (R-13)**, and .NET offers
no isolation boundary worth trusting for third-party code in that process — `AssemblyLoadContext` is a versioning
mechanism, not a security one. The value `gh#64` actually wants is **self-description** (an adapter declaring the
information needed to *offer* it), not runtime loading.

## Decision

Adopt the **venue setup contract** — the descriptor ADR-0016 deferred — as a **compile-time, self-declared** part of
every adapter. `ITradingVenue` / `VenueCapabilities` remain the runtime half; this adds the setup-time half.

**1. A compiled-in adapter declares an `IVenueSetupContract`.** Alongside its runtime contract it declares, as data:

- **Identity** — `VenueId`, a display name.
- **A credential schema** — an *ordered* list of fields, each carrying `key`, a human **label**, `secret` (masked,
  never logged, never returned), `required`, and **help text**. This is the field that fixes the 2-vs-7 problem and
  turns the ProjectX naming trap into a label the operator reads at the point of entry.
- **An endpoint model** — fixed host, **per-firm** host (ProjectX: firms run their own branded hosts), or a
  **demo/live pair** the adapter already knows (Tradovate).
- **A mode-reporting mechanism** — how the adapter surfaces practice-vs-live (a flag on the account, a host split, or
  *does not report it*). This is the `gh#60` input: the plugin declares the *mechanism*; the operator declares what
  it **means** per firm (`FirmConventions`, ADR-0016 §5). It never declares the *mode itself* — R-14 stays operator
  truth.
- **Its capabilities** — `VenueCapabilities`, already declared today.

**The onboarding UI is generated from this contract**, not hand-written per adapter. Adding a firm stays settings
(ADR-0016); adding a platform stays code; but the *form* for that platform is now the adapter's declaration rather
than a bespoke component.

**2. Discovery stays compile-time — the plugin self-describes, it is never dynamically loaded.** Contracts are
declared by adapters **compiled into the image** and registered at the composition root, resolved by `VenueId`. No
`AssemblyLoadContext`, no manifest-driven runtime loading, no third-party assembly executing in the process that
places orders and runs auto-flatten (R-13). A fork adds a platform the way ADR-0015 already expects — write the
adapter and its contract, add the project reference, rebuild — and the UI knows every contract because every adapter
is present at build time.

**3. Capabilities gate what a contract may declare, and the gate stays the backstop.** A plugin cannot declare
itself into a capability it does not implement, and — the load-bearing half — **a declaration is never an
enforcement point.** A data-only provider declares no execution capability and no account credentials; the R-5/R-11
gate (ADR-0007) refuses any order path regardless of what a contract claims. Enforcement lives below the model and
below the plugin: a venue that *declared* `CanPlaceOrders` it cannot honour fails at the gate and the venue seam,
never by trusting the claim.

**4. The contract is a published, versioned interface.** Once a fork's adapter depends on it, breaking it breaks
their adapter. It therefore carries a version, and a **breaking change is itself an ADR** (supersede, not edit) —
the same immutability the ADR trail has.

**5. Data-only providers declare a narrower shape.** Finnhub and Tiingo have no accounts, no credentials in the
account sense, and no execution capability. They declare the **market-data slice** (identity, endpoint, data
capabilities) rather than an execution contract with every account/credential field marked optional — an
optional-everything contract documents nothing and lets a data provider *look* like it could be onboarded for
trading.

## Alternatives considered

- **Runtime plugin loading** (`AssemblyLoadContext` / a manifest, genuine third-party plugins). Rejected — the crux,
  and unchanged from ADR-0016: third-party code in the order-placing, auto-flattening process is a security exposure
  .NET cannot isolate. Fork-and-rebuild (ADR-0015) is the sanctioned extension path, and it keeps every line of a
  venue that can move money reviewable before it ships.
- **Keep hand-writing a per-adapter credential form indefinitely** (ADR-0016 §3, extended). Rejected now, not in
  principle: Tradovate's 7 fields against ProjectX's 2, plus the naming trap, is exactly the "forms become the
  bottleneck" trigger ADR-0016 named. Designing the contract *while* building the second adapter (`gh#41`) is
  cheaper than retrofitting it after.
- **A fully data-driven venue** — endpoints and auth as config executed by a generic driver. Rejected on ADR-0016's
  evidence: every venue met so far needed real code (ProjectX is two SignalR hubs; Tradovate a bespoke frame
  protocol). The *setup* is data; the *runtime* is not.
- **One contract for execution and data venues.** Rejected — see decision 5; a shared shape makes every
  execution-only field optional and stops meaning anything.

## Consequences

- **Onboarding is generated from the adapter, and the ProjectX naming trap becomes a field label** where the person
  entering it will read it — the concrete win `gh#64` opened on.
- **Tradovate (`gh#41`) is the first implementation and the real test** of whether the contract's shape is right —
  the 7-field, demo/live-pair case the 2-field ProjectX case cannot validate alone.
- **Discovery is settled as compile-time, on the record**, so a future "load plugins at runtime" proposal meets a
  decision rather than an empty space — and must supersede this to win.
- **Forks get a published contract** to implement, at the cost that it is now an interface with versioning
  obligations.
- **The gate remains the only enforcement point.** A plugin declaring a capability changes what the UI offers, never
  what the venue is permitted to do — the descriptor is presentation and configuration, never policy.
- **The credential *store* is unchanged.** ADR-0016's status note recorded credentials landing as an env-entry
  reference (`Connection.CredentialKey`, no secret in the DB); this contract describes the *schema* of those fields,
  not where the secret rests, and `gh#95` still owns the one-credential-set-per-process constraint.

## Follow-ups

- **Implement `IVenueSetupContract` with ProjectX and Tradovate as the first two** (`gh#41`), and generate the
  firm-registry credential form (`gh#61` / `gh#60`) from it — greying platforms whose adapter is absent (ADR-0016
  §4).
- **Decide the versioning policy concretely** (semver on the contract assembly; what a fork is promised across a
  minor bump) when the first external adapter is a real prospect, not before.
- **Confirm whether the mode-reporting mechanism subsumes or complements `FirmConventions`** (`gh#60`) once both are
  expressed as data — the declaration is the mechanism, the firm record is the meaning, and the boundary wants one
  owner.
- **`gh#95`** (one-credential-set-per-process) is orthogonal but adjacent: per-key client lifetimes and this
  contract's per-firm endpoint model will meet in `ProjectXVenueFactory`; sequence them so neither reintroduces a
  process-wide singleton (ADR-0016 §Follow-ups).
