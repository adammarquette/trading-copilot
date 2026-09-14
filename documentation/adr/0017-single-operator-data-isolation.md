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

**Multi-tenancy here is not merely unnecessary — it is a liability**, and the reason has to be stated precisely,
because the loose version ("multi-user means shared credentials") is wrong and invites rebuttal: ADR-0011 §Decision
did design *per-user* credentials and **no shared broker session**. The liability is not that multi-user forces
sharing. It is threefold, and it stands even with ADR-0011's isolation built:

1. **Custody.** Hosting several operators means the deployment holds *other people's* broker credentials. Prop
   firms treat that as an account-integrity and terms-of-service problem however well the host isolates them
   internally — the exposure is the custody itself, not a leak between users.
2. **The isolation is unbuilt work, not a config flag.** The venue client is **one credential per process** — the
   websocket client is a singleton today (ADR-0015 §1). Real per-user sessions are a re-architecture; a deployment
   that skips it *falls back* to a shared session, and that is the breach the loose version wrongly attributes to
   multi-user in general.
3. **Auto-flatten liability scales with users.** A host running R-13 on others' behalf owns the consequence when a
   token refresh fails at 2:29 PM CT and someone's funded account breaches (ADR-0015 §2).

The safe answer to all three is one deployment per person. Stating it this way matters because "we could support a
few users" is otherwise an easy and reasonable-sounding thing to re-propose later.

All three, note, are about additional *trading* operators — each needing broker credentials, a venue session, and
auto-flatten run on their behalf. A **read-only** user carries none of them: no credentials, no session, nothing to
flatten. That is a genuinely different shape, and the reason the owning-identity scoping (§3) and the dormant
invitation mechanism (§4) are kept rather than deleted — see Follow-ups.

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

**4. `Invitation` stays, dormant.** Invitation-only onboarding is **not** the product's story — the single
operator is seeded at first start, and there is nobody to invite. But the mechanism is **kept in the codebase**,
undocumented as a product feature, exactly as [ADR-0015](0015-distribution-licensing-governance.md) decided.

> **An earlier draft of this ADR removed `Invitation`, on the stated grounds that the entity, endpoints and
> migration did not exist.** That was **wrong**: `Invitation` and `InvitationStatus`, the `/invitations` and
> `/accept-invite` endpoints, and the `AddInvitations` migration are all built and on `develop`, and the local
> flow runs end to end. ADR-0015's reasoning held — a second login stays possible *"without unwinding a
> migration"* precisely because the migration is real. Dropping it would mean writing a **new** migration to
> drop the table and deleting working code, which is more cost than keeping dormant plumbing, not less.
>
> The asymmetry argument (trivial to keep, painful to retrofit) therefore applies to `Invitation` too, not only
> to the owning-identity column in §3.

**5. Sharing is an artifact, not a feature.** A strategy template — its rules, setups, triggers, defaults, and the
notes attached to them — **exports to a portable JSON file** that another operator imports on their own
deployment. Transport is a gist, an email, a repo; the platform is not in the middle. Two constraints define it
(`gh#3`):

- **The export excludes credentials, account identifiers, journal, positions, fills, P&L and AI-usage** — the
  full exclusion list is `gh#3`'s, and it is normative, not illustrative. A file gets forwarded and posted; the
  blast radius of a mistake here is larger than for anything held server-side.
- **An import arrives inert.** Its rules are enabled as a group by a deliberate action, never silently live —
  the source is now an anonymous file rather than a named peer, and a downloaded artifact must not be able to
  take positions.

## Alternatives considered

- **Keep multi-tenancy (ADR-0011 as written).** Rejected on the custody-and-liability argument above, which is
  the decisive one: the host holds other people's broker credentials and, until per-user sessions are built,
  runs them through one shared venue session — a terms-of-service problem with the broker that no amount of
  row-level isolation inside our database changes, because it is not about what one user can read of another.
  Rejected also as unpaid complexity — billing, roles, cross-user analytics, and an isolation test suite all
  exist to serve users we do not have.
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

## Update (2026-07-23) — invitation issuance restricted to the primary operator (gh#128)
The gh#127 regression suite probed the dormant plumbing and found issuance unrestricted: any authenticated user
— an accepted invitee included — could mint further invitations (chaining ⇒ uncontrolled account creation on a
web-exposed deployment). Fixed per the operator's ruling: `User.IsPrimaryOperator` is **declared at bootstrap
seeding** (never derived from creation order; backfilled on existing deployments by excluding invite-created
users), and `POST /auth/invitations` refuses any non-primary caller with 403. Invite-created users are never
primary — consistent with §4's mentee future, where mentees observe and never invite. The gh#127 chaining probe
is the fix's regression guard.

## Follow-ups

- Define the **EF Core global query filter** pattern (owning identity, default-deny) and a **guard** — test or
  analyzer — that every operator-owned entity has one. Carried forward unchanged from ADR-0011; the mechanism
  survives even though its rationale changed.
- Resolve **event-log tenancy** (ADR-0001): shared market events and operator decision events in one log. Still
  open, and now simpler — the question is partitioning by kind, not by owner.
- Specify the **template export schema** (`gh#3`): version field, lineage representation with no server-side
  identity, re-import semantics, and the reject-vs-warn rules for validation.
- Operator lifecycle: credential rotation and account deletion (R-15 tombstoning). Self-service **registration**
  is dropped (a seeded single operator does not register); **invitation acceptance is dormant, not dropped** (§4).
- **Read-only / mentee users — a plausible future, and the concrete shape a second login would take.** A mentor
  offering tips, or a trader showing a mentee their own profile, needs an *observer* who can read a scoped view
  and place no orders. This carries none of the trading-operator liability above — no broker credentials, no
  venue session, no auto-flatten — and it reuses exactly what this ADR keeps: the owning-identity scoping (§3)
  for the read boundary, and the dormant invitation mechanism (§4) to admit the observer. Not in scope now;
  recorded so the retained machinery has a named purpose rather than reading as dead weight. Would land as its
  own ADR and issue.

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
