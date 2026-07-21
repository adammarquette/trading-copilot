# ADR-0018: Build the image once in CI, publish to GHCR, consume it everywhere

**Status:** Accepted · **Date:** 2026-07-21 · **Deciders:** Adam (operator/maintainer)
**Extends:** [ADR-0012](0012-containerization-local-dev.md) (containerization & local dev) — the "same artifact
local and cloud" goal, made literal. Does not supersede it; ADR-0012's containerize / `docker compose up` /
config-driven-DB decisions all stand.
**Relates to:** engineering §8 (deployment), §10 (CI/CD); [deployment runbook](../deployment-runbook.md);
[ADR-0015](0015-distribution-licensing-governance.md) (fork-first, public). Issue: `gh#79`.

## Context

ADR-0012 said local and cloud run "the same artifact." In practice they ran the *same Dockerfile* built in two
places: Railway rebuilt from source on `railway up`, and `docker compose up` rebuilt locally. Functionally
equivalent, but not bit-for-bit the same image, and rebuilding to run is wasted time — the SDK restore/publish
is the slow step, and it happened on every local bring-up.

Nothing shared a built artifact because there was no registry. Adding one closes the gap ADR-0012 named.

## Decision

- **Build once, in CI.** On merge to a long-lived branch (`develop` / `staging` / `main`), CI builds the image
  after the unit tests pass and pushes it to the **GitHub Container Registry**,
  `ghcr.io/adammarquette/trading-copilot`. PRs do **not** publish — the code is not yet on a shared branch, and
  a fork PR cannot hold `packages: write`.
- **Tag by branch, plus an immutable SHA tag.** `:develop` / `:staging` / `:main` track each environment's
  current build; `:sha-<short>` pins an exact build for a rollback. Branch → environment matches §8.
- **The image is public.** Consistent with the public, fork-first repo (ADR-0015). It carries **no secrets** —
  those arrive from env at runtime (ADR-0012) — so there is nothing sensitive to expose, and a public image
  needs no login to pull, locally or by a fork.
- **Local pulls by default, builds on demand.** `docker-compose.yml` sets the `app` image to the GHCR ref
  (`:${IMAGE_TAG:-develop}`) with **no `build`**, so `docker compose up` pulls. A second file,
  `docker-compose.dev.yml`, adds the `build` and a distinct `trading-copilot:local` tag; layering it
  (`-f docker-compose.yml -f docker-compose.dev.yml up --build`) runs the working tree instead. This is the
  "kill the published container and run my changes" workflow, and it is opt-in so the common case stays a pull.
- **Railway deploys the CI-built image**, not a source rebuild. The Railway service is reconfigured to deploy
  from `ghcr.io/adammarquette/trading-copilot:<branch>`, so the artifact CI produced is the artifact that runs —
  ADR-0012's goal, now literal.

## Alternatives considered

- **Keep rebuilding independently (no registry).** The status quo. Rejected: it wastes local build time on
  every bring-up and leaves "same artifact" aspirational — Railway and local images are never provably identical.
- **Docker Hub instead of GHCR.** Rejected: GHCR is native to the repo's host, authenticates with the built-in
  `GITHUB_TOKEN` (no extra secret to manage), and inherits the repo's identity. Docker Hub would add an account
  and a credential for no gain here.
- **Private image.** Rejected on ADR-0015: a fork-first, public project should be runnable without a token, and
  the image holds nothing secret. Private would add a `read:packages` PAT to every local pull and to Railway for
  no security benefit.
- **Publish on every PR.** Rejected: it would push unreviewed images, and fork PRs cannot be granted
  `packages: write`. Publishing on merge means every published tag has passed review and CI.

## Consequences

**Positive**
- Local and Railway run the byte-identical image CI built and tested. "Works locally" and "works deployed" stop
  being two builds.
- Local bring-up is a pull, not a build — fast, and it works on a machine that cannot build the image.
- `:sha-<short>` gives a precise rollback target; branch tags give a stable "current" per environment.
- A fork clones and `docker compose up`s with no registry login.

**Negative / costs**
- **A published artifact now exists to reason about.** A build that passes CI but is wrong still gets a tag; the
  smoke tests on deploy (§10) remain the backstop, not the registry.
- **Railway is now coupled to GHCR availability.** A GHCR outage blocks a deploy. The image is cached on the
  running host, so it does not take the *running* system down — only a new deploy.
- **Two compose files to keep in lockstep** with the app service, on top of the Dockerfile (extends ADR-0012's
  same cost).
- **First-publish and Railway wiring are console actions** (see runbook), not code — the gap this project has
  already been bitten by, so they are written down rather than assumed.

## Follow-ups

- **Operator, once (recorded in the [runbook](../deployment-runbook.md)):** set the GHCR package visibility to
  **public** after the first publish (packages are created private); reconfigure the Railway service to deploy
  the GHCR image; supply no pull credential (public).
- **CI deploy-trigger.** Once Railway is image-sourced, a deploy step can replace `railway up` with a trigger to
  deploy the freshly-pushed tag (§10). Deferred until the Railway service is reconfigured, so the pipeline is
  never half-wired.
- **Multi-service images.** The architecture is several services (ingestion, processor, execution, …); today
  only the BFF image exists. Each new service that ships as its own image extends this same publish job.
