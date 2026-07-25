# Deployment Runbook — Trading Co-Pilot

**Operational companion** to [engineering §8](trading-platform-engineering.md) (config / secrets, environments) and
[§10](trading-platform-engineering.md) (Git workflow, CI/CD) — those hold the *practices*; this runbook holds the
concrete **resources and procedures** for deploying and operating the platform.

**Status:** living — the local stack and the CI → GHCR image pipeline are **real** (`src/` is the actual solution;
pipeline steps 1–3 below run on every merge). The **Railway deploy hook is not wired yet** — steps 4–6 are the
intended shape (see *Operator setup* and *Open items*), and the cloud environments still need creating.

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
- **`.env` under compose is an allowlist, not a passthrough.** The app service's `environment:` block names every
  key compose forwards; anything else in `.env` is used for interpolation and then **silently dropped**.
  **Adding a bound `Options` section is not done until its keys are on that list** — the omission is invisible at
  runtime, because the app simply uses its own defaults and says nothing (gh#236 was exactly that, for the
  `Flatten__` auto-flatten deadlines).
- **Two entry shapes, and the difference is load-bearing** (gh#236). The `environment:` block is a **map**, so:
  - `KEY: "${KEY:-default}"` — **always sets** `KEY`, with a compose-level fallback. Use only where compose must
    impose a value the app cannot know (the DB host, the local-dev signing key).
  - `KEY:` — a **null** value **passes through only if defined** in `.env` or the shell; when undefined the key is
    **absent from the container**, not empty (identical to a bare `- KEY` list entry — verified via
    `docker compose config`). Use this for anything the **app already defaults**.

  The distinction matters because an empty string **binds** and overwrites the app's default. On the `Flatten__`
  safety path that would mean a zeroed attempt cap or an unparseable deadline, and on a `required` field
  (`Flatten__Instruments__N__Symbol`) a startup crash. **Absent is the only value that means "the app decides."**
- **The per-instrument flatten slots are capped at four** (`Flatten__Instruments__0__` … `__3__`) — compose cannot
  forward an open-ended indexed list. Four covers every market with a built-in deadline (ES, NQ, CL, GC). A fifth
  needs its keys added to `docker-compose.yml`: a deliberate edit, not a silent limit. The slots are
  **independent, not positional** — the .NET binder collects whatever indices are present, so gaps are harmless.
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
2. `dotnet format --verify-no-changes` + **unit tests** + the **pre-merge integration suite** (venue-independent,
   on a throwaway real-Postgres container — gh#121) must pass.
3. **Publish image to GHCR** — CI builds the `Dockerfile` **once** and pushes `ghcr.io/adammarquette/trading-copilot`
   tagged `:<branch>` + `:sha-<short>`. Merge-only (`if: github.event_name == 'push'`); a PR never publishes.
4. **Railway deploys that image** (the pushed tag) to the branch's environment — the tested artifact, not a rebuild
   *(not yet wired: console step 2 below is pending, so Railway does not deploy on merge today)*.
5. **Integration tests** run against **staging** after a merge to `staging` *(not yet wired — this is the staging
   tier; the venue-independent **pre-merge tier already runs in CI**, step 2 / gh#121)*.
6. On **production** deploy, a **smoke-test subset** runs; a failure **flags the release for rollback** — pin the
   previous `:sha-<short>` tag to roll back to an exact prior build *(not yet wired, with step 5)*.

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

Every feature PR targets `develop`, so the first ruleset covers the whole review surface. `review_on_push` means
each new commit on an open PR is reviewed, not just the PR's opening. Drafts are excluded so a branch can churn
without triggering a review per commit.

**This rule gates the merge — it is not advisory.** Copilot's *findings* are advisory (no finding blocks
anything), but the **rule itself is a ruleset requirement**: a PR into `develop` cannot merge until Copilot has
**responded**. What makes it confusing is that a ruleset rule is **not a status check** — it never appears in the
checks tab, so while it is outstanding the PR reads `mergeStateStatus: BLOCKED` with every check green, 0
required approvals, and **nothing to point at**. A merge click silently does not take.

**Quota exhaustion delays it; it does not deadlock it.** When Copilot cannot review, it still replies — a
`COMMENTED` review reading *"unable to review this pull request because the user who requested the review has
reached their quota limit"* — and **that reply satisfies the rule**; the PR goes `CLEAN` and merges. Observed on
both PR #195 and PR #237. So a PR that looks permanently stuck is usually just waiting on the bot to answer:
re-check before escalating.

Diagnose with `gh pr view <n> --json mergeStateStatus,reviewDecision,latestReviews` — the reviews array shows
whether Copilot has responded at all. The ruleset carries **no bypass actors**, so if it ever genuinely does
deadlock, only editing the ruleset in repo settings clears it.

The other **blocking** gates are the required status checks enforced by the branch-protection rulesets below
(gh#45): `build & unit tests`, `commit-hygiene`, the pre-merge integration suite (gh#121), and `ladder` on the
promoted branches. Repo-specific review guidance lives in
[`.github/copilot-instructions.md`](../.github/copilot-instructions.md); update it when a review repeatedly
misses something this codebase cares about.

### Branch protection — required-check rulesets (gh#45)
Rulesets make the CI checks **blocking** on the long-lived branches. They live in repo settings, recorded here
because settings-only config is otherwise invisible to anyone reading the pipeline. Applied 2026-07-24 via the
rulesets API (possible once the repo went public, `gh#58`); the disabled, mis-targeted `default-main` leftover —
whose `required_deployments` / `code_scanning` / `code_quality` rules reference features this repo does not have
and would have deadlocked every merge if enabled — was deleted at the same time.

| Ruleset | Target | Required status checks | Other rules |
| --- | --- | --- | --- |
| `protect-develop` (**active**) | `refs/heads/develop` | `build & unit tests` · `commit-hygiene` · `integration tests (pre-merge)` | PR required (0 approvals) · block force-push · block deletion |
| `protect-staging` (**active**) | `refs/heads/staging` | the develop set **+ `ladder`** | PR required (0 approvals) · block force-push · block deletion |
| `protect-main` (**active**) | `refs/heads/main` | the develop set **+ `ladder`** | PR required (0 approvals) · block force-push · block deletion |

- **`ladder` is required on `staging`/`main`**, so a promotion PR from a disallowed source fails the check and
  cannot merge — this is what turns the advisory ladder guard into hard enforcement of `staging ← develop` and
  `main ← staging`.
- **`stale-base` and `publish image (GHCR)` are deliberately NOT required** — `stale-base` is skipped on the
  long-lived branches and `publish image` runs only on `push`, so requiring either would leave a required check
  forever pending and **deadlock the merge**. This is the trap to remember before adding any required check:
  confirm it actually runs on that branch's PRs.
- **Non-strict** (no forced up-to-date-before-merge), **0 required approvals** (single operator — the checks and
  the ladder are the gate), **no bypass list** (the rules bind even for the admin; that is the point of
  enforcement). An approval requirement can be added later — `trading-copilot-reviewer[bot]` (gh#141) can satisfy
  it on your own promotion PRs — as can an emergency bypass if a broken required check ever needs overriding.

### Reviewer identity — a GitHub App for agent verdicts (gh#141)
The [Code Reviewer contract](agents/code-reviewer.md) requires an agent to render a **formal verdict** — Approve
or Request changes — not a bare comment. But **GitHub forbids approving or requesting changes on your own PR**,
and every agent here authenticates as the maintainer (`adammarquette`), who authors the PRs. So the verdict needs
a **distinct identity that is not the author.**

**Decision (gh#141): a GitHub App**, not a second machine-user account — it needs no extra login or email, its
token is scoped and revocable, its reviews post as `…[bot]` (a separate actor, so not self-review), and a fork
recreates it without a second person (ADR-0015).

**One-time operator setup — GitHub UI, cannot be scripted** (the App-manifest flow and the private key are
console-only, recorded here because they are otherwise invisible to anyone reading the pipeline):
1. **Settings → Developer settings → GitHub Apps → New GitHub App.** Name e.g. `trading-copilot-reviewer`; any
   valid Homepage URL; **uncheck Webhook → Active**.
2. **Permissions → Repository:** **Pull requests → Read & write**; **Contents → Read-only**; **Metadata →
   Read-only** (auto). Nothing else.
3. **Create**, then **Generate a private key** (a `.pem` downloads) and note the **App ID**.
4. **Install App → this account → Only select repositories → `trading-copilot`**; note the **Installation ID**.
5. Provide these to the reviewer agent as environment values (never in source): `REVIEWER_APP_ID`,
   `REVIEWER_APP_INSTALLATION_ID`, and the private key. **For the key, prefer a file:** save the downloaded
   `.pem` **unmodified** somewhere git-ignored (outside the repo is simplest — no `.gitignore` to trust) and set
   `REVIEWER_APP_PRIVATE_KEY_FILE` to its path. A path has no newline/quote pitfalls; **cramming a multi-line PEM
   into a line-based `.env` corrupts it** (the BEGIN header fuses to the base64). Inline `REVIEWER_APP_PRIVATE_KEY`
   is also accepted (multi-line or `\n`-escaped, double-quoted) if you must. Locally: the operator's env / a
   git-ignored `.env`; in CI: repository secrets.

**How the reviewer agent uses it** — the committed helper
[`.github/scripts/reviewer-review.sh`](../.github/scripts/reviewer-review.sh) does the token dance (JWT signed
with the private key → `POST /app/installations/{id}/access_tokens` → a ~1 h installation token → the review),
reading the three secrets from the environment. The key is written only to a private (0600) temp file for the
openssl call and removed immediately (native-Windows openssl cannot read a process-substitution FD); neither key
nor token is ever printed.
- `reviewer-review.sh verify` — mints a token and reports what the installation can reach; **posts nothing**. Run
  this first.
- `reviewer-review.sh review <pr> REQUEST_CHANGES <body-file>` (or `APPROVE` / `COMMENT`) — submits the verdict;
  it posts as `trading-copilot-reviewer[bot]`, a different actor from the author, so GitHub accepts it. The script
  prints the bot login it posted as — the proof self-review was bypassed.

**Until the App exists**, an agent review falls back to a comment whose **first line is the verdict**
(`**Verdict: Request changes**` / `**Verdict: Approve**`) so the signal is unambiguous even without a formal
state. Once the App is live, this fallback is retired and (with `gh#45`) its approval can become a required
check.

## The dead-man's switch (operator setup — required before live)

The **only** alerting tier that survives this process dying (R-13, `gh#244`,
[ADR-0019](adr/0019-alerting-channel-and-thresholds.md)). Every other alert assumes something is alive to raise it;
if the host dies before an auto-flatten deadline, the flatten never fires **and nothing alerts**. The app reports to
an external monitor, which pages when the report **fails to arrive**.

**A deployment without it is silently missing its most important safety net.** The app starts and warns loudly, but
nothing else notices.

1. **Create the Pushover application** and note the user key + app token (ADR-0019 — Emergency priority repeats until
   acknowledged and bypasses Do Not Disturb; a channel without both is not a pager).
2. **Create the monitor checks** on a cron-monitor (healthchecks.io or equivalent) — **on infrastructure independent
   of this app.** One sharing this host or this Railway project is not a dead-man's switch, it is a second thing that
   dies at the same moment.
   - **Liveness:** period 1 min, grace 3 min.
   - **Per instrument:** expected on **weekdays**, by that market's flatten deadline **+ 5 min** (ES/NQ ~14:35 CT,
     CL ~13:20, GC ~12:20 — confirm against your configured deadlines, not these examples).
3. **Route every check to Pushover** at **Emergency** priority.
4. **Set the environment variables** (`CheckIn__HeartbeatUrl`, `CheckIn__Instruments__N__Symbol` / `__Url`) — see
   `.env.example`. Under compose, only the keys named in the app service's `environment:` map are forwarded, and
   **four instrument slots (0–3)** are wired; a fifth market needs another pair added there.
5. **Verify it pages.** Stop the app before a deadline and confirm the page arrives. An unverified dead-man's switch
   is an assumption, not a safety net.

**Ping URLs are capability URLs** — whoever holds one can forge an all-clear on a safety path. Treat them as secrets:
environment only, never committed, never pasted into an issue. The app never logs them.

**If you deliberately disable auto-flatten for a market** (R-13's warned, own-risk override), **pause that market's
monitor check too** — the app will correctly refuse to vouch for a market nothing is watching, so the absent check-in
would otherwise page every day.

## Deploy procedure
- **Non-prod (dev / staging):** automatic on merge — CI builds + deploys.
- **Production:** **human-approved** (§9). Promote `staging → main`; CI deploys; smoke tests verify. A person must be
  aware of and approve any production deploy.
- **Before the first production deploy:** the dead-man's switch above is provisioned and **proven to page**.

## Rollback procedure
- Triggered by a **failed production smoke test** or an operator decision.
- **Human-approved** (§9): roll back via Railway (redeploy the previous release) and confirm with smoke tests. Any
  rollback is an explicit, approved action — never automatic.

## Verification / smoke tests
Post-deploy, the tagged **smoke** subset (engineering §5) confirms the critical paths. **The set exists**
(gh#131): `SystemSmokeIntegrationTests`, tagged `Category=Smoke`, **strictly read-only** — `GET /health`,
`GET /auth/me`, `/firms`, `/connections`, `/connections/{id}/accounts`, `/accounts/{id}/risk`. Nothing
execution-shaped carries the smoke tag, by design: execution-path checks belong to the staging integration tier,
because a smoke test runs against **production**. Pointing the suite at a deployed target starts **no** local
container and needs no Docker (gh#152), and CI excludes `Category=Smoke` from the pre-merge integration job
(gh#159). *(Extend the set as read-only surfaces land — the auto-flatten path is verified on **staging**, in a
practice context, not here.)*

## Cost
Monthly Railway spend ceiling is **Q-10** (open) — watch always-on ingestion + database costs.

## Open items
- Postgres / Timescale / pgvector on Railway: managed plugin vs. self-hosted service.
- The Railway deploy integration (CLI / MCP / GitHub trigger) — the GitHub Actions workflows themselves exist
  (`ci.yml` + `branch-policy.yml`; §CI/CD above).
- Create the `dev` + `staging` Railway environments and map branch → environment.
- Non-prod **snapshot refresh** cadence + mechanism (§8).
- Define the **smoke-test set** and the health / verify checks.
