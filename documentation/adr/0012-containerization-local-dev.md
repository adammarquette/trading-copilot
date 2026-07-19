# ADR-0012: Containerization & local development

**Status:** Accepted · **Date:** 2026-07-19 · **Deciders:** Adam (operator)
**Relates to:** engineering §2 (stack), §3 (solution structure), §8 (deployment / environments / secrets), §10
(CI/CD); [deployment runbook](../deployment-runbook.md); PRD `R-14` (practice/live), `R-17` (venues).

## Context
Development is **AI-first**, but engineers (and agents) still need to **run the system locally** to build, test, and
verify against a realistic environment. The cloud tier already deploys to **Railway via a multi-stage `Dockerfile`**
(engineering §8), so the app is containerized for production. This ADR establishes the matching **local** story so
"works locally" and "works on Railway" are the **same artifact**.

## Decision
- **Containerize the system.** App services ship as **Docker images** (the same multi-stage `Dockerfile` used for
  Railway), so local and cloud run the **same artifact** — no "works on my machine" drift.
- **`docker compose up` for local dev.** A checked-in root **`docker-compose.yml`** stands up the local environment
  (app service[s] + supporting infra) with one command, for engineers and agents alike.
- **The database is config-driven — the one allowed exception.** The DB **need not** run under our compose: the app
  reads its **connection string from configuration** (`ConnectionStrings__*` env / `appsettings`, §8), so it can point
  at the **compose DB service** (included for convenience — TimescaleDB + pgvector), a **locally-installed** Postgres,
  or a **managed** instance. Compose provides a working DB by default; swapping it is a **config change, not a code
  change**.
- **Secrets never in the image or compose.** Local defaults are non-secret; real credentials / API keys come from
  **env / a gitignored `.env`** locally and **CI secrets** in the cloud (§8) — never committed.

## Alternatives considered
- **Run natively (no containers) locally.** Fewer moving parts, but drifts from the Railway Docker artifact and makes
  onboarding / agent setup fragile. Rejected — parity matters.
- **Dev Containers (VS Code) only.** Nice editor integration, but ties local dev to one editor; `docker-compose` is
  editor-agnostic and works for CI and agents too. (A devcontainer can *wrap* the same compose later.)
- **Containerize the DB into compose with no external option.** Convenient, but some devs want a managed / persistent
  Timescale instance; a config-driven connection keeps both open.

## Consequences
**Positive** — one-command local bring-up; **local == Railway artifact**; agents can stand up a realistic env;
config-driven DB keeps compose-vs-external flexible.
**Negative / costs** — a `docker-compose.yml` (and the app `Dockerfile`) to **maintain in lockstep** as services are
added; Docker is a prerequisite for local dev; the compose DB image (TimescaleDB + pgvector) must track the extensions
the app relies on.

## Follow-ups
- Author the app **`Dockerfile`** (multi-stage sdk → aspnet, binds `$PORT`) and wire the `app` service in compose once
  the BFF project exists (§3, §8).
- Confirm the **TimescaleDB + pgvector** image / tag + volume path for the compose `db` service.
- Keep a **`.env.example`** documenting `ConnectionStrings__*` + `POSTGRES_*`; real `.env` stays gitignored.
- Decide whether a **Dev Container** wraps the same compose for editor onboarding.
