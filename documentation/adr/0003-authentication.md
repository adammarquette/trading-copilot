# ADR-0003: API authentication — JWT, RBAC-ready authorization

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator) · **Premise restored by [ADR-0017](0017-single-operator-data-isolation.md)** — [ADR-0011](0011-multi-user-tenancy.md) briefly superseded the *single-operator* premise below with multi-user tenancy; that reversal is itself superseded, so **single-operator stands as written**. The RBAC-ready claims/policy design is retained either way — now because it keeps a second login possible without reworking the data layer, not because multi-user is planned.
**Relates to:** PRD `R-18`, [engineering](../trading-platform-engineering.md) §2, [architecture](../trading-platform-architecture.md), `R-11` / `R-16` (execution / kill switch).

## Context
The frontend is a **React SPA** consuming the BFF's **REST** endpoints and **SignalR** hubs — and it is the component **possibly exposed on the Internet**, so it needs authentication. Constraints:
- **Single-operator now** — no tenancy, no billing, no user directory.
- **RBAC-ready** — roles/permissions may be wanted later; the design must allow adding them **without a major overhaul**.
- The **execution / kill-switch / account** endpoints must never have an unauthenticated path.

## Decision
- **JWT bearer authentication** on the **REST API and the SignalR connection** (SignalR carries the token via `access_token` on negotiate/websocket; REST via the `Authorization` header). Tokens are verified server-side; the signing key is a server-side secret (§8).
- **Bearer tokens, not cookies** — the SPA holds a token and sends it explicitly, sidestepping cross-site-cookie / CSRF concerns for the single-origin client.
- **Claims / policy-based authorization from day one.** Authorization goes through ASP.NET Core **authorization policies** keyed on JWT **claims**, *not* hard-coded single-operator checks. v1 has effectively one policy (the operator); adding RBAC later means **adding roles/claims + policies**, not rewriting the model — the "no overhaul" lever.
- **One gate for everything sensitive.** Execution, kill switch, and account endpoints sit behind the same auth (R-18) — no unauthenticated path to order actions.

## Alternatives considered
- **Session cookies.** Work, but bring cross-site-cookie / CSRF handling for an SPA and map less cleanly to SignalR; bearer JWT is simpler for a token-carrying SPA.
- **API key / shared secret.** Simplest, but no user/claims model to grow into roles — a dead end for the RBAC-ready goal.
- **Full RBAC now.** Premature for a single operator; the policy-layer approach gets the readiness without the cost.
- **No auth (deployment-only security).** Unacceptable once the API is Internet-exposed.

## Consequences
**Positive**
- The Internet-exposed API is authenticated; the execution path has no anonymous route.
- **RBAC is an incremental add** (roles/claims + policies), not a rewrite — the design goal, met.
- Bearer tokens fit the single-origin SPA and avoid cookie/CSRF complexity; one token secures REST + SignalR.

**Negative / costs**
- We build **token issuance + refresh** and **signing-key management** ourselves (server-side secret, rotation).
- Discipline required: authorization must *always* go through policies/claims — a stray hard-coded `if (isOperator)` erodes the RBAC-readiness.
- SignalR needs the token wired through the connection (`access_token` query on negotiate/websocket).

## Follow-ups
- Token **issuance + refresh** flow (login → JWT; refresh strategy; expiry).
- **Signing-key** storage / rotation (server-side secret, §8).
- The **initial policy set** (one operator policy) and the claim(s) future roles will key on.
- SignalR **access-token** wiring.
