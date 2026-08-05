# ADR-0003: API authentication — JWT, RBAC-ready authorization

**Status:** Accepted · **Date:** 2026-07-18 · **Deciders:** Adam (operator) · *Status history:* extended by [ADR-0011](0011-multi-user-tenancy.md) (multi-user tenancy), itself **superseded by [ADR-0017](0017-single-operator-data-isolation.md)** — the *single-operator* premise below **stands restored**; the RBAC-ready claims/policy design remains the lever that keeps a future read-only login an incremental add
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

## Update (2026-08-05) — the SPA's authenticated client lands (gh#648)

The client half of the bearer decision above shipped, in `src/MarqSpec.TradingCopilot.Client/src/api`: the JWT is
acquired at sign-in and attached as `Authorization: Bearer …` from **one** request path, so a surface cannot issue
an unauthenticated call. The token lives in **`localStorage`** — the *implementation* of the bearer choice, not a
new decision: an httpOnly cookie would trade the header model chosen above for cookie auth + CSRF handling.
`localStorage` is XSS-reachable and this client can send orders, so the exposure rests on the single-origin
premise already decided — a first-party bundle served same-origin by the BFF under a strict CSP, no third-party
script surface. A 401 clears the token and routes to sign-in; it is never retried. This closes the *issuance +
expiry* part of the token follow-up below (refresh remains); the client's request types are generated from the
gh#604 OpenAPI spec, so an API-contract change is a client build failure rather than a runtime 400.

## Update (2026-08-05) — the sign-in / accept-invite surfaces and session land (gh#652)

The operator-facing half of the bearer decision, in `src/MarqSpec.TradingCopilot.Client/src/auth`. The SPA now
boots **through** the R-18 gate: `AuthProvider` establishes the session once on load from `GET /auth/me`,
`RequireAuth` is the single place a signed-out operator is turned away — to `/sign-in`, remembering where they
were headed — and the app-bar account menu is the one sign-out. `/sign-in` and `/accept-invite` are the only
surfaces outside the gate, mirroring the BFF's anonymous allow-list (login + accept-invite), and they render
outside the shell: a credential field never shares a screen with account state, which is why sign-out lives with
the operator's identity in the app bar and not on the sign-in card.

Two properties are carried deliberately from the server. A rejected sign-in reads **identically** whether the
email is unknown or the password wrong — the surface cannot enumerate accounts, matching the endpoint's single
401 — whereas an invitation failure *is* named, because "invalid, already used, or expired" is about the invite,
not about whether some account exists. The accept-invite surface is the dormant-model minimum (redeem the token
the endpoint already accepts; no role selection or management), per ADR-0015 / ADR-0017. This closes the client
side of the **login → JWT** issuance follow-up below; refresh and SignalR access-token wiring remain.

## Follow-ups
- Token **issuance + refresh** flow (login → JWT; refresh strategy; expiry).
- **Signing-key** storage / rotation (server-side secret, §8).
- The **initial policy set** (one operator policy) and the claim(s) future roles will key on.
- SignalR **access-token** wiring.
