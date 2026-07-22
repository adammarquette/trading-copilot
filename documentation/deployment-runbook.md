# Deployment Runbook — Trading Co-Pilot

**Operational companion** to [engineering §8](trading-platform-engineering.md) (config / secrets, environments) and
[§10](trading-platform-engineering.md) (Git workflow, CI/CD) — those hold the *practices*; this runbook holds the
concrete **resources and procedures** for deploying and operating the platform.

**Status:** scaffold — deepens as CI/CD and the real services are built. **Nothing is deployed yet** (`src/` is a
throwaway placeholder); this documents the intended shape and the setup still to do.

## Platform
- **Cloud:** [Railway](https://railway.com) — project **`soothing-illumination`**
  (`2601eb74-b5f9-411f-bb9a-0cd19e6fd540`).
- **Image registry:** **GHCR** — `ghcr.io/adammarquette/trading-copilot`, **public** ([ADR-0018](adr/0018-image-registry-ghcr.md)).
  CI builds once per merge and pushes; local and Railway both **pull** this artifact. Tags: `:develop` / `:staging` /
  `:main` per environment, plus `:sha-<short>` for an exact rollback target.
- **Data:** one Postgres with **TimescaleDB** + **pgvector** (Railway-managed plugin vs. self-hosted — *Decide*),
  three shapes in one database (engineering §2). Factor into the Decide: the `AddEventBackbone` migration
  **degrades gracefully on non-Timescale Postgres** (the `Events` log stays a plain table, `RAISE WARNING`, no
  hypertable/retention/continuous-aggregates) — the app runs, but the ADR-0001 backbone only gets its Timescale
  behaviors on a Timescale-enabled instance (locally: the compose `timescaledb-ha` image, which bundles both
  extensions).

## Local development (docker-compose)
`docker compose up -d` from the repo root stands up the local stack ([ADR-0012](adr/0012-containerization-local-dev.md),
[ADR-0018](adr/0018-image-registry-ghcr.md), engineering §8). The `app` service **pulls the GHCR image** — the same
artifact Railway runs — so **local ≡ cloud** literally, not just the same Dockerfile.

**Two modes:**

| Goal | Command |
| --- | --- |
| Run the published build (default) | `docker compose up -d` |
| Run **my local changes** | `docker compose down` then `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build` |

`docker compose up` **pulls, never builds** — fast, and works on a machine that cannot build the image.
`IMAGE_TAG` selects the published build (default `develop`; e.g. `IMAGE_TAG=staging docker compose up -d`).
The dev override (`docker-compose.dev.yml`) builds from your working tree and tags it `trading-copilot:local`, a
distinct name so a later `docker compose pull` cannot clobber your build. Building needs a **recursive clone**
(the Dockerfile copies the `external/` submodule).

> **Before the image is published-and-public, the default pull fails.** The GHCR package is created on the first
> merge to `develop`, **private** until the operator flips it public (below). Until that flip, `docker compose up`
> returns `unauthorized`/`not found` — so the **dev-override build is the working local path**, or authenticate
> with `docker login ghcr.io` (`read:packages`). This is a startup-window caveat, not a steady state: once the
> package is public, the plain pull is the default again.
- **Database is config-driven** — `docker-compose.yml` includes a **TimescaleDB + pgvector** service for convenience,
  but the app takes its **connection string from config** (`ConnectionStrings__*` env / `appsettings`), so you can
  point at the compose DB, a local Postgres, or a managed instance instead.
- **Secrets** stay in a **gitignored `.env`** (copy `.env.example`) — never committed; cloud secrets come from Railway (§8).
- Schema changes apply via **`dotnet ef database update`** against the configured connection (as the data layer lands).

## Environments ↔ branches
Each long-lived branch deploys to its own Railway environment (engineering §10). Never wire a **live** account into a
non-prod environment — non-prod is **practice-only** (real execution path, no real money).

| Branch | Railway env | Trading mode | Data | Deploy approval |
|---|---|---|---|---|
| `develop` | dev | **practice** (ProjectX) | seeded from a prod snapshot | auto on merge |
| `staging` | staging | **practice** | prod snapshot · integration tests run here | auto on merge |
| `main` | production | **live** — real money, the *only* live env | authoritative | **human-approved** |

*(A new Railway project starts with a single environment; the `dev` + `staging` environments still need creating.)*

## Secrets & config (per environment)
Server-side only, from the Railway environment — **never in source** (Options pattern, validate-on-start; §8):
- **Broker (ProjectX):** account id + credentials + endpoints, **per environment** (practice vs. live).
- **Auth:** JWT signing key (ADR-0003).
- **Embeddings:** Cohere API key.
- **Data providers:** Finnhub + Tiingo API tokens (free tier).
- **Database:** connection string (Railway-managed).
- **Ingestion:** poll intervals; news relevance config (or DB-stored).

### Operator password recovery (R-18, ADR-0017 operator lifecycle)
The operator controls the deployment, so the environment can recover the account — **host control = account
control**. There is no email reset (no mail infrastructure, no need) and no second admin. Forgot the password:

1. Set **`Bootstrap__Password`** to the **new** password and **`Bootstrap__ResetPassword=true`** in the
   environment (`.env` locally; Railway variables in the cloud).
2. **Restart the app once.** On startup it re-hashes the new password onto the **existing** operator — same user
   row, same id, so every R-20-scoped row in the workspace (firms, conventions, orders, journal) stays yours.
   *Never* recover by deleting the user row: the reseeded user gets a new id and the default-deny filter strands
   the entire workspace as orphaned data.
3. Sign in, then **remove `Bootstrap__ResetPassword`** (and rotate `Bootstrap__Password` out if you prefer).
   The flag is a deliberate one-restart opt-in — without it, a stale env password can never silently overwrite
   the stored credential.

## Services (as they are built)
The microservices ([architecture](trading-platform-architecture.md)) deploy as **separate Railway services**, scaled
independently: ingestion (websocket) · poller · processor(s) · trigger engine · BFF/API + agents · the React SPA
(static). *(Fill in service names / start commands as they land.)*

## CI/CD pipeline (GitHub Actions → GHCR → Railway)
`lint → build → test → publish image → deploy → verify` (engineering §10, [ADR-0018](adr/0018-image-registry-ghcr.md)):
1. Push / merge to a long-lived branch triggers the pipeline.
2. `dotnet format --verify-no-changes` + **unit tests** must pass.
3. **Publish image to GHCR** — CI builds the `Dockerfile` **once** and pushes `ghcr.io/adammarquette/trading-copilot`
   tagged `:<branch>` + `:sha-<short>`. Merge-only (`if: github.event_name == 'push'`); a PR never publishes.
4. **Railway deploys that image** (the pushed tag) to the branch's environment — the tested artifact, not a rebuild.
5. **Integration tests** run against **staging** after a merge to `staging`.
6. On **production** deploy, a **smoke-test subset** runs; a failure **flags the release for rollback** — pin the
   previous `:sha-<short>` tag to roll back to an exact prior build.

### Operator setup — console actions CI cannot do (ADR-0018)
The build/push half is code (`.github/workflows/ci.yml`). These are one-time console steps, recorded here because
configuration that lives only in a provider console is otherwise invisible to anyone reading the pipeline:
1. **After the first CI publish**, set the GHCR package `trading-copilot` visibility to **public** — GHCR creates
   packages **private** by default, so the first pull (local or Railway) fails until this is flipped.
2. **Reconfigure the Railway service** to deploy from the image `ghcr.io/adammarquette/trading-copilot:<branch>`
   rather than building from the repo source. No pull credential is needed (public image).
3. Until step 2 is done, Railway still builds from source; the GHCR image is used by local dev only. The pipeline
   is deliberately left this way rather than half-wiring a deploy trigger against a not-yet-image-sourced service.

### Automated code review — a ruleset, not a workflow
**Copilot code review is not a GitHub Actions job** and cannot be invoked from `ci.yml`. It is a **branch
ruleset** rule, so it lives in repository settings rather than in this repo — recorded here because
configuration that exists only in the GitHub UI is otherwise invisible to anyone reading the pipeline.

| Ruleset | Target | Rule | Settings |
| --- | --- | --- | --- |
| `copilot-review-develop` (**active**) | `refs/heads/develop` | `copilot_code_review` | `review_on_push: true` · `review_draft_pull_requests: false` |
| `default-main` (**disabled**) | — | `pull_request`, `code_scanning`, … | Required approvals and the promotion guards — deliberately off; see `gh#45` |

Every feature PR targets `develop`, so the first ruleset covers the whole review surface. `review_on_push` means
each new commit on an open PR is reviewed, not just the PR's opening. Drafts are excluded so a branch can churn
without triggering a review per commit.

Review findings are advisory — they do not block a merge. The blocking gates remain `dotnet format`, the unit
tests, and the `ladder` branch-policy check. Repo-specific review guidance lives in
[`.github/copilot-instructions.md`](../.github/copilot-instructions.md); update it when a review repeatedly
misses something this codebase cares about.

## Deploy procedure
- **Non-prod (dev / staging):** automatic on merge — CI builds + deploys.
- **Production:** **human-approved** (§9). Promote `staging → main`; CI deploys; smoke tests verify. A person must be
  aware of and approve any production deploy.

## Rollback procedure
- Triggered by a **failed production smoke test** or an operator decision.
- **Human-approved** (§9): roll back via Railway (redeploy the previous release) and confirm with smoke tests. Any
  rollback is an explicit, approved action — never automatic.

## Verification / smoke tests
Post-deploy, the tagged **smoke** subset (engineering §5) confirms the critical paths — health, connectivity, auth,
and the safety-critical **execution + auto-flatten** path in a **practice** context. *(Define the smoke set as the
paths land.)*

## Cost
Monthly Railway spend ceiling is **Q-10** (open) — watch always-on ingestion + database costs.

## Open items
- Postgres / Timescale / pgvector on Railway: managed plugin vs. self-hosted service.
- Concrete GitHub Actions workflow(s) + the Railway deploy integration (CLI / MCP / GitHub trigger).
- Create the `dev` + `staging` Railway environments and map branch → environment.
- Non-prod **snapshot refresh** cadence + mechanism (§8).
- Define the **smoke-test set** and the health / verify checks.
