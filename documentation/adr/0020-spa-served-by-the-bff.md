# ADR-0020: The SPA is served by the BFF from one origin, not a separate static host

**Status:** Accepted · **Date:** 2026-08-04 · **Deciders:** Adam (operator/maintainer)
**Extends:** [ADR-0012](0012-containerization-local-dev.md) (containerization & local dev) and
[ADR-0018](0018-image-registry-ghcr.md) (build once, one artifact everywhere) — this keeps the client inside that
same single artifact rather than adding a second one. Neither is superseded.
**Relates to:** `R-18` (authentication & authorization), `R-19` (installable client);
[ADR-0010](0010-progressive-web-app.md) (PWA); [ADR-0015](0015-distribution-licensing-governance.md) (self-hosted,
fork-first); engineering §3, §8. Issue: `gh#646`, parent `gh#23`.

## Context

`gh#23` [U1] calls for a React SPA. There was no client code in the repository at all, so the first client PR has
to answer a question that is cheap now and expensive later: **how does the built bundle reach a browser?** The
answer is not cosmetic — it decides whether there is a cross-origin boundary between the client and the BFF, and
that in turn decides the CORS posture, how the `R-18` JWT is carried and stored, and what a future CSP has to
allow. Deciding it per-surface, as each of `gh#647`/`gh#648`/`gh#652`–`gh#654` lands, would mean re-deciding it
five times and converging by accident.

Two properties of this system constrain the answer more than developer preference does:

- It is **single-operator and self-hosted** ([ADR-0015](0015-distribution-licensing-governance.md)). A fork must be
  able to deploy it without an enterprise account or a platform team.
- The image is **built once and run everywhere** ([ADR-0018](0018-image-registry-ghcr.md)) — "the same artifact
  local and deployed" is a stated goal, not an aspiration.

## Decision

**The ASP.NET BFF serves the built client as static files from its own origin, and the bundle ships inside the
existing container image.** There is no second deployable and no cross-origin boundary between client and API.

Concretely: the image gains a Node build stage that produces the bundle; the bundle is copied into the API's
`wwwroot`; the BFF serves it with static-file middleware plus a SPA fallback so client-side routes resolve on a
hard refresh. `docker compose up` continues to bring up **one** service that answers both the API and the app.

## Alternatives considered

**A separate static origin** (object storage / CDN / a second service). Rejected: it buys independent client
deploys and CDN edge caching, neither of which this system needs — there is one operator, one region that matters
(latency to the broker), and the promotion ladder moves one artifact at a time. What it costs is concrete: a CORS
allow-list to maintain and get wrong, a JWT that now crosses origins (with the cookie/storage and CSRF questions
that follow), a second thing to deploy, version-skew between a client and an API that were never built together,
and a fork that must now stand up two things. It also breaks the `ADR-0018` "one artifact" property outright.

**Serve the client in dev from Vite and in production from the BFF.** Rejected as the *decision*, though Vite's dev
server is of course still used while developing: making the two environments structurally different is exactly how
a CORS or auth bug reaches production having been invisible locally. The dev server proxies `/api`, `/health` and
the auth routes to the BFF so that **dev has the same single-origin shape as production**.

## Consequences

**Positive**

- **No CORS surface at all** between client and API — the safest configuration is the one that does not exist.
- The `R-18` token is same-origin, so its handling stays a client-storage question rather than a cross-site one.
- One image, one deploy, one thing to promote up the `develop → staging → main` ladder; client and API can never
  be version-skewed, because they are built together.
- A fork deploys exactly what it deployed before this ADR: one container.
- The future CSP (`R-19` / [ADR-0010](0010-progressive-web-app.md)) has a single origin to describe.

**Negative / costs**

- The API image now carries a Node build stage, so image build time grows and an npm dependency-audit surface
  joins the .NET one. `.dockerignore` must exclude `node_modules/`, or the build context balloons.
- A client-only change rebuilds and redeploys the API image. Acceptable at this cadence; it is the direct price of
  the single-artifact property.
- **The SPA shell is anonymously reachable, and must be** — a browser cannot present a token before it has loaded
  the page that collects one. This is *not* a weakening of `R-18`: the shell is static markup and script with no
  data and no action in it, and every API route it subsequently calls still requires a token. It is recorded by
  name in `AuthorizationSurfaceIntegrationTests`' allow-list, the same way the `gh#604` documentation routes were,
  so being public stays a decision on the record rather than an omission. Note this cost is **not avoided** by the
  rejected alternative — a separately-hosted shell is equally public, just on another origin.

## Follow-ups

- The CSP itself is not written here; it belongs with the app shell (`gh#647`) once the real asset and connection
  set is known.
- Whether npm advisories gate CI the way `NU1903` does for NuGet (`gh#604`'s `Microsoft.OpenApi` pin) is left to
  the first client dependency bump that raises one.
