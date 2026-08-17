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

## Update (2026-08-05) — two corrections from the #677 review (gh#685)

The #648 client reached develop stacked under #652; the #677 review caught two things worth recording here,
since this ADR is where the client's error model and the "one attach path" claim live.

- **`refused` is now gated on a 4xx status, not just an `{ error }` body.** The refused-vs-`failed` split (R-11)
  is the client's reason for existing, and it was inverted for an unexpected status: a 5xx that happened to carry
  a JSON `{ error }` (an unhandled exception / ProblemDetails response does) was classified as an authoritative
  `refused` — rendered as "the gate said no," never retried — for a request the gate may never have evaluated. A
  5xx is never a gate answer; it is now always a `failed` (retry meaningful).
- **The "one JWT-attach path" is now enforced, not asserted.** #648's acceptance asked that a bypassing call
  "does not compile or is caught by a test"; it rested on a doc comment. A lint rule now fails any `fetch` outside
  `api/client.ts` — bare `fetch(...)`, or the `window`/`globalThis`/`self` `.fetch(...)` member forms that
  resolve to the same global — with the anonymous `/health` probe the one allow-listed exception; a new one is a
  deliberate, reviewable edit to that list.

## Update (2026-08-17) — a 2xx is not on its own a success (gh#951, gh#954)

Two defects of one family, recorded together because the second is what proved the first was a class rather than
an instance. Both were **read-surface liveness** failures, and both were invisible to the type system: the client
declares what a 2xx body *will* be, and neither case is a lie the compiler can see.

- **A 2xx whose body does not parse is a `failed` read (gh#951).** `request()` wrapped only the `fetch` call in
  try/catch; the body read — `JSON.parse` — sat outside it, so a `200` carrying an HTML error page or a truncated
  body threw *after* the promise had been handed to the caller. Every read surface consumes this as
  `void getX().then(...)` with no `.catch`, so the rejection was unhandled and the surface sat on its
  `LoadingState` **forever**, with no error and no retry. This was not a new contract — `failed` already named
  "an unparseable body" as one of its cases, and the 4xx path already behaved this way via `readRefusal` — the
  2xx path was simply the asymmetry.
- **A 2xx carrying no usable token is a `failed` sign-in (gh#954).** An **empty** body does not fail to parse: it
  is the legitimate 204 path, returning `undefined`, so gh#951's guard never fires. `signIn` / `acceptInvite`
  dereferenced it as `result.data.token`, throwing `TypeError` out of `SignInPage.onSubmit`'s un-caught `await`,
  so `setSubmitting(false)` never ran — a **dead submit button, no alert, no way forward**, on the surface that
  gates every other surface. The token is now validated as a non-empty **string** before a session is stored:
  a `'token' in data` check would pass for a non-string and store `[object Object]`, which is worse than a clean
  refusal because the app then looks signed in and every later call 401s.

The rule the two share, and the one to apply to any future body read here: **a 2xx is a statement about the
transport, not about the payload.** A surface may only ever be left in a state the operator can act on — an
answer, an error with a retry, or a refusal — and never in one that requires reloading the page to escape.

**A third of the family, resolved (gh#969) — a discarded 2xx body is a lost answer.** `repriceOrder` declared
`request<void>`, reading the `200` and throwing it away — including the gate-approved-**size** echo (`size` vs
`requestedSize`, `outcome = Resized`, gh#292) the server returns precisely so a downsize is not silent. Not a
liveness failure (nothing strands), but the same root: a client that does not read what the server answered. It
now reads the typed `RepriceResult` and surfaces a gate-approved downsize on the blotter, so a trimmed quantity is
never silently applied. The audit it triggered cleared the other `request<void>` callers returning a body: the
order / conditional cancels and the kill-switch disengage return acks the consumer re-reads past, and
`PUT /accounts/{id}/risk` echoes the exact profile it was sent (a full-field replace, no clamp) — so discarding
those is correct, and only the reprice carried a gate-adjustable outcome.

## Follow-ups
- Token **issuance + refresh** flow (login → JWT; refresh strategy; expiry).
- **Signing-key** storage / rotation (server-side secret, §8).
- The **initial policy set** (one operator policy) and the claim(s) future roles will key on.
- SignalR **access-token** wiring.
