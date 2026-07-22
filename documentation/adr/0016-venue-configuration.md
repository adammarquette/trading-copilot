# ADR-0016: Venue configuration — adapters are compiled in, firms are configured in settings

**Status:** Accepted (2026-07-22 — implemented; see the *Status note* below · proposed 2026-07-20) · **Date:** 2026-07-20 · **Deciders:** Adam (operator/maintainer)
**Relates to:** PRD `R-17` (venue abstraction), `R-14` (practice vs. live), `R-18` (auth), `Q-14` (capability
matrix); [ADR-0015](0015-distribution-licensing-governance.md) (fork-first distribution),
[ADR-0007](0007-order-execution-model.md) (execution). Issues: `gh#64` (the deferred plugin contract), `gh#60`
(mode declaration), `gh#41` (Tradovate).

## Context

`ITradingVenue` (R-17) is the runtime contract, and `VenueCapabilities` already lets an adapter describe what it
grants. What has never been settled is **where a venue's setup information lives** — and the wireframes had been
assuming it was hardcoded.

Two things forced the question.

**Firms are not platforms, and the relationship is one-to-many.** Apex offers **Tradovate and Rithmic**; Topstep
**owns** ProjectX and offers only that. A login is therefore per **firm × platform**, and the firm decides which
platforms are even on offer (`gh#60`).

**Firms on the same platform differ by endpoint.** From `.env.example`: *"other ProjectX firms run their own
branded hosts."* Connecting to Apex-on-ProjectX rather than TopstepX is a **base URL**, not code.

That is the insight this ADR turns on: **adding a *firm* is configuration; adding a *platform* is code.** The
wireframes conflated them, offering a fixed platform list with no account of where it came from.

A fuller answer was drafted — a descriptor contract in which each venue declares its own credential schema,
endpoint model, and mode-reporting mechanism, so setup UI is generated rather than written. It is a good design
and it is **deferred** (`gh#64`), because it solves a problem this project does not have yet: third parties
shipping adapters. With every adapter compiled in, the UI already knows all of them.

## Decision

**1. Adapters are compiled in.** Project references, registered into DI at the composition root, resolved by
`VenueId`. Adding a *platform* means writing the adapter, adding the reference, and rebuilding — consistent with
ADR-0015, where forking is the expected path. No runtime assembly loading.

**2. Firms are configured in settings, against a compiled adapter.** A firm carries:

- a **name** (Apex Trader Funding),
- **which of the available adapters** it offers — one or more,
- an **endpoint** per adapter, where that adapter takes one (ProjectX: the firm's branded host; Tradovate:
  demo/live is a host the adapter already knows),
- **credentials**, held server-side per R-18,
- its **stage conventions** — what Evaluation and Funded mean *at this firm* (`gh#60`).

No code is needed to add a firm, which is the common case: new prop firms appear far more often than new
platforms.

**3. The credential form is shaped per adapter, in the UI.** ProjectX needs 2 fields, Tradovate needs 7, and
ProjectX's are misleadingly named — `ApiKey` is the *username*. The UI knows both adapters because both are
compiled in, so it can render each correctly and label the ProjectX trap in the field itself rather than in an
env-file comment. This is the part a descriptor contract would generalise; hand-writing it is cheap at two or
three adapters and is the explicit trade being made.

**4. A platform with no adapter is shown, and shown as unavailable.** Rithmic appears in the firm's platform
list greyed, labelled as needing an adapter. Hiding it would misrepresent what the firm offers; enabling it
would promise something settings cannot deliver.

**5. Conventions attach to the firm, not the platform.** An Apex evaluation is the same fee, the same pass/fail
and the same trailing drawdown whether reached over Tradovate or Rithmic. Only the *reporting* differs. Deriving
stake from the platform would give one account two answers (`gh#60`).

## Alternatives considered

- **The full plugin/descriptor contract** (`gh#64`). Each venue declares its credential schema, endpoint model,
  and mode-reporting mechanism; setup UI is generated. Deferred, not rejected — it is the right answer *once
  adapters come from outside this repo*, and premature while they do not. Its analysis is preserved on `gh#64`,
  including the reason discovery should stay compile-time: a venue plugin places orders and participates in
  **auto-flatten (R-13)**, and .NET offers no isolation boundary worth trusting for third-party code in that
  process — `AssemblyLoadContext` is a versioning mechanism, not a security one.
- **Clients declared entirely as configuration** — endpoints and auth as data, executed by a generic driver.
  Rejected on the evidence: every venue met so far needed real code. ProjectX is two SignalR hubs; Tradovate is
  a bespoke frame protocol (`o`/`a`/`h`/`c`) with client-driven heartbeats and manual request correlation. A
  driver general enough to express both would be a programming language with extra steps.
- **Keep hardcoding firms as well as platforms.** Rejected. New prop firms appear constantly, and requiring a
  rebuild to add one — when the difference is a name and a base URL — is friction with nothing behind it.
- **Hide unavailable platforms.** Rejected under point 4: a firm's offering is a fact about the firm, not about
  this deployment's build.

## Consequences

- **Adding a firm is settings; adding a platform is code.** A clean line, and one the UI must state plainly so
  nobody waits for a Rithmic option that only a fork can deliver.
- **The credential form is written per adapter and does not scale.** At two or three adapters that is cheap. The
  point at which it stops being cheap is the trigger to revisit `gh#64` — this ADR is the reason that issue
  stays open rather than closed.
- **The ProjectX naming trap moves into a field label**, where the person entering it will read it, instead of
  an env comment they will not.
- **`gh#60` is unblocked without waiting on a contract**, and its domain half has landed. The adapter reports
  its mechanism; the operator declares the meaning per firm (`FirmConventions`); an undeclared stage resolves to
  `TradingMode.Undeclared` and is refused in **every** environment — stricter than *at risk*, which production
  still permits. What this ADR still owes `gh#60` is the configuration surface that carries the declaration:
  until it exists, every account reads `Undeclared` and none are tradeable.
- **Nothing forecloses `gh#64`.** Compiled adapters, firm records, and per-adapter forms are all things a
  descriptor would later *generate* rather than replace.

## Follow-ups

- Wire the **firm registry wireframe** (`gh#60`) to this: endpoint field per adapter, credential form shaped by
  the chosen adapter, unavailable platforms shown greyed.
- **`gh#64` stays open as backlog**, with the descriptor analysis intact. Revisit when either a third-party
  adapter is a real prospect or the hand-written credential forms become the bottleneck.
- Decide how **data-only providers** (Finnhub, Tiingo) are configured — same firm-and-endpoint shape, or their
  own, since they have no accounts and no credentials in the same sense.
- ADR-0015 asks that S3/S4 stay **composition-root-agnostic** — resolve the venue per account rather than a
  process-wide singleton. Firm records are per-user data, so this must not reintroduce one.

## Status note — accepted as implemented (2026-07-22)

The decision merged substantially as written (gh#76, PRs #86–#94): firm records with per-stage conventions and a
`/firms` configuration surface; one login per firm × platform (`Connection`); discovery persisting each account's
resolved stage with a per-account operator override; modes computed from the declared conventions and refused
while `Undeclared`. One deliberate divergence, recorded in the data dictionary: **credentials landed as an
env-entry reference** (`Connection.CredentialKey` — no secret stored) rather than §2's UI-entered server-side
store, which stays a later increment (`gh#95` tracks the one-credential-set-per-process constraint that implies).
The consequence above — "until it exists, every account reads `Undeclared`" — is therefore historical: the
configuration surface exists.
