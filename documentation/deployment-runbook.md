# Deployment Runbook — Trading Co-Pilot

**Operational companion** to [engineering §8](trading-platform-engineering.md) (config / secrets, environments) and
[§10](trading-platform-engineering.md) (Git workflow, CI/CD) — those hold the *practices*; this runbook holds the
concrete **resources and procedures** for deploying and operating the platform.

**Status:** living — the local stack and the CI → GHCR image pipeline are **real** (`src/` is the actual solution;
pipeline steps 1–3 below run on every merge). **Steps 4 and 6 — deploy and verify — are now wired in
`ci.yml` (`gh#379`) but inert**: they skip until the operator creates the `dev` / `staging` Railway environments
and sets the deploy-hook secrets (*Operator setup* steps 4–5). Step 5, the staging integration tier, is still
unwired. The cloud environments still need creating, so nothing deploys today.

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
- **The API documents itself** (gh#604). Once up, it serves an **OpenAPI 3 spec at `/openapi/v1.json`** and a
  browsable **Scalar reference UI at `/scalar/v1`**, generated from the live routes so the spec never drifts from
  the endpoints (the source of truth the README links to, #605). **Production exposure policy:** the **spec JSON is
  served in every environment**, but the **interactive Scalar UI is disabled in production** (`ScalarUiPolicy` —
  enabled in dev/staging only, keyed off the R-14 environment), because its "try it" console would put a one-click
  trigger for `POST /accounts/{id}/orders` and `POST /kill-switch` against the **live** venue in front of anyone
  who reaches the page. Every endpoint already requires a JWT, so this is defence in depth; flip the policy to put
  the UI behind auth in production if preferred — the spec itself is always available.
- **Database is config-driven** — `docker-compose.yml` includes a **TimescaleDB + pgvector** service for convenience,
  but the app takes its **connection string from config** (`ConnectionStrings__*` env / `appsettings`), so you can
  point at the compose DB, a local Postgres, or a managed instance instead.
- **Secrets** stay in a **gitignored `.env`** (copy `.env.example`) — never committed; cloud secrets come from Railway (§8).
- **`.env` under compose is an allowlist, not a passthrough.** The app service's `environment:` block names every
  key compose forwards; anything else in `.env` is used for interpolation and then **silently dropped**.
  **Verify a flatten override actually took by reading the startup log** (gh#255) — do not assume a clean start
  means it applied. The app reports every governed market on boot, naming the deadline **and its source**:

  ```
  info: Auto-flatten armed for ES at 14:15 CT (2026-07-25 19:15:00Z) — ConfiguredOverride.
  info: Auto-flatten armed for NQ at 14:30 CT (2026-07-25 19:30:00Z) — BuiltInDefault.
  warn: Auto-flatten is DISABLED for GC (ConfiguredOverride) — positions will NOT be closed at 12:15 CT.
  ```

  `ConfiguredOverride` means your setting reached the app; `BuiltInDefault` on a market you *did* configure means
  it was **dropped** — check the allowlist below first. A market you configured that reads `ConfiguredAddition`
  when you expected an override is a **misspelled symbol**: it added a new market beside the untouched default. A
  **disabled** market logs at `warn`, never `info`.

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
- **Per-instrument contract specs** (`InstrumentSpecs__Instruments__0__` … `__3__`, gh#541) follow the same capped,
  independent-slot shape and the same absent-vs-empty rule. They supply the **tick size, point value and
  catastrophic safety-stop distance** a *server-originated* suggestion needs to become an order ticket — a manual
  ticket carries them on the request, a suggestion has no author to ask, and the browser is deliberately not allowed
  to supply them. The app ships built-in specs for the same four markets (ES 0.25/$50, NQ 0.25/$20, CL 0.01/$1000,
  GC 0.10/$100), so these settings only **override or add**. An entry replaces a default **wholesale** — set all four
  fields together, because a new tick size against an old point value is a silently wrong contract. A non-positive
  `TickSize`, `PointValue` or `SafetyStopTicks` **fails startup by design**: a zero here would not fail loudly, it
  would silently mis-size every risk calculation downstream.
- **`Suggestions__ValidityMinutes`** (gh#544) is how long a newly issued suggestion stays actionable *before* the
  auto-flatten clamp: the system stamps `min(this window, time to the market's flatten deadline)`, so a suggestion can
  never outlive the flatten about to close the position. It **must be positive** — a non-positive value fails startup
  by design, because it would emit a row the `CK_Suggestions_ExpiresAfterCreated` constraint refuses.
- Schema changes apply via **`dotnet ef database update`** against the configured connection (as the data layer lands).
  **`AddSuggestionIssuanceFields` (gh#542/#543/#544) backfills**: existing suggestions get
  `ExpiresAt = CreatedAt + 1 second` — already expired, the fail-safe direction, and strictly greater so the new CHECK
  applies cleanly. Backfilling to `CreatedAt` exactly would violate the strict inequality and abort the migration.

## Environments ↔ branches
Each long-lived branch deploys to its own Railway environment (engineering §10). Never wire a **live** account into a
non-prod environment — non-prod is **practice-only** (real execution path, no real money).

| Branch | Railway env | Trading mode | Data | Deploy approval |
|---|---|---|---|---|
| `develop` | dev | **practice** (ProjectX) | seeded from a prod snapshot | auto on merge |
| `staging` | staging | **practice** | prod snapshot · integration tests run here | auto on merge |
| `main` | production | **live** — real money, the *only* live env | authoritative | **human-approved** |

*(A new Railway project starts with a single environment; the `dev` + `staging` environments still need creating —
see *Operator setup* steps 4–5. The pipeline half is wired and inert until they exist, `gh#379`.)*

## Secrets & config (per environment)
Server-side only, from the Railway environment — **never in source** (Options pattern, validate-on-start; §8):
- **Broker (ProjectX):** account id + credentials + endpoints, **per environment** (practice vs. live).
- **Auth:** JWT signing key (ADR-0003).
- **Embeddings:** Cohere API key.
- **LLM (agent review):** `Llm__ApiKey` — the Anthropic key the reviewer wakes a live model with (gh#402/#423); a
  **real secret** like the Cohere key. Absent, the stub reviewer stands in, so production never fabricates geometry.
- **Data providers:** Finnhub + Tiingo API tokens (free tier).
- **Database:** connection string (Railway-managed).
- **Ingestion:** poll intervals; the `Ingestion:Symbols` allowlist; news relevance config (or DB-stored).
- **AI-spend governor (gh#448, ADR-0008):** `Governor__DailyBudgetUsd` + `Governor__AlertThresholdFraction` — the
  platform-wide daily AI budget and pre-alert fraction (not secrets; unset leaves the governor inert).
- **Telemetry (gh#230, ADR-0002):** `Telemetry__OtlpEndpoint` — the OTLP collector endpoint (e.g.
  `http://otel-collector:4317`). **Leave it unset to disable export**: the SDK stays wired and the app runs
  normally, it simply ships nothing. `Telemetry__ServiceName` overrides the service name stamped on every signal
  (default `trading-copilot-api`); the deployment environment is taken from `ASPNETCORE_ENVIRONMENT`. Neither is
  a secret — the collector endpoint is an address, not a credential.

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
   - **Env forwarding (`gh#325`).** `./scripts/check-env-forwarding.sh` fails the build when a key documented in
     `.env.example` is not forwarded by `docker-compose.yml`. The compose `environment:` map is an **allowlist**,
     so a missing key is read for interpolation and then dropped — the operator sets it, the app never sees it,
     nothing says so. That has hit the **R-13 flatten deadlines** (`gh#236`) and the **watchlist** (`gh#304`).
     **When it fails:** add the key to the app service's `environment:` map, using the **null pass-through**
     (`Key__Name:` with no value) for anything the app already defaults — an empty string *binds* and overwrites
     that default. For an indexed list, add the specific index; the caps are deliberate and documented in
     `.env.example`. **Run it locally** before a PR that adds configuration; it needs only Docker.
3. **Publish image to GHCR** — CI builds the `Dockerfile` **once** and pushes `ghcr.io/adammarquette/trading-copilot`
   tagged `:<branch>` + `:sha-<short>`. Merge-only (`if: github.event_name == 'push'`); a PR never publishes.
4. **Railway deploys that image** (the pushed tag) to the branch's environment — the tested artifact, not a rebuild.
   The `deploy` job is **wired** (`gh#379`) for `develop` → dev and `staging` → staging, and it **skips with a
   notice** until the branch's `RAILWAY_DEPLOY_HOOK_*` secret is set — so the pipeline stays green while the
   console steps below are outstanding, rather than going red on every merge. **`main` is deliberately excluded:**
   production deploy and rollback are human-approved, never automatic, so a merge-triggered production deploy is
   refused *by construction* rather than by a setting someone can forget. Promoting to production stays the
   manual *Deploy procedure* below.
5. **Integration tests** run against **staging** after a merge to `staging` *(not yet wired — this is the staging
   tier; the venue-independent **pre-merge tier already runs in CI**, step 2 / gh#121)*.
6. On **production** deploy, a **smoke-test subset** runs; a failure **flags the release for rollback** — pin the
   previous `:sha-<short>` tag to roll back to an exact prior build *(the smoke subset is not yet wired, with
   step 5)*. The **`verify` job is wired** (`gh#379`) for the environments step 4 deploys: it probes
   **`GET /ready`** on the deployed instance via `scripts/verify-deploy.sh`, retrying while the container starts
   and applies migrations. `/ready` rather than `/health` on purpose — `/health` answers from the process and
   returns 200 even when the database is unreachable, which is precisely the failure a post-deploy check exists
   to catch. A deploy whose base URL secret is missing **fails**: an unverified deploy is not a successful one.
   The script is runnable locally (`scripts/verify-deploy.sh http://localhost:8080`), so the local check and the
   gate cannot disagree.

### Operator setup — console actions CI cannot do (ADR-0018)
The build/push half is code (`.github/workflows/ci.yml`). These are one-time console steps, recorded here because
configuration that lives only in a provider console is otherwise invisible to anyone reading the pipeline:
1. **After the first CI publish**, set the GHCR package `trading-copilot` visibility to **public** — GHCR creates
   packages **private** by default, so the first pull (local or Railway) fails until this is flipped.
2. **Reconfigure the Railway service** to deploy from the image `ghcr.io/adammarquette/trading-copilot:<branch>`
   rather than building from the repo source. No pull credential is needed (public image).
3. Until step 2 is done, Railway still builds from source; the GHCR image is used by local dev only. The pipeline
   is deliberately left this way rather than half-wiring a deploy trigger against a not-yet-image-sourced service.
   The `deploy` job added by `gh#379` honours that: with no hook secret it **skips**, so it cannot fire against a
   service that would rebuild from source instead of pulling the tested artifact.
4. **Create the `dev` and `staging` environments** in the Railway project and map them per *Environments ↔
   branches*. A new Railway project starts with a single environment, so both are missing today.
5. **Set the per-environment secrets.** Two of them turn the pipeline on, and nothing else does:

   | Secret (GitHub Actions) | Purpose |
   |---|---|
   | `RAILWAY_DEPLOY_HOOK_DEV` / `RAILWAY_DEPLOY_HOOK_STAGING` | Railway deploy-hook URL. **Absent ⇒ the deploy job skips.** |
   | `DEPLOY_BASE_URL_DEV` / `DEPLOY_BASE_URL_STAGING` | Public base URL of the deployed instance, for the `/ready` probe. **Absent after a deploy ⇒ the verify job fails.** |

   The application's own secrets (ProjectX credentials + endpoints, DB connection, OTLP) are **Railway
   environment variables**, never GitHub secrets and never in source — CI triggers a deploy, it does not carry
   the app's configuration.

   > ⚠️ **The ProjectX credential mapping is the safety-critical step in this entire setup.** `dev` and `staging`
   > are **practice-only**; a **live** account belongs to `production` and nowhere else (R-14). Nothing below this
   > mapping can catch a mistake — the application cannot tell it was handed live credentials in staging, and the
   > first symptom is a real order on real money. Verify the account for each non-prod environment **at the
   > broker** after setting it, not from the value you believe you pasted.

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
| `protect-develop` (**active**) | `refs/heads/develop` | `build & unit tests` · `commit-hygiene` · `integration tests (pre-merge)` | PR required (0 approvals) · **`strict` ON** — must be up to date with `develop` (gh#575) · block force-push · block deletion |
| `protect-staging` (**active**) | `refs/heads/staging` | the develop set **+ `ladder`** | PR required (0 approvals) · block force-push · block deletion |
| `protect-main` (**active**) | `refs/heads/main` | the develop set **+ `ladder`** | PR required (0 approvals) · block force-push · block deletion |

- **`ladder` is required on `staging`/`main`**, so a promotion PR from a disallowed source fails the check and
  cannot merge — this is what turns the advisory ladder guard into hard enforcement of `staging ← develop` and
  `main ← staging`.
- **`stale-base` and `publish image (GHCR)` are deliberately NOT required** — `stale-base` is skipped on the
  long-lived branches and `publish image` runs only on `push`, so requiring either would leave a required check
  forever pending and **deadlock the merge**. This is the trap to remember before adding any required check:
  confirm it actually runs on that branch's PRs.
### Combining-PR protection on `develop` — `strict`, because the merge queue is unavailable (gh#357, gh#575)

> **The merge queue cannot be enabled on this repository.** Adding the `merge_queue` rule to `protect-develop`
> fails with `422 — Invalid rule 'merge_queue'`, and retrying with **no parameters at all** gives the identical
> error, so it is the rule *type* being rejected rather than a bad payload. **Cause:** the repo is
> `owner.type = User` — merge queue requires an **organization-owned** repository, and public visibility alone is
> not enough. That is why the checkbox is absent from the ruleset UI. (gh#357 assessed availability on visibility
> and missed ownership.) Moving the repo under an organisation would unlock it — a real option, and its own decision.
>
> **In force instead: `strict_required_status_checks_policy = true`** on `protect-develop`, applied 2026-07-30 via
> `PUT /repos/adammarquette/trading-copilot/rulesets/19715669`. A PR must be **up to date with `develop`** before it
> can merge, so CI always compiles the combination that will actually land. Same gap closed, by **serialising**
> rather than batching — at the cost gh#357 documented: each merge invalidates every other open PR, which then needs
> a rebase and a full (~4 min) CI re-run. With many parallel sessions that is a real tax; it is accepted because a
> broken `develop` blocks every downstream PR, and some guard beats none.
>
> **The `merge_group:` triggers in `ci.yml` / `branch-policy.yml` stay** (gh#357). They are **inert** with no queue
> — the event never fires — and cost nothing. Keeping them means enabling the queue is a one-checkbox change if this
> repo ever moves under an organisation. **Do not delete them as dead config.**

The rest of this section describes the gap being closed and why a queue was preferred; it stands regardless of which
mechanism enforces it.

### Merge queue on `develop` — the design, if it ever becomes available (gh#357)

**The gap it closes.** Required checks prove each PR green **against its own base**. Two PRs that are each green
can still break `develop` once both land — different files, so no git conflict, and no CI run ever compiled the
combination. That is exactly what happened on 2026-07-28 (`gh#351`): two PRs merged 23 seconds apart, one adding a
4-arg constructor and the other a test constructing it with 2. The merge queue closes it by testing each queued PR
**stacked on the base plus everything ahead of it in the queue**, and merging only what is green.

`stale-base` is **not** the guard for this and is unchanged — it catches a *stacked* PR whose base already merged
(`gh#72`), a different failure. The `gh#351` write-up misattributed it; `gh#357` corrected that.

**Why a queue *would be* preferred over "require branches to be up to date" (`strict`) — the trade being paid for now.** `strict` works, but every merge to
`develop` invalidates every other open PR, each then needing a rebase and a full ~4-minute CI re-run. This repo runs
many parallel agent sessions, so that serialises merges and produces near-constant rebasing. A queue batches instead
of serialising. (`strict` remains the interim fallback if the queue proves awkward.)

**Settings that *would* apply — recorded for the org-owned future, not currently actionable.** The order matters:
enabling a queue **before** the workflow triggers exist leaves every queued PR waiting on checks that never run.

1. **The workflow triggers are already landed** — `ci.yml` and `branch-policy.yml` both carry `merge_group:`
   (`gh#357`). A required check that does not run on `merge_group` deadlocks the queue, the same trap recorded
   above for `stale-base` and `publish image`.
2. **Then** it would be enabled on `protect-develop` → *Require merge queue*, with:
   - **Merge method: `Rebase`** — **set this explicitly.** The default is a merge commit, which would break
     `gh#104` ("PRs land by rebase-merge; every branch commit becomes permanent history") and put merge commits on
     `develop`, where they are reserved for `develop → staging → main` promotions.
   - Build concurrency and batch size left at the defaults until there is evidence to tune them.
3. `protect-staging` / `protect-main` are **deliberately not queued**: each takes exactly one curated source
   (`develop`, then `staging`), so there is no combination to test and a queue would only add latency.

**Interaction with `copilot-review-develop`.** The Copilot-review rule is a **pull-request** requirement, satisfied
before a PR can be queued, so it should not interact with the queue at all. That is the expectation, not a verified
fact — **confirm it on the first queued PR**, because this rule has already been observed to block merges silently
with every check green and nothing in the checks tab (see *Automated code review* above).

**What it would look like.** Merging would become *Merge when ready*: the PR joins the queue, GitHub creates a
temporary `gh-readonly-queue/develop/...` ref, CI runs against it, and the PR merges only if that run is green — so
a PR can now be rejected by the queue after passing its own checks. That is the mechanism working, not a fault: it
means the combination broke, and the fix is to rebase onto the new `develop` and resolve it.

- **`issue-link` is deliberately NOT required, and never fails** (gh#406). It warns when a PR references issues
  but will close none of them — the shape that let `#385` stay open after PR #391 delivered it, costing a full
  duplicate implementation (PR #401, closed unmergeable). It is advisory *by design*: a PR against an epic, or a
  QA suite that **pins** a defect rather than fixing it, legitimately closes nothing, so a hard failure would
  train authors to add `Closes` where it does not belong — and a wrongly-closed issue is harder to notice than
  one left open. Promoting it to required would need the false-positive rate measured first.
  It reads GitHub's own **`closingIssuesReferences`** rather than pattern-matching the body, because that is the
  set that will actually close on merge; a keyword in the **title** binds nothing, which is a real recurring
  mistake here (PRs #369, #376, #425 all carried one). It additionally names two near-misses it finds: a keyword
  in the title, and prose like *"Settles #307"* that reads like a link and is not.
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

## Observability stack (local, opt-in)

The self-hosted LGTM stack (`gh#231`, [ADR-0002](adr/0002-observability.md)) sits behind the **`observability`
compose profile**, so it is **off by default**:

```bash
docker compose up -d                              # app + db only
docker compose --profile observability up -d      # ...plus the stack
```

| Service | Port | What it holds |
|---|---|---|
| Grafana | 3000 | the single pane; datasources **and dashboards** provisioned from `./observability/grafana` (`gh#366`) |
| Prometheus | 9090 | metrics (remote-write receiver + exemplar storage enabled); evaluates `./observability/rules` |
| Alertmanager | 9093 | routing, dedup, quiet hours and the Pushover receivers (`gh#245`, ADR-0019) |
| Loki | 3100 | logs |
| Tempo | 3200 | traces |
| OTel Collector | 4317 / 4318 | the only address the app exports to |

**The app exports to the collector and knows no backend.** Swapping a backend, adding a second destination, or
sampling is a change to `./observability/otel-collector-config.yaml`, not to application configuration.

**Footprint** (measured 2026-07-26, idle): the five backends running then totalled **~263 MB RAM** — Tempo 102,
Grafana 63, Loki 40, collector 33, Prometheus 25. **Alertmanager** was added afterward (2026-07-27, `gh#245`) and is
**not** in that figure — it is capped at `mem_limit` 256m, so budget a little more for the **six-service** stack.
Limits are set well above measured usage (`mem_limit` **256–512 MB** each) so a busy stack has headroom without
being able to exhaust the host. Images total ~1.7 GB on first pull. Retention is **7 days** on all three backends.

**Grafana credentials** default to `admin`/`admin` for local development only, from `GF_SECURITY_ADMIN_USER` /
`GF_SECURITY_ADMIN_PASSWORD`. A deployed Grafana takes them from that environment's secret store — never from a
committed file, and never left at the default.

### Dashboards (`gh#366`)

**Four** dashboards provision from `./observability/grafana/dashboards`, in the **Trading Co-Pilot** folder:

| Dashboard | UID | What it answers |
|---|---|---|
| Auto-flatten reliability | `tc-auto-flatten` | Did R-13's obligation run, how fast, did the backstop save it |
| Execution & risk gate | `tc-execution-gate` | Gate coverage, which limit binds, order-ack latency, kill switch, unprotected exposure |
| Synthetic risk & pipeline health | `tc-synthetic-risk` | Platform-held protection, and whether the log's consumers keep up |
| AI usage & spend (`gh#412`) | `tc-ai-spend` | What the AI is costing — 24h/30d spend, governor headroom, LLM-vs-embed split, spend by tier, outcomes, tokens, p95/p99 latency (`ai_llm_*` gh#477 + `ai_embed_*` gh#403) |

> **The headroom panel reads the budget from the app, not from a constant (`gh#506`).** *"Governor headroom — % of
> daily budget"* divides by the **`ai_governor_daily_budget_usd`** gauge, which the app publishes from its own
> `Governor__DailyBudgetUsd` config. So changing the governor's budget is a **single** change — the panel follows it,
> and cannot silently report against a stale denominator. (It briefly *was* a hand-synced Grafana `constant`; gh#506
> replaced that precisely because the two could drift apart.)

**They are read-only in the UI on purpose.** The provider sets `allowUiUpdates: false` and the mount is
read-only, so a dashboard cannot drift into console state that no PR reviewed. **To change one, edit the JSON and
commit it** — Grafana re-reads every 30 s, so a local edit shows up without a restart.

**The most important thing to know when reading them:** on the auto-flatten board, **a blank panel is the alarm,
not a quiet day.** The deadline metric is emitted on every evaluation including `nothing-to-do`, so absence means
the loop did not run. The board says this in a text panel and reports *reporting / SILENT* as a stat rather than
leaving it to be inferred.

To check they loaded after a change:

```bash
curl -s -u admin:admin "http://localhost:3000/api/search?type=dash-db"
```

### Alerting — receiver configuration (`gh#245`, ADR-0019)

Alerting is **Layer 1** of ADR-0019: Prometheus evaluates `./observability/rules/*.yml`, Alertmanager routes them
to Pushover. Layer 2 — the dead-man's switch (`gh#244`) — is external by design and does **not** depend on any of
this.

Set three values in `.env`:

| Variable | What it is |
|---|---|
| `PUSHOVER_USER_KEY` | your Pushover user key |
| `PUSHOVER_API_TOKEN` | the Pushover **application** token |
| `ALERTMANAGER_HEARTBEAT_URL` | a dead-man's-switch check URL (healthchecks.io, Dead Man's Snitch, …) |

**These are separate variables from the app's `Pushover__*` settings on purpose.** Those configure the app's own
direct push (the fast path, `gh#243`); these configure the rule engine's backstop. They may hold the same values,
but they are read by different processes — and a stack whose alerting silently inherited the app's config would
give you no way to test one without the other.

**Alertmanager has no environment-variable expansion in its config**, unlike almost everything else in this
stack. A `${PUSHOVER_API_TOKEN}` written into `alertmanager.yml` would be sent to Pushover as that literal
string — the config loads, the stack looks healthy, and every page fails authentication. The receivers therefore
use `token_file` / `user_key_file` / `url_file`, and compose materialises those files from `.env` through a
`secrets:` block. **Do not "simplify" this back to `environment:`.**

An unset token does not stop the container starting — deliberately, so a bad credential cannot take down the
thing that reports every other failure. Which is why the next step is not optional.

#### Send a test page (do this after any credential change)

Do **not** wait for a real incident to discover the pager is broken. Fire a synthetic alert straight at
Alertmanager:

```bash
curl -s -XPOST http://localhost:9093/api/v2/alerts -H 'Content-Type: application/json' -d '[{"labels":{"alertname":"TestPage","priority":"P1","component":"flatten"},"annotations":{"summary":"Test page — ignore","description":"Verifying the Pushover receiver end to end."}}]'
```

A P1 must arrive as a Pushover **Emergency** notification that repeats until you acknowledge it. If nothing
arrives, check `docker compose --profile observability logs alertmanager` — an authentication failure appears
there as a notify error.

Substitute `"priority":"P2"` to check the quiet-hours behaviour: it should arrive between 06:00–17:00 CT and be
suppressed outside that window. **A P1 is never suppressed** — that is the entire point of the tier.

#### Verify the rules without deploying

The rules carry executable tests, including a **clean-session fixture** asserting a normal day pages nobody:

```bash
docker run --rm -v "$PWD/observability:/obs" -w /obs/rules/tests --entrypoint promtool prom/prometheus:v3.0.1 test rules trading-alerts-test.yml
```

#### Confirm the alerting chain is alive

The `Watchdog` rule fires **permanently** by design and posts to `ALERTMANAGER_HEARTBEAT_URL` every 5 minutes;
its **absence** is what the external check alarms on. To confirm the chain end to end:

```bash
curl -s http://localhost:9090/api/v1/alertmanagers   # Prometheus must list the Alertmanager
curl -s http://localhost:9093/api/v2/alerts          # Watchdog must be present and firing
```

While `ALERTMANAGER_HEARTBEAT_URL` is left at its placeholder default, that route logs a delivery failure every
5 minutes. That is intentional — an unconfigured dead-man's switch should be visible rather than silent — and it
stops as soon as a real URL is set.

**Everything is provisioned as code.** Nothing in this stack is configured by clicking, and
`docker compose --profile observability down && up` returns the same stack (verified, including datasource
re-provisioning and volume persistence). `prometheus.yml` already mounts a `rules/` directory, so `gh#245`'s
alerting rules arrive as reviewable files rather than console state.

## When a page arrives

Every alert's `runbook` annotation links to one of the sections below — these are what you read at 03:00, so
they lead with the action, not the explanation. **Assume the system is already doing what it can**: the
always-native safety stop is the physical floor throughout every one of these.

*(These four sections exist because `gh#245` shipped rules whose `runbook` annotations pointed at anchors that
did not exist. A page linking nowhere is worse than one with no link — `gh#370` added them.)*

### Auto-flatten failure

**Alert:** `FlattenEscalated` (P1) · `FlattenMissed` (P1)

Exposure is open past its deadline and the system could not close it. `FlattenEscalated` means the primary tier
exhausted its attempts; `FlattenMissed` means the firing window passed with exposure remaining — on the
`watchdog` tier that is the last line having failed.

1. **Flatten manually, now**, in the broker platform. Do not wait for the next watchdog pass — if it is escalating,
   three closes already failed against the same venue.
2. Check whether the venue is rejecting orders generally (`VenueDisconnectedWithExposure`, broker status page).
3. Afterwards: `flatten.escalated` / `flatten.missed` journal entries carry the reason per attempt.

Prop-firm note: Topstep's own backstop runs ~15:10 CT. `FlattenMissed` fires **after** it, so a P1 here means the
firm may already have acted.

### Unprotected position

**Alert:** `UnprotectedPosition` (P1)

The venue reports a live position with **no stop order resting behind it** — the state the staged-stop model
exists to make impossible. The census (`gh#370`) compares venue positions against venue working orders every
30 s, so this is venue truth, not a local belief.

1. **Place a protective stop manually, or flatten the position.**
2. Then find out why: was the entry's bracket rejected at attach; did a cancel remove the wrong leg; is the
   working stop still `Hidden` and not yet promoted (which is normal, but the *safety* stop should be native
   regardless)?
3. `trading_positions_unprotected` and the `ERROR` log from `ProtectionMonitorService` name the count.

This fires only after 2 minutes: a bracket attaches on fill, so a brief unprotected window during entry is
normal and deliberately not paged.

### Orphaned stops

**Alert:** `OrphanedStopsWithExposure` (P1)

A venue-connection drop moved working stops to `Orphaned` — protection is platform-held rather than
exchange-held (ADR-0007's `synthetic_risk`). The native safety stop is still resting at the exchange.

1. Check the connection first — the orphan guard **re-arms automatically on reconnect** and re-validates each
   stop against venue truth, so a brief drop needs no action.
2. If the connection is up and stops stay orphaned, they could not be re-validated (venue unreachable per-stop).
   Verify each position's protection at the broker directly.
3. Persisting past a session: treat as unprotected and act as above.

### Backfill shortfall

**Alert:** `BackfillShortfall` (P2)

A consumer fell off the back of the 24h event-log retention window, and the gh#306 recovery could **not** cover
all of it from the clean-historical bar store. The label carries the contract; the histogram carries how much of
the window has no bars.

**What it means concretely:** hidden stop plans on that contract may have crossed their promotion band while the
system was blind, and were **not** promoted to venue-held stops. The **native safety stop is still resting at the
exchange** — this is a degraded floor, not an unprotected position, which is why it is a P2 rather than a page.

1. **Identify the exposure.** Which open positions are on the labelled contract, and do they have a working stop
   at the venue or only the safety stop? Check the broker directly — a local record is a belief, and this alert
   exists because a belief was wrong.
2. **The next quote self-heals it.** `StopPromotionService` re-evaluates hidden plans on every quote, so a
   contract that is still trading will promote on its own once price revisits the band. No action is needed for a
   position you are happy to leave on its safety stop until then.
3. **Act before the session close** if the tighter stop matters — promote by hand at the broker, or flatten.
   After hours there are no quotes, so nothing will self-heal.
4. **Repeating shortfalls are a bar-coverage problem, not an alerting one.** Check that the instrument is in
   `Backfill__Instruments` and that ingestion is actually storing bars for it; a contract that is traded but never
   backfilled will shortfall on every gap.

### Telemetry pipeline

**Alert:** `TelemetryPipelineSilent` (P1)

No flatten-deadline metric for 15 minutes. The flatten loop emits one on **every** evaluation including idle
ones, so silence means the app is down, the collector is broken, or remote write stopped.

**Read this as *unmonitored*, not as *healthy*.** Every other rule in the file is blind while it fires, which is
why the flatten alerts are inhibited by it — the actionable page is this one.

1. Is the app running? `docker compose ps`, then its logs.
2. Is the collector up and receiving? `docker compose --profile observability logs otel-collector`.
3. Until it clears, **check positions manually at the broker** — the automation may be fine and merely unobserved,
   or it may be down. You cannot tell from here, which is the point of the alert.

## The dead-man's switch (operator setup — required before live)

The **only** alerting tier that survives this process dying (R-13, `gh#244`,
[ADR-0019](adr/0019-alerting-channel-and-thresholds.md)). Every other alert assumes something is alive to raise it;
if the host dies before an auto-flatten deadline, the flatten never fires **and nothing alerts**. The app reports to
an external monitor, which pages when the report **fails to arrive**.

**A deployment without it is silently missing its most important safety net.** The app starts and warns loudly, but
nothing else notices.

0. **Set `Pushover__AppToken` and `Pushover__UserKey`** — the app's *own* alerting channel (`gh#243`), separate from
   the monitor's. Without them the app still runs and still detects everything; it just writes the alert to the log
   instead of your phone, warning at startup that it is doing so. A Page that reaches no one logs as an **error**.
   Under compose these are forwarded only because they are named in the app service's `environment:` map.
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
  **What a Postgres without pgvector actually costs (gh#109, settled):** the app **still starts and still trades** — the `Embeddings` table is simply not created and semantic retrieval is off. That is deliberate: refusing to start would let a retrieval feature take down the safety-critical auto-flatten (R-13), and nothing on the trading path depends on embeddings. It is **not silent** — the migration raises a `WARNING` naming the consequence, and the embedding provider reports itself unavailable so retrieval refuses rather than returning empty results that read as "nothing is relevant". **The provider half landed in gh#474** — before it, a key set on a Postgres without the extension embedded on every poll (real spend) and faulted at the upsert; availability is now probed at startup and means the whole round trip. Verified both ways: `timescale/timescaledb-ha:pg17` creates the table and its HNSW index; plain `postgres:17` skips it and creates the other 24 tables normally. **Timescale is the harder constraint** — its degrade loses compression and retention on the data path; pgvector's loses an optional feature.
- The Railway deploy integration (CLI / MCP / GitHub trigger) — the GitHub Actions workflows themselves exist
  (`ci.yml` + `branch-policy.yml`; §CI/CD above).
- Create the `dev` + `staging` Railway environments and map branch → environment.
- Non-prod **snapshot refresh** cadence + mechanism (§8).
- Define the **smoke-test set** and the health / verify checks.
