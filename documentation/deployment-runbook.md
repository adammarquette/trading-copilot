# Deployment Runbook — Trading Co-Pilot

**Operational companion** to [engineering §8](trading-platform-engineering.md) (config / secrets, environments) and
[§10](trading-platform-engineering.md) (Git workflow, CI/CD) — those hold the *practices*; this runbook holds the
concrete **resources and procedures** for deploying and operating the platform.

**Status:** scaffold — deepens as CI/CD and the real services are built. **Nothing is deployed yet** (`src/` is a
throwaway placeholder); this documents the intended shape and the setup still to do.

## Platform
- **Cloud:** [Railway](https://railway.com) — project **`soothing-illumination`**
  (`2601eb74-b5f9-411f-bb9a-0cd19e6fd540`).
- **Data:** one Postgres with **TimescaleDB** + **pgvector** (Railway-managed plugin vs. self-hosted — *Decide*),
  three shapes in one database (engineering §2).

## Local development (docker-compose)
`docker compose up -d` from the repo root stands up the local stack ([ADR-0012](adr/0012-containerization-local-dev.md),
engineering §8). App services run as the **same containerized artifact** as Railway (the multi-stage `Dockerfile`),
so **local ≡ cloud**.
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

## Services (as they are built)
The microservices ([architecture](trading-platform-architecture.md)) deploy as **separate Railway services**, scaled
independently: ingestion (websocket) · poller · processor(s) · trigger engine · BFF/API + agents · the React SPA
(static). *(Fill in service names / start commands as they land.)*

## CI/CD pipeline (GitHub Actions → Railway)
`lint → build → test → deploy → verify` (engineering §10):
1. Push / merge to a long-lived branch triggers the pipeline.
2. `dotnet format --verify-no-changes` + **unit tests** must pass.
3. Build, then **deploy to the branch's Railway environment**.
4. **Integration tests** run against **staging** after a merge to `staging`.
5. On **production** deploy, a **smoke-test subset** runs; a failure **flags the release for rollback**.

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
