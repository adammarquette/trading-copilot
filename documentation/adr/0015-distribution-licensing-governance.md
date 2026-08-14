# ADR-0015: Distribution, licensing & governance — self-hosted, Apache-2.0, maintainer-led

**Status:** Accepted · refined by [ADR-0017](0017-single-operator-data-isolation.md) · **Date:** 2026-07-20 · **Deciders:** Adam (operator/maintainer)
**Relates to:** PRD `R-14` (practice vs. live), `R-17` (venue abstraction), `R-18` (auth), `R-20` (tenancy);
[ADR-0011](0011-multi-user-tenancy.md) (refined here; since **superseded by [ADR-0017](0017-single-operator-data-isolation.md)**), [ADR-0013](0013-failure-recovery-model.md)
(auto-flatten), [ADR-0012](0012-containerization-local-dev.md) (deployment); `README.md`, `CONTRIBUTING.md`.

## Context

Three facts converged, and together they settle how this project is distributed, licensed, and governed.

**1. Broker credentials authenticate one person.** A ProjectX credential is a username plus an API key for a
single TopstepX login, and it exposes that individual's accounts. Confirmed against the live gateway: one
credential, 293 accounts, all belonging to one operator. Hosting many users therefore means **custody of other
people's broker credentials** and one realtime connection set per user — the client's websocket client is a
singleton today, one credential per process.

**2. The liability is worse than the cost.** The infrastructure is merely expensive. The real problem is
**R-13 auto-flatten**: safety-critical, always-on, and the system's one autonomous action. A host running it on
others' behalf owns the consequence when a token refresh fails at 2:29 PM CT and somebody's funded account
breaches. That is an obligation scaling with user count against revenue that does not.

**3. The project's purpose is partly demonstrative.** It exists to do a real job *and* to evidence engineering
capability to prospective employers and business partners. That audience has to be able to read, clone, and run
it without friction — a constraint the licence either serves or defeats.

A fourth fact bears on the licence specifically: **this is an AI-first engineering project.** The great majority
of the source, tests, and documentation was authored by AI coding agents under human direction and review;
comparatively little was hand-written. Copyright in AI-generated work is unsettled — the US Copyright Office has
held that material lacking human authorship is not copyrightable — so the strength of any copyright claim here,
and with it the enforceability of any licence, is genuinely uncertain.

## Decision

- **Self-hosted, fork-first distribution.** Users fork the repository and run their own deployment against their
  own broker credentials. There is no hosted multi-tenant service, and credentials never leave the operator's
  own instance.
- **Authentication and tenancy stay** (R-18, R-20; ADR-0011 is **refined, not superseded**). The API is
  Internet-exposed, so anonymous access is unacceptable regardless of how many people use an instance. Tenancy is
  already built and its cost is sunk; the asymmetry is decisive — removing it later is trivial, retrofitting it
  touches every table and query. An instance is therefore **multi-user *capable*, single-operator by default.**
- **Apache License 2.0.** Permissive, so the intended audience can engage without legal friction; an **express
  patent grant** and an **explicit trademark non-grant**, so the name stays with the maintainer; a `NOTICE` file
  that carries attribution into every fork.
- **Maintainer-led governance.** Contributions are welcome and reviewed on merit and on fit with the direction
  here. The maintainer is the final authority on scope, architecture, and what merges; there is no obligation to
  accept a change, and a declined contribution is not a judgement on its quality. **Forking is a legitimate
  outcome, not a failure** — the licence exists so that it is.
- **Component seams are a product property, not an implementation detail.** The venue abstraction (R-17) and the
  other seams exist so a fork can swap in its own broker, data provider, or LLM without touching the core. This
  constrains design: new integrations go behind an interface, never inline.
- **AI-first authorship is disclosed**, in `NOTICE` and the README. It is a fact about the work and, given this
  audience, part of what the work demonstrates.

## Alternatives considered

- **Hosted multi-tenant SaaS.** Rejected. It requires custody of other people's broker credentials and makes the
  host answerable for other people's auto-flatten. The engineering is tractable; the liability is the objection.
- **AGPL-3.0** (briefly adopted, 2026-07-20, superseded same day). Chosen while the goal was retaining commercial
  control, and wrong once the goal became demonstrating capability to employers and partners. Many organisations
  ban AGPL outright — the very people meant to evaluate the work may be unable to clone it — and its protection
  here is largely theoretical, since a trader self-hosting for personal use never triggers the network clause.
  A deterrent to the intended audience in exchange for a benefit that mostly does not arise. The uncertain
  copyright position under AI authorship weakens a restrictive licence further: it asks more, and there is less
  standing to enforce it.
- **MIT.** Viable, and the closest runner-up. Apache-2.0 preferred for the express patent grant and explicit
  trademark handling — meaningful for software that executes financial transactions, and better signal for the
  audience.
- **Proprietary / source-available (BUSL, PolyForm).** Rejected. Directly contradicts both the fork-and-run
  distribution model and the demonstrative purpose.
- **Governance by formal committee or open commit bit.** Rejected as premature for a single-maintainer project.
  Revisit if a real contributor community forms.

## Consequences

**Positive**
- No custody of third-party credentials, and no responsibility for anyone else's safety-critical flatten.
- The audience that matters can read, clone, run, and adopt with no legal review.
- Attribution travels with every fork via `NOTICE` — which is the durable value of a demonstrative project.
- Tenancy retained cheaply, leaving a small-group deployment open without committing to hosting for strangers.
- Swappable seams give a fork a supported path to diverge, which makes forking healthy rather than hostile.

**Negative / costs**
- **Adoption friction.** Most futures traders will not run Docker and operate a Postgres. Self-hosting trades
  operational liability for a support burden of "it will not start on my machine."
- **A permissive licence permits closed commercial forks.** Accepted: for a demonstrative project a commercial
  fork is a reference, not a loss.
- **No revenue path is preserved by the licence.** Dual-licensing leverage is given up deliberately; any future
  commercial offering would rest on hosting, support, or a separate product rather than on licence terms.
- **Disclosing AI authorship invites scepticism** from some readers. Accepted as the honest position, and the
  test suites, ADR trail, and traceability are the counter-evidence.

## Update (2026-07-20) — narrowed to one operator per deployment

The decision above said *multi-user **capable**, single-operator by default*. That is now firmer: **one
deployment, one operator.** Authentication exists because the deployment is **reachable from the web**, not
because the app serves a user base.

What follows:

- **Authentication stays** (R-18), unchanged. A web-exposed trading system without it is indefensible.
- **Tenancy stays** (R-20), reframed. The per-user scoping is a **default-deny safety property** rather than a
  multi-tenant feature: a query that forgets its scope returns *nothing* instead of *everything*. It is built and
  tested, costs nothing to keep, and would be painful to retrofit.
- **Invitation-only onboarding is no longer the product's story.** The endpoints, entity, and migration **remain
  in the codebase** — dormant, undocumented as a product feature — so a second login on one instance stays
  possible without unwinding a migration. The documented path is: the operator's account is seeded at first
  start, then they log in.
- **AI spend is simply the operator's own.** The prior framing — one shared LLM account funding many users, spend
  hidden from "end users" — no longer describes anything. Spend lives in Grafana because it is a running-cost
  question rather than a trading decision, not because it is being withheld from someone.

**The earlier ADRs are deliberately not rewritten.** [0003](0003-authentication.md),
[0008](0008-ai-invocation-cost-model.md), [0011](0011-multi-user-tenancy.md),
[0013](0013-failure-recovery-model.md), and [0014](0014-news-importance-feedback.md) carry multi-user framing and
keep it: an accepted ADR is an immutable record, superseded by a later one rather than edited (see the
[ADR index](README.md)). They remain accurate about *why* those decisions were taken; this ADR narrows the
deployment model they assumed. `0011`'s tenancy mechanism in particular is still exactly what ships.

## Follow-ups

- Reconcile the **PRD's pervasive "multi-user" framing** with "multi-user capable, single-operator deployment"
  (gh#57). The requirements themselves do not change; the framing around them does.
- Decide whether an instance ever supports **several operators with distinct broker credentials**. That needs
  per-user credential storage and a **per-user venue client lifetime** — the websocket client is a singleton in
  the current client library. Not required by the decision above; do not foreclose it.
- Keep **S3/S4 composition-root-agnostic**: resolve the venue per account rather than injecting a process-wide
  singleton, so both deployment shapes stay reachable at no present cost. — **Taken up by
  [ADR-0024](0024-per-credential-set-venue-clients.md)** (gh#95): per-`CredentialKey` client lifetimes that
  supersede the singleton binding this bullet flags.
- **Liability wording is not settled by a licence.** The Apache disclaimer covers software warranty, not
  another person's trading losses. The README disclaimer and the human-in-the-loop design carry that weight; take
  legal advice before any commercial offering.
