# ADR-0017: One operator per deployment — data isolation as a safety property, not tenancy

**Status:** Accepted · **Date:** 2026-07-21 · **Deciders:** Adam (operator/maintainer)
**Supersedes:** [ADR-0011](0011-multi-user-tenancy.md) (multi-user tenancy & data isolation) — its **data-layer
mechanism is kept**, its **multi-user premise is not**. Restores the single-operator premise of
[ADR-0003](0003-authentication.md), whose claims/policy layer is retained unchanged.
**Relates to:** PRD `R-18` (auth), `R-20` (data isolation), `R-14` / `R-17` (accounts / venues), `R-21`
(templates); [ADR-0015](0015-distribution-licensing-governance.md) (self-hosted, fork-first distribution),
[ADR-0016](0016-venue-configuration.md) (firm configuration), [ADR-0001](0001-event-backbone.md) (event log).
Issues: `gh#77` (this retirement), `gh#3` (template export/import), `gh#8` (auth), `gh#4` (`Invitation`).

## Context

ADR-0011 moved the product from single-operator to **multi-user**: invitation-only onboarding, a `User` as tenant
root, per-user broker connections. ADR-0015 then settled the opposite direction — the platform is **self-hosted
and fork-first**, one deployment per person. The two positions have been live in the doc set simultaneously,
with ADR-0003 marked as superseded by a premise the project had already reversed.

**Multi-tenancy here is not merely unnecessary — it is a liability.** Several people behind one deployment would
sit behind the *same broker and prop-firm credentials* and the *same REST and WebSocket sessions*. Prop firms
treat account and credential sharing as a breach of their terms; this is an account-integrity and plausibly a
legal exposure, not an architectural preference. It is worth stating plainly, because "we could support a few
users" is otherwise an easy and reasonable-sounding thing to re-propose later.

What ADR-0011 got right is the **mechanism**. Row-level scoping with default-deny query filters is valuable on a
single-operator deployment too, for a different reason than tenancy: a query that forgets its scope returns
*nothing* rather than *everything*. That is a fail-closed property on a system that holds order and position
data, and it is cheap to keep.

The remaining question ADR-0011 answered — how one operator shares a strategy with another — is answered
differently here, and the new answer is a better fit for a self-hosted project.

## Decision

**1. One operator per deployment.** A second person means a **second deployment**, with their own credentials.
Fork-first distribution (ADR-0015) already makes that cheap. There is no user directory, no invitation flow, and
no open sign-up; the operator is provisioned at deploy time.

**2. The login exists for web security, not to separate users.** R-18 stands: the API is internet-reachable, so
every request and connection carries a JWT and anonymous access is refused. The **claims/policy layer from
ADR-0003 is kept** — not for RBAC we intend to build, but because it keeps a second login on an instance a
possible future without reworking the data layer.

**3. Data isolation is kept, and reframed as a safety property (R-20).** Every operator-owned row still carries an
owning identity, and every query is still scoped to the authenticated one via **EF Core global query filters
(default-deny)**. The justification is fail-closed behaviour, not tenancy. Reference and market data
(instruments, venues, providers, bars/ticks/quotes, raw news) stays **shared / global**.

**4. `Invitation` is removed.** Single-use, email-bound, issue/accept/revoke — there is nobody to invite. The
entity and its ERD edge come out of the data dictionary (`gh#4`).

> **This departs from [ADR-0015](0015-distribution-licensing-governance.md)**, which kept invitation-only
> onboarding *"dormant, undocumented as a product feature"* so that *"a second login on one instance stays
> possible without unwinding a migration."* That reasoning assumed *"the endpoints, entity, and migration remain
> **in the codebase**"* — but none of them exist. The data layer is unbuilt; the data dictionary is a design-time
> model. There is no migration to unwind, so keeping `Invitation` buys nothing and instead leaves a specified
> entity nobody intends to build.
>
> The asymmetry argument ADR-0015 relied on — trivial to remove later, painful to retrofit — is real, and it
> applies to the **owning-identity column and its query filters**, which §3 keeps for exactly that reason.
> It does not apply to `Invitation`, which is onboarding UX and re-specifiable in an afternoon.

**5. Sharing is an artifact, not a feature.** A strategy template — its rules, setups, triggers, defaults, and the
notes attached to them — **exports to a portable JSON file** that another operator imports on their own
deployment. Transport is a gist, an email, a repo; the platform is not in the middle. Two constraints define it
(`gh#3`):

- **The export excludes credentials, account identifiers, journal, positions, fills and P&L.** A file gets
  forwarded and posted; the blast radius of a mistake here is larger than for anything held server-side.
- **An import arrives inert.** Its rules are enabled as a group by a deliberate action, never silently live —
  the source is now an anonymous file rather than a named peer, and a downloaded artifact must not be able to
  take positions.

## Alternatives considered

- **Keep multi-tenancy (ADR-0011 as written).** Rejected on the credential argument above, which is the decisive
  one: shared venue sessions across users is a terms-of-service problem with the broker, and no amount of
  row-level isolation inside our database changes what the venue sees. Rejected also as unpaid complexity —
  billing, roles, cross-user analytics, and an isolation test suite all exist to serve users we do not have.
- **Drop scoping entirely along with tenancy.** Tempting, and rejected: the `user_id` column and default-deny
  filters cost almost nothing and convert a forgotten `.Where(...)` from "returns every row" into "returns none".
  A single-operator system still benefits from failing closed, and dropping the column would make a second login
  a data-layer migration rather than a configuration change.
- **In-app peer-to-peer sharing (ADR-0011 / the original `gh#3`).** Rejected as a consequence of point 1 — there
  is no second user in the instance to share with. A user directory, invite/accept, and revocation would all be
  server-side machinery in service of what a file already does.
- **Public template gallery hosted by the project.** Out of scope and against the grain of ADR-0015: it would
  make the maintainer a service operator with moderation duties. Files people publish themselves get the same
  outcome with none of that.

## Consequences

**Positive**
- The doc set states one position. ADR-0003's single-operator premise is no longer marked as superseded by a
  decision that was itself reversed.
- The venue seam gets simpler: one set of credentials per firm, per deployment (ADR-0016), with no question of
  whose session an order was placed on.
- Safety machinery loses a dimension. The risk gate (R-5), auto-flatten (R-13) and kill switch (ADR-0007) act on
  *the* operator's accounts — there is no "whose kill switch" question, which was a real failure mode.
- Sharing becomes portable across deployments and versions, which peer-to-peer sharing never would have been.

**Negative / costs**
- **The `user_id` column and its filters are kept without their original justification**, which will read as
  over-engineering to anyone who has not read this ADR. §3 above is the reason; the guard below preserves it.
- **`gh#3` needs a schema and a version discipline** — an export format is a compatibility surface, and imports
  must fail loudly on an unknown version rather than half-apply.
- **Import validation is now a security boundary.** Treating an imported file as untrusted input is easy to state
  and easy to forget; it needs tests, not just a note.
- **Cross-operator benchmarks are gone** as a product capability. Whether that mattered was never established.

## Follow-ups

- Define the **EF Core global query filter** pattern (owning identity, default-deny) and a **guard** — test or
  analyzer — that every operator-owned entity has one. Carried forward unchanged from ADR-0011; the mechanism
  survives even though its rationale changed.
- Resolve **event-log tenancy** (ADR-0001): shared market events and operator decision events in one log. Still
  open, and now simpler — the question is partitioning by kind, not by owner.
- Specify the **template export schema** (`gh#3`): version field, lineage representation with no server-side
  identity, re-import semantics, and the reject-vs-warn rules for validation.
- Operator lifecycle: credential rotation and account deletion (R-15 tombstoning). Registration, email
  verification and invitation acceptance are **dropped**, not deferred.

### Follow-ups this ADR closes

- **ADR-0015: *"Decide whether an instance ever supports several operators with distinct broker credentials."***
  **Answered: no.** Not on the cost of per-user credential storage and per-user venue client lifetimes — which
  was the framing — but on the venue's terms. That is a firmer answer than the engineering one, and it does not
  soften as the client library improves.
- **ADR-0015: *"Reconcile the PRD's pervasive multi-user framing"*** (`gh#57`). Done in the PR carrying this ADR:
  the PRD, data dictionary, ERD, architecture and wireframes now state one position.

**Not** closed, and deliberately: ADRs [0008](0008-ai-invocation-cost-model.md),
[0013](0013-failure-recovery-model.md) and [0014](0014-news-importance-feedback.md) still contain incidental
multi-user phrasing. Per ADR-0015's own rule — *"an accepted ADR is an immutable record, superseded by a later
one rather than edited"* — their text stands. This ADR narrows the deployment model they assumed; none of their
decisions turn on it.
