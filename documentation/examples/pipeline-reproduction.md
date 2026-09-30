# Pipeline reproduction — a portable specification of the build pipeline (target-shape example)

> **Example of the pipeline shape this project is aiming for, not a description of it (gh#1218).** Imported from a
> sibling project's GitLab CI, with its name replaced by placeholders. Its jobs, runner tags, variables and
> secrets are that project's. This repo's actual pipeline is
> [`.github/workflows/`](../../.github/workflows/) and the [Platform contract](../agents/platform.md); where they
> differ, they win. Use this to see what a complete pipeline covers (stages, gates, self-tests, deploy and
> verify), then decide what to build.

This document says **what a new CI host must reproduce** to run this repository's pipeline, and how each
piece maps onto GitHub Actions. It is the portable view. [`CI-SETUP.md`](CI-SETUP.md) is the operating
view — why each gate exists, what went wrong on this project's hosts, and the history. When the two
disagree, the pipeline files win: [`.gitlab-ci.yml`](../.gitlab-ci.yml), the scripts it calls, and
[`.github/workflows/`](../.github/workflows/).

Scope: it specifies the pipeline as it stands on `develop`. Making the pipeline definition itself
portable (one definition, two hosts) is a separate piece of work, `gl#476`; this document does not do it.

**Contents:** §1 the shape of the pipeline · §2 every job, by stage · §3 prerequisites · §4 mapping to
GitHub Actions · §5 standing it up on a new host.

---

## 1. The shape of the pipeline

### Which pipelines exist

`.gitlab-ci.yml`'s `workflow:` block creates MR and schedule pipelines, plus a pipeline from **any**
source on `main`, `develop` or `staging`: a push, a UI run, an API or trigger call, a parent
pipeline. Tags and pushes to feature branches create **no** pipeline. The triggers the jobs
distinguish are:

| Trigger (used in the tables below) | GitLab source | What it is for |
|---|---|---|
| **MR** | `merge_request_event` | Every change before merge. Runs on the **source branch**, not a merge preview (GitLab CE has no merged-results pipelines) |
| **develop** | `push` to `develop` | Lint, build, test, then **publish every image and deploy the staging environment** |
| **staging** / **main** | `push` to `staging` or `main` | Lint, build, test. `main` may also run the production apply (below). Neither publishes |
| **schedule** | `schedule` (a project setting, nightly, targeting `develop`) | Only `railway-drift` and `post-deploy-verify`. Every other job opts out. **No schedule exists on 2045** (§3) |
| **web** | "Run pipeline" in the UI, on a protected branch | Offers the manual jobs: the two plans, drift, post-deploy-verify |

Feature branches run through the MR trigger only. A branch-push rule would run each commit twice.

Tags are excluded on purpose. Protected tags receive protected variables, and pushing a tag is not
gated the way pushing to a protected branch is.

### The promotion chain

The three long-lived branches are a chain, not three trunks:

1. **`develop`**: every push builds each component once as `<component>-sha-<first 12 of the commit>`,
   pushes it to `docker.io/amarquette/gauntletai`, and applies exactly those tags to the **staging**
   environment in the same pipeline.
2. **`staging`**: gates `main`. It builds nothing and deploys nothing.
3. **`main`**: rebuilds nothing. Production runs explicit `-sha-<12>` pins in `.railway/railway.ts`,
   copied from a build staging already ran. Applying them is an operator step (`DEPLOYMENT.md` §9).

### Stages and ordering

`stages: [gate, lint, build, test, publish, deploy]`. A job starts when the previous stage has
succeeded, unless it declares `needs:`:

- **`gate` runs first and fails fast.** An unapproved or unmergeable MR stops in seconds, before any
  lint, build or test job runs. A red `review-verdict` is the normal state of an MR until a reviewer
  approves it.
- **Some jobs carry `needs: []` and start immediately.** On an MR pipeline that is only
  `verdict-selftest` and `mergeability-selftest`: they prove the gate still works, so they must not sit
  behind it. The others are the two manual plan jobs, `railway-drift` and `post-deploy-verify`, and
  none of those is created on an MR pipeline.
- **The five `test` jobs `needs: [build]`**, the publish jobs need four of them (not
  `contract-schemas`), and the staging deploy needs those four and all four publish jobs.
  `image-scan` needs the four publish jobs (`gl#835` added the fourth), and the two DAST jobs need the
  staging apply (`gl#811`).
- **Two resource groups** serialise the environments: `railway-staging` (staging apply and the staging
  integration tests) and `railway-production` (production apply, drift, post-deploy-verify).

Jobs that set no `rules:` inherit `.rules.not-scheduled`: they run on MR, `develop`, `staging` and `main`
pipelines, and never on a schedule.

### Confirm the job list, don't count it

Do not trust a hand-typed job total, here or anywhere. Ask GitLab:

```bash
jq -n --rawfile c .gitlab-ci.yml '{content:$c, include_jobs:true, ref:"develop"}' |
  curl -s -X POST -H "PRIVATE-TOKEN: $GITLAB_API_TOKEN" -H 'Content-Type: application/json' \
       --data @- https://labs.gauntletai.com/api/v4/projects/2045/ci/lint | jq -r '.jobs[] | "\(.stage)\t\(.name)"'
```

Add `"dry_run": true` to see only the jobs a `develop` push would **create**. §2 below was checked
against both answers on `develop` at `49a8a5b5`.

---

## 2. Every job, by stage

Columns: **Trigger** uses the vocabulary in §1. **Inputs** name variables and secrets by name only.
**Fail** is whether the job may fail without stopping the pipeline. Unless a row says otherwise, a job
reads no secret, uploads no artifact, and may not fail.

Shared defaults for every job:

- `tags: [<project>-ci]` (runner routing).
- `GIT_DEPTH: "0"` (full history). The verdict gate's rebase replay, `docs-sync` and the secret
  scan's MR range all need the merge base.
- The in-file variables `SOLUTION=<Project>.slnx`, `BUILD_CONFIG=Release`,
  `UNIT_TEST_PATTERN=*UnitTests.csproj`, the three `DOTNET_*` quieting flags, and
  `NUGET_PACKAGES=$CI_PROJECT_DIR/.nuget/packages`.

Two base images carry most jobs:

- `.dotnet`: `mcr.microsoft.com/dotnet/sdk:10.0`. Caches `.nuget/packages`, keyed on
  `Directory.Packages.props`.
- `.busybox`: `alpine:3.21` plus `sh tools/apk-add-retry.sh bash git` (retries any `apk` failure with backoff, `gl#845`). Many jobs add `jq`.

### 2.1 `gate`

| Job | Trigger | Runs | Inputs | Outputs | Fail |
|---|---|---|---|---|---|
| `review-verdict` | MR | `.gitlab/ci/verdict-gate.sh $CI_MERGE_REQUEST_IID`, which calls `verdict-state.sh` to read the MR's **notes** through the API. Exit 0 = approved, 75 = no fresh approval, 1 = the gate could not answer. Image `.busybox` + `curl jq`; 10-minute timeout | `GITLAB_API_TOKEN` (masked, **not** Protected, `read_api`), `CI_PROJECT_ID`, `CI_API_V4_URL` | — | No. The one exception is a scratch ref matching `^chore/gl[0-9]+-gate-verify$`, which may fail (CI-SETUP §8) |
| `mergeability` | MR | `.gitlab/ci/mergeability-gate.sh <target> <sha>`: a `git merge-tree` of the head against a freshly fetched target. Exit 0 = mergeable, 65 = conflict, 1 = could not tell | Predefined MR variables only | — | No |

### 2.2 `lint`

All `lint` jobs run on MR, `develop`, `staging` and `main` pipelines, except `docs-sync` (MR only).

**Formatting, licences and dependencies**

| Job | Runs | Inputs | Outputs |
|---|---|---|---|
| `format` | `dotnet format <Project>.slnx --verify-no-changes` | — | — |
| `license-scan` | `dotnet restore`, `dotnet tool restore`, then `dotnet-project-licenses` against `.github/licenses/allowed-licenses.json` and `license-packages-filter.json`. The scanner is pinned in `.config/dotnet-tools.json`. Sets `DOTNET_ROLL_FORWARD=LatestMajor` | Network: nuget.org | `licenses-report.json` (7 days, always) |
| `dependency-audit` | Seven steps in order: `tools/verify-nuget-audit-selftest.sh`, `verify-nuget-audit.sh`, `nuget-audit-effective-selftest.sh`, `nuget-audit-effective.sh`, `nuget-audit-fires.sh`, `nuget-audit-report-selftest.sh`, `nuget-audit-report.sh`. The gate itself is `dotnet restore` (NuGetAudit plus warnings-as-errors); this job pins it and proves it still fires | Network: nuget.org | `vulnerability-report.json` (7 days, always) |

**Self-tests of the pipeline's own guards**

Each runs a script that proves a guard still goes red. All use `.busybox` and need no network or
secret.

| Job | Runs | Extra packages |
|---|---|---|
| `verdict-selftest` (`needs: []`) | `.github/scripts/verdict-state-selftest.sh`, `verdict-gate-selftest.sh` | — |
| `mergeability-selftest` (`needs: []`) | `.gitlab/ci/mergeability-gate-selftest.sh`, `merge-gate-selftest.sh`, `verdict-state-e2e-selftest.sh` | `jq` |
| `post-deploy-selftest` | `scripts/post-deploy-verify-selftest.sh` | — |
| `qa-cohort-selftest` | `scripts/qa-cohort-credentials-selftest.sh`: a stand-in OpenEMR that verifies the RS384 client assertion (`gl#748`) | `jq openssl` |
| `railway-guard-selftest` | `scripts/railway-destructive-guard-selftest.sh` | `jq` |
| `railway-apply-wiring-selftest` | `scripts/railway-apply-wiring-selftest.sh`, which extracts the apply jobs' `script:` from `.gitlab-ci.yml` with `yq` and runs them against a stub CLI | `jq yq-go` |
| `counted-selftest-lint` | `tools/counted-selftest-lint-selftest.sh`, `counted-selftest-lint.sh` | — |
| `release-process-selftest` | `scripts/release-process-selftest.sh`: a throwaway git history and a stateful stub of the GitLab API (`gl#789`) | `jq` |
| `snapshot-guard-selftest` | `scripts/railway-snapshot-guard-selftest.sh` | `jq` |
| `backup-schedules-selftest` | `scripts/railway-backup-schedules-selftest.sh`, then `tools/pipefail-reader-lint-selftest.sh` and `pipefail-reader-lint.sh` | `jq` |
| `deploy-identity-selftest` | `scripts/railway-deploy-identity-selftest.sh` | `jq` |
| `stays-up-selftest` | `scripts/railway-stays-up-selftest.sh` | `jq` |
| `docs-sync-optout-selftest` | `.gitlab/ci/mr-description-optout-selftest.sh`, `tools/docs-sync-paths-selftest.sh` | `curl jq` |

**Configuration, infrastructure and documentation checks**

| Job | Runs | Image or packages | Inputs, outputs |
|---|---|---|---|
| `iac-sites-volume-selftest` | `npm ci`, `npm run iac:selftest`: evaluates `.railway/railway.ts` offline | `node:24-alpine` | Network: npm registry |
| `eval-snapshot-panel` | `tools/verify-eval-snapshot-panel-selftest.sh`, `verify-eval-snapshot-panel.sh` | `.busybox` + `jq` | — |
| `openemr-pin` | `tools/verify-openemr-pin.sh`, `verify-local-mirrors-deploy-selftest.sh`, `verify-local-mirrors-deploy.sh` | `.busybox` | — |
| `nginx-config-lint` | Renders `reverse-proxy/nginx.conf.template` twice with `envsubst` (`GRAFANA_UPSTREAM` empty, then set) and runs `nginx -t` on each. Then `sh tools/apk-add-retry.sh curl` and `reverse-proxy/nginx-behavior-selftest.sh`, which runs a real nginx and asserts the blocked routes' `404`s, since `gl#810` the per-client `429`s and, since `gl#816`, the dotfile deny | `nginx:1.30-alpine` | In-file, non-secret: `PORT`, `OPENEMR_UPSTREAM`, `SIDECAR_UPSTREAM`, `DNS_RESOLVER`, `RESOLVER_IPV6`, `REAL_IP_TRUSTED_CIDR` (the Dockerfile default, `100.64.0.0/10`; `gl#810`) |
| `alert-rules-lint` | `observability/alerts/alert-rules-gate-selftest.sh`, `alert-rules-gate.sh` (promtool) | `prom/prometheus:v3.15.0`, entrypoint cleared | — |
| `secret-detection` | `tools/secret-scan-selftest.sh`, then `tools/secret-scan.sh`. On an MR it adds `--range $CI_MERGE_REQUEST_DIFF_BASE_SHA..$CI_COMMIT_SHA`. 15-minute timeout | `ghcr.io/gitleaks/gitleaks:v8.30.1`, pinned by digest | Outputs: `gl-secret-detection-report*.json` (7 days, developer access) |
| `sast` | `tools/sast-scan-selftest.sh`, `tools/sast-scan.sh` (semgrep; exits non-zero on a finding). 15-minute timeout | `registry.gitlab.com/security-products/semgrep:6.26.1`, pinned by digest | Outputs: `gl-sast-report.json` (7 days, developer access) |
| `security-gate-selftest` | `tools/security-gate-selftest.sh`: drives `tools/security-gate.sh`, the pass/fail half of the three deploy-time scans (§2.4, §2.5), and validates the committed `tools/security-gate.allow` (`gl#811`) | `.busybox` + `jq` | — |
| `docs-sync` (**MR only**) | Inline script. Fails when code paths changed between `CI_MERGE_REQUEST_DIFF_BASE_SHA` and the source head but no doc path did, unless the MR description carries a `docs: n/a - <reason>` line. The description is read by `.gitlab/ci/mr-description-optout.sh` | `.busybox` + `curl jq` | `CI_MERGE_REQUEST_DESCRIPTION` (+ `_IS_TRUNCATED`), then `CI_JOB_TOKEN`, then `GITLAB_API_TOKEN` to re-read a truncated description |
| `doc-citation-check` | `scripts/verify-line-citations-selftest.sh`, `verify-line-citations.sh` | `.busybox` | — |

### 2.3 `build` and `test`

All run on MR, `develop`, `staging` and `main` pipelines, on `.dotnet`. None reads a secret. Every
test job has `needs: [build]`, so all six start together once the build is green. Each test job
restores and builds in its own container; no `bin/`/`obj/` artifact is passed between jobs (CI-SETUP
§6).

| Job | Runs | Outputs |
|---|---|---|
| `build` | `dotnet restore`, `dotnet build -c Release` | Writes the NuGet cache (`pull-push`) |
| `unit-tests` | `dotnet test` on every `*UnitTests.csproj`, with junit and trx loggers and `XPlat Code Coverage`. Then `.github/scripts/verify-test-results.sh TestResults` (the false-green guard, which must stay in `script:`), then a `reportgenerator` text summary | `TestResults/` (7 days, always); a junit report; a Cobertura `coverage_report`; a coverage percentage parsed from the log. Coverage is informational only |
| `eval-tests` | `dotnet test tests/<Project>.EvalTests`, then `verify-test-results.sh` | `TestResults/`, junit |
| `hermetic-tests` | `dotnet test tests/<Project>.HermeticTests`, then `verify-test-results.sh`. Fixtures and a scripted model; needs no network | `TestResults/`, junit |
| `integration-tests-no-deployment` | `dotnet test tests/<Project>.IntegrationTests --filter "Deployment=None"`, then `verify-test-results.sh`. The integration classes that need no deployed environment (`gl#839`) | `TestResults/`, junit |
| `evals` | `dotnet run --project tests/<Project>.Evals -- evals`. A hard gate | `evals/results.json` (7 days, always) |
| `contract-schemas` | `scripts/contract-schemas-selftest.sh`, then `contract-schemas.sh render`, then `diff` (with `--base <MR diff base>` on an MR) | `contract-schemas/` (30 days, exposed in the MR), junit |

### 2.4 `publish` — `develop` push only

All four jobs:

- need `[unit-tests, eval-tests, hermetic-tests, evals]`;
- run `docker:27-cli` with a `docker:27-dind` service over TLS (`DOCKER_TLS_CERTDIR=/certs`,
  `DOCKER_HOST=tcp://docker:2376`, `DOCKER_TLS_VERIFY=1`, `DOCKER_CERT_PATH=/certs/client`);
- log in with `DOCKERHUB_USERNAME` / `DOCKERHUB_TOKEN` (masked and Protected);
- build a candidate image, check it before pushing (below), and push to
  `docker.io/amarquette/gauntletai` with the component in the tag.

| Job | Builds | Checks before publishing | Pushes |
|---|---|---|---|
| `publish-image` | `Dockerfile`, repo-root context | `/app/<Project>.Api.dll` is present | `<project>-sha-<12>`, `<project>-develop`, `<project>-latest` |
| `publish-observability-images` (a four-leg matrix: `prometheus`, `grafana`, `loki`, `tempo`) | `observability/<component>/Dockerfile`, repo-root context | Per component: Loki and Tempo validate their own config; Prometheus renders its scrape target and retention flags; Grafana provisions Loki and Tempo only when their URL is set | `<component>-sha-<12>`, `<component>-develop` |
| `publish-proxy-image` | `reverse-proxy/Dockerfile`, **`reverse-proxy/` context** | The template, both entrypoint hooks and the `/grafana` route are in the image | `proxy-sha-<12>`, `proxy-develop` |
| `publish-security-platform-image` | `security-platform/Dockerfile`, **`security-platform/` context** | The image's allowlist names the staging front door and the process refuses the production origin (`gl#807`) | `security-platform-sha-<12>`, `security-platform-develop` |

A fifth job in this stage builds nothing. **`image-scan`** (`gl#811`) needs the four publish jobs and
reads the seven `<component>-sha-<12>` tags they pushed straight from the registry, with no Docker daemon.
It runs `tools/security-gate-selftest.sh`, then `trivy image --download-db-only` (a failure there
exits 2 as `SCANNER ERROR`, never as a finding), then per image `trivy image --format cyclonedx` (the SBOM)
and `trivy image --scanners vuln --format json`, and `tools/security-gate.sh image` on each report. It
fails on severity **HIGH or CRITICAL** not covered by a live entry in `tools/security-gate.allow`, after
deciding every image. Image `aquasec/trivy:0.74.0`, pinned by digest, plus `jq`; the Trivy database is
cached per version, not pinned. Inputs: `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` as `TRIVY_USERNAME`/
`TRIVY_PASSWORD`, for the rate limit only. Outputs: `sbom/<component>.cdx.json` and
`image-scan/<component>.json` (30 days, always). Exit 1 is a finding; exit 2 is a scanner error or a
refused report. It is not `allow_failure`, so a red scan reddens the pipeline, but nothing `needs:` it:
the staging apply does not wait for it.

### 2.5 `deploy`

The Railway jobs extend `.railway`:

- image `node:24-alpine`, plus `sh tools/apk-add-retry.sh bash git curl jq`;
- `npm ci`, then `npm install -g @railway/cli`. The CLI is deliberately **unpinned**, and the plan a
  run produces depends on its version;
- a refusal when `RAILWAY_TOKEN` is empty.

The token comes from the environment template the job extends:

| Template | `RAILWAY_TOKEN` comes from | Also sets |
|---|---|---|
| `.railway-staging` | `RAILWAY_TOKEN_STAGING` | `RAILWAY_ENVIRONMENT_NAME=staging`, `STAGING_BUILD_SHA=$CI_COMMIT_SHA` (read by `railway.ts`) |
| `.railway-production` | `RAILWAY_TOKEN_PROD` | `RAILWAY_ENVIRONMENT_NAME=production`, `RAILWAY_PLAN_JOB=railway-plan-production` |

Both tokens are Railway project tokens, masked and **Protected**. Each token reaches one environment,
so the token is what selects the environment.

| Job | Trigger | Runs | Inputs | Outputs | Fail |
|---|---|---|---|---|---|
| `railway-plan-staging` | **web**, manual, `develop` only | `npm run iac:typecheck`, `railway config plan --verbose --out railway-plan.json` | Staging template | `railway-plan.json`, `railway-plan.txt` (30 days, developer access) | Yes (manual) |
| `railway-plan-production` | **web**, manual, any protected branch | Same as the staging plan | Production template | Same | Yes (manual) |
| `railway-apply-staging` | **develop** push. Needs the four test jobs and all four publish jobs. Resource group `railway-staging`, `environment: staging`, not interruptible | In order: typecheck; plan; `scripts/railway-destructive-guard.sh` (only exit 0 continues); `railway-deploy-identity.sh run` wrapping `railway-snapshot-guard.sh run` wrapping `railway config apply --plan railway-plan.json --yes`; `railway-stays-up.sh`; `post-deploy-verify.sh $STAGING_FRONT_DOOR_URL --wait 120` | Staging template; `STAGING_FRONT_DOOR_URL` (public; set in the file, a CI/CD variable overrides it); `scripts/railway-data-refreshable.json` | `railway-plan.json`/`.txt`, `railway-deploy-identity.json` (30 days, developer access); a GitLab deployment record | No |
| `staging-integration-tests` | **develop** push. Needs `railway-apply-staging`; same resource group | Checks the one-origin rule; `dotnet build` the integration project; `pwsh …/playwright.ps1 install --with-deps chromium`; `post-deploy-verify.sh --wait 180`, so staging answered immediately before the suite (`gl#788`); `apt-get install jq` and `qa-cohort-credentials.sh`, which derives the two patient ids and a per-run introspection client from the QA system client and the cohort's `AF-DEMO-NN` identifiers (`gl#748`); `dotnet test tests/<Project>.IntegrationTests`; `verify-test-results.sh` | `STAGING_FRONT_DOOR_URL`, the `OpenEmrQa__*` set (`System__PrivateKeyPath` is a **File** variable), `OpenEmrAgenda__ClientId`/`__ClientSecret`, `LlmQa__ApiKey`/`__Model` (real LLM spend), `<Project>DataQa__ConnectionString`. All Protected. The patient ids and `TestClient*` are derived, not read. The full list is in `DEPLOYMENT.md` §5 | `TestResults/`, junit | No. It reports on a deploy that has already happened; it does not gate one |
| `dast-zap-baseline` | **develop** push. Needs `railway-apply-staging`; no resource group. Image `zaproxy/zap-stable:2.17.0`, pinned by digest | `security-gate-selftest.sh`; `security-gate.sh target $STAGING_FRONT_DOOR_URL` (refuses anything but our staging front door, `reverse-proxy-staging-5c25.up.railway.app`, whose host is pinned in the script); `zap-baseline.py -m 1 -T 10 -I` (one-minute spider, passive rules only); `security-gate.sh zap`, which fails on risk **High** (`gl#811`) | `STAGING_FRONT_DOOR_URL` | `dast/zap-baseline.json`, `.html` (30 days, developer access, always) | No. It reports on a deploy that has already happened |
| `dast-nuclei` | **develop** push. Needs `railway-apply-staging`; no resource group. Image `projectdiscovery/nuclei:v3.11.1`, pinned by digest, plus `git jq` | The self-test and target check as above; a depth-1 clone of `nuclei-templates` at `NUCLEI_TEMPLATES_TAG`, refused unless it is `NUCLEI_TEMPLATES_COMMIT`; `nuclei` over `http/misconfiguration`, `http/exposures`, `http/technologies` and `ssl`, excluding the `intrusive,dos,fuzz,bruteforce` tags, interactsh off, 30 requests a second; `security-gate.sh nuclei`, which fails on severity **high or critical** | `STAGING_FRONT_DOOR_URL`; github.com for the templates | `dast/nuclei.jsonl` (30 days, developer access, always) | No |
| `release-image-labels` | **develop** push. Needs the four publish jobs. Resource group `release-image-labels`, not interruptible. Image `alpine:3.21` + `bash git jq curl` | `scripts/release-process.sh label --build $CI_COMMIT_SHA`: labels every open `work::done` issue whose merged MRs are all ancestors of the commit `image::<12>`, replacing the previous one, and prunes `image::` labels no open issue carries (`gl#789`, `DEPLOYMENT.md` §9) | `RELEASE_API_TOKEN` (Protected, `api` scope), passed to the script as `GITLAB_API_TOKEN`; refuses when unset | Issue labels on the tracker | No |
| `railway-apply-production` | **main** push, only when `.railway/**/*` or `package.json` changed. Resource group `railway-production` | `.gitlab/ci/railway-plan-artifact.sh`, which fetches the plan a `railway-plan-production` job pinned for this commit and **fails closed** when none exists (the normal case today). Then the destructive guard, the same identity/snapshot/apply nesting as staging, and `railway-stays-up.sh` | Production template; `GITLAB_API_TOKEN` (to find the pinned plan) | `railway-plan.json`, `railway-deploy-identity.json` (30 days) | No |
| `railway-drift` | **schedule**, or **web** manual on a protected branch. `needs: []`, resource group `railway-production` | `railway config plan --detailed-exit-code`, then `railway-destructive-guard.sh --require-clean` as the last line. Plans only; never applies | Production template | `railway-drift.txt`, `railway-drift.json` (30 days) | Scheduled run: no. Manual: yes |
| `post-deploy-verify` | **schedule**, or **web** manual on a protected branch. `needs: []`, resource group `railway-production` | `scripts/post-deploy-verify.sh $FRONT_DOOR_URL --wait 120` against production's front door. Image `alpine:3.21` + `bash curl` | `FRONT_DOOR_URL` (public, unmasked) | — | Scheduled run: no. Manual: yes |

**Not jobs, but part of the pipeline's contract:**

- `.gitlab/ci/merge-gate.sh` is the sanctioned merge path. It refuses unless a fresh approval names the
  head being merged. GitLab runs nothing at merge time, so nothing enforces it.
- `post-verdict.sh` and `watch-verdict.sh` are the reviewer's and the author's side of the verdict
  gate.

`MR_WORKFLOW.md` and CI-SETUP §8 own all three.

---

## 3. Prerequisites

### Runner and host

- **A Docker executor.** Every job names an `image:`; a shell executor ignores it and the jobs break.
  Runners must accept the `<project>-ci` tag. On another host, give them whatever label the jobs are
  routed by.
- **`privileged = true` and a `/certs/client` volume** in `[runners.docker]` on **every** runner that
  can take a publish job. Without them the dind service dies. The job then fails at `docker login`
  with `open /certs/client/ca.pem: no such file or directory`, which names neither cause. This lives
  in the runner's own `config.toml`, not in source (CI-SETUP §0, item 1).
- **Outbound network.** An allowlisted runner needs every host below. A missing mirror fails at the
  gate, because both gate jobs start with `apk add`:
  - image registries: `mcr.microsoft.com`, Docker Hub, `ghcr.io`, `registry.gitlab.com`;
  - package mirrors: the Alpine repository `dl-cdn.alpinelinux.org` (every `apk add`: the `.busybox`
    jobs, `.railway`, `nginx-config-lint`, `post-deploy-verify`), and the SDK image's Ubuntu apt
    mirrors `archive.ubuntu.com` and `security.ubuntu.com` (`playwright.ps1 install --with-deps` and
    `apt-get install jq` in `staging-integration-tests`);
  - `api.nuget.org`, the npm registry, and Playwright's browser download CDN;
  - the GitLab API (the verdict gate, `docs-sync`, the production plan lookup), Railway's API
    (`backboard.railway.com`), and the staging and production front doors;
  - for `staging-integration-tests` only: the LLM provider's API (`LlmQa__ApiKey`) and the Postgres
    host in `<Project>DataQa__ConnectionString`.

### Images, tools and runtimes

| What | Where it comes from |
|---|---|
| .NET 10 SDK, PowerShell (`pwsh`, for Playwright) | `mcr.microsoft.com/dotnet/sdk:10.0` |
| .NET tools (`dotnet-project-licenses`, `reportgenerator`) | `.config/dotnet-tools.json`, restored with `dotnet tool restore` |
| bash, git, curl, jq, yq-go | `alpine:3.21` + `apk add` per job. BusyBox userland, not GNU: the shell gates are written for it |
| Node 24, TypeScript, the `railway` IaC SDK, the Railway CLI | `node:24-alpine`; `npm ci` from `package-lock.json`; `npm install -g @railway/cli`, unpinned |
| nginx, promtool | `nginx:1.30-alpine`, `prom/prometheus:v3.15.0` |
| gitleaks, semgrep | Images pinned by digest in `.gitlab-ci.yml`. The digest also pins the rule set |
| Trivy, ZAP, Nuclei | `aquasec/trivy:0.74.0`, `zaproxy/zap-stable:2.17.0`, `projectdiscovery/nuclei:v3.11.1`, each pinned by digest. Nuclei's templates are pinned by release tag **and** commit; Trivy's vulnerability database is deliberately not pinned |
| Docker CLI and daemon | `docker:27-cli` with a `docker:27-dind` service |

### Credentials and variables

Names only. CI-SETUP §0 and §10 say how each one is created.

| Name | Kind | Protected? | Used by |
|---|---|---|---|
| `GITLAB_API_TOKEN` | API token, `read_api` only, masked | **No** — MR pipelines never receive Protected variables | `review-verdict`, `docs-sync` (fallback), `railway-apply-production` |
| `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN` | Docker Hub personal access token (read and write), masked | Yes | The four publish jobs; `image-scan`, for pulls only |
| `RELEASE_API_TOKEN` | GitLab project access token, `api` scope, **Reporter or above** (it creates and deletes labels and relabels issues; a Guest token gets `403`, `gl#796`), masked | Yes | `release-image-labels` — the one job that writes to the tracker (`gl#789`) |
| `RAILWAY_TOKEN_STAGING`, `RAILWAY_TOKEN_PROD` | Railway **project** tokens, one per environment, masked | Yes | The Railway jobs, through `.railway-staging` and `.railway-production` |
| `FRONT_DOOR_URL`, `STAGING_FRONT_DOOR_URL` | Public URLs, not secrets | No | `post-deploy-verify`; the staging apply, the staging integration tests and the two DAST jobs. **`FRONT_DOOR_URL` is not set on 2045** (read 2026-09-27); `STAGING_FRONT_DOOR_URL` is |
| `OpenEmrQa__*`, `OpenEmrAgenda__*`, `LlmQa__*`, `<Project>DataQa__ConnectionString` | Integration-test configuration (`DEPLOYMENT.md` §5). `OpenEmrQa__System__PrivateKeyPath` is a File variable | Yes | `staging-integration-tests` |
| Railway `preserve()` secrets | Set **on the Railway environment**, not in CI | — | Every apply (`DEPLOYMENT.md` §9) |

### Project settings, none of them in source

- `main`, `develop` and `staging` are **protected branches**, so Protected variables reach their push
  pipelines. If `develop` is unprotected, the publish jobs and the staging apply lose their
  credentials.
- A **pipeline schedule** (cron `0 6 * * *`, UTC, target `develop`). Without it, `railway-drift` and
  `post-deploy-verify` never run, and nothing reports the missing schedule. **This is the state of
  2045 today:** it has no pipeline schedule and no scheduled pipeline has ever run (read
  2026-09-27), and with `FRONT_DOOR_URL` unset too, neither job has ever run on GitLab. The other
  three settings in this list are in place.
- **Prevent outdated deployment jobs** is on, so an older pipeline cannot apply over a newer one on
  staging.
- **"Pipelines must succeed" is on** (since 2026-09-27, `gl#568`; `only_allow_merge_if_pipeline_succeeds: true`).
  A red pipeline locks the merge button, and an armed auto-merge waits for a green head pipeline.

---

## 4. Mapping to GitHub Actions

### Construct by construct

| GitLab | GitHub Actions equivalent | Notes |
|---|---|---|
| `workflow: rules` (MR, three branches, schedule) | `on: pull_request`, `on: push: branches: [main, develop, staging]`, `on: schedule`, `on: workflow_dispatch` | Branch filters leave out tags by default. This keeps the tag exclusion |
| `stages:` | No stages. Order comes only from `needs:` | To keep the gate first, the gate job takes no `needs:`, and every lint job **and `build`** needs it, except the self-tests that must run outside the gate. The test jobs then wait through `needs: build`. The gate is `review-verdict`, plus `mergeability` only on a host that tests the source branch rather than a merge commit. GitHub does not need `mergeability` (below). **The gate runs only on pull requests, so on a push it is skipped, and on Actions a skip propagates** (next row). So every job downstream of the gate needs `if: ${{ !failure() && !cancelled() }}` (step 5) |
| `needs: [x]` / `needs: []` | `needs: [x]` / no `needs:` | **Ordering is the same; skipping is not.** On GitLab a job that `rules:` left out of the pipeline simply isn't there, and stage order carries on without it. On Actions a job whose `if:` is false still exists, as **skipped**. GitHub's docs: "If a job fails or is skipped, all jobs that need it are skipped unless the jobs use a conditional expression that causes the job to continue." A job `if:` with no status function gets an implicit `success()`, so it is skipped too. The fix used here is `if: ${{ !failure() && !cancelled() }}`, combined with the job's own condition. `failure()` is true only when an ancestor job **failed**, so a skipped gate lets the job run and a red gate still stops it. Put it on **every** job downstream of the gate, not just the directly gated ones: the implicit `success()` looks at the whole ancestor chain, so a job two levels below a skipped job can be skipped too (actions/runner#491) |
| `rules: if:` on a job | Job-level `if:` on `github.event_name` and `github.ref` | Not the same effect. `rules:` decides whether a job is **created**. On GitLab, a `needs:` naming a job that was not created is a pipeline error, which is why `railway-apply-staging` and everything it needs share one rule. On Actions every job exists, and a false `if:` makes it skipped, which propagates as in the row above |
| `rules: changes:` (`railway-apply-production`) | `on.push.paths`, or a paths-filter step | Workflow-level paths apply to every job in the workflow |
| `when: manual` + `allow_failure: true` | `workflow_dispatch` (with an input naming the job), or an environment with required reviewers | A GitLab manual job is a play button **inside** an existing pipeline. It carries `allow_failure` so the unplayed button does not block that pipeline. `workflow_dispatch` starts a **new** run instead, so there is nothing to block and no `continue-on-error` is needed. Required reviewers on an environment pause a job inside its run, which is the closer match |
| `allow_failure` | `continue-on-error` | Close, not identical. Both keep the pipeline or run from failing. GitLab can set it per rule (`rules: allow_failure:`, as `review-verdict`'s scratch-ref rule does) or per exit code. Actions sets it per job or per step, with no exit-code form, but it accepts an expression, so a ref condition can go there. Check how a dependant's `if:` reads a tolerated failure before relying on it |
| `extends:`, `!reference`, hidden `.jobs` | Composite actions or reusable workflows | — |
| `image:` | `container:`, or `runs-on` plus setup actions (`actions/setup-dotnet`, `actions/setup-node`) | Hosted runners are Ubuntu with GNU tools. Run the shell gates in `alpine:3.21` as well, or they are only tested on one userland |
| `services: docker:dind` + the TLS variables | Not needed. Hosted runners have a Docker daemon; use `docker/setup-buildx-action` and `docker/login-action` | The `privileged`/`/certs/client` runner requirement does not exist there |
| `parallel: matrix` | `strategy.matrix`, with `fail-fast: false` | — |
| `cache: key: files:` | `actions/cache`, keyed on `hashFiles('Directory.Packages.props')` | GitLab caches only under the project directory, which is why `NUGET_PACKAGES` is moved there. The policies differ. GitLab's `pull-push` writes the cache back on every run, and `pull` never writes. `actions/cache` saves only on a key miss and never overwrites an existing key. For a `pull`-only job, use `actions/cache/restore` alone |
| `artifacts:` (`paths`, `expire_in`, `when: always`) | `actions/upload-artifact` with `if: always()` and `retention-days` | GitLab jobs **download** every earlier stage's artifacts by default, which is why the scans set `dependencies: []`. Actions downloads nothing unless a job runs `actions/download-artifact`, so `dependencies: []` has nothing to port. Reading an artifact from **another** run, as `railway-plan-artifact.sh` does, needs the Actions API and `actions: read` |
| `artifacts: access: developer` | **No equivalent.** Anyone who can read the repository can download its artifacts | A Railway plan names every variable in the graph. On a public repository, do not upload it |
| `reports: junit`, `coverage_report`, `coverage:` regex | No native rendering | `verify-test-results.sh` writes counts to the job summary; upload the XML as an artifact |
| `resource_group:` | `concurrency: group:` with `cancel-in-progress: false` | Actions keeps one running and **one** pending run per group, cancelling older pending ones. That is close to "prevent outdated deployment jobs", but a skipped intermediate build is never tested on staging |
| `interruptible: true` | `concurrency` with `cancel-in-progress: true` | Close, not identical. GitLab cancels a pipeline's interruptible jobs when a newer pipeline starts on the same ref. Actions cancels whatever shares the concurrency group, so the group name sets the scope |
| `environment: staging` | `environment: staging` | Same name, more responsibilities. On GitLab it records deployments, and a project setting adds forward-only ordering. On Actions it also holds environment-scoped secrets, a deployment-branch policy and optional required reviewers, which together are the best replacement for Protected variables. There is no forward-only setting (see `resource_group:`) |
| Protected variables | **Environment secrets** restricted to the right branch | Repository secrets reach `pull_request` runs from same-repository branches. Never put a Railway or Docker Hub token there |
| Masked variables | Secrets are masked automatically | The same limit applies: masking hides a value from the log, not from code in the job |
| File-type variable | Store the PEM as a secret, write it to `$RUNNER_TEMP` in a step, and export the path | — |
| `CI_JOB_TOKEN`, `GITLAB_API_TOKEN` | `GITHUB_TOKEN` with a `permissions:` block | — |
| MR pipeline on the source branch | `pull_request` checks out the **merge commit**, `refs/pull/N/merge` | Stronger than GitLab CE: tests the change against the current target |
| `CI_MERGE_REQUEST_DIFF_BASE_SHA` | `git merge-base` of `github.event.pull_request.base.sha` and `head.sha` | Not `base.sha` alone: that is the target branch's tip when the event fired, not the merge base. Diffing against it would pull the target's newer commits into what `docs-sync` and the secret scan see as the change |
| `CI_MERGE_REQUEST_DESCRIPTION` (truncated near 2700 characters) | `github.event.pull_request.body`, not truncated | `mr-description-optout.sh` and its self-test are not needed |
| Pipeline schedule (a project setting) | `on: schedule: cron` in the workflow file | Scheduled workflows run from the default branch only. GitLab's schedule names its target branch (`develop`). On Actions, check out that branch explicitly unless it is the default |
| `tags:` runner routing | `runs-on:` labels (self-hosted) | — |
| `timeout:` | `timeout-minutes:` | — |
| Merge blocked only by convention (`merge-gate.sh`) | Branch protection: required status checks, plus "require branches to be up to date" | This is enforcement GitLab CE lacks here |

### Parts that are GitLab-specific and need re-implementing

- **The verdict gate reads MR notes.** `.gitlab/ci/verdict-state.sh` parses a note's first line, checks
  provenance, and carries a verdict across a clean rebase by replaying it with `git merge-tree`.
  GitHub already has its own pair, `.github/scripts/verdict-state.sh` and `verdict-gate.sh`. It reads
  PR **reviews**, binds freshness to a patch-id, and waits for a ruling instead of failing fast. The
  parser is shared; the freshness test is not (CI-SETUP §8).
- **`mergeability`.** GitHub blocks a conflicted pull request natively, so this job is not needed.
- **`merge-gate.sh`.** Replace it with required status checks in branch protection.
- **`release-process.sh` and `release-image-labels`.** Issue labels, the `work::` board and GitLab
  Releases are this tracker's objects; a GitHub port would re-target Issues, labels and Releases through
  its own API (`gl#789`).
- **`railway-plan-artifact.sh`.** It walks the GitLab jobs API back to a pinned plan. On GitHub,
  `railwayapp/config` pins the plan on the pull request and applies it on merge, as
  `railway-config.yml` already does.
- **Anything that reads `.gitlab-ci.yml` as text:**
  - `railway-apply-wiring-selftest.sh` extracts the apply jobs' `script:` with `yq`;
  - `verdict-gate-selftest.sh` cases 10–12 pin the `review-verdict` rules and the absence of
    `allow_failure`.

  Both would have to read the new host's workflow file instead.
- **GitLab's own report formats and names.** The `gl-*.json` security reports, the junit and coverage
  widgets, and `expose_as`. They are cosmetic, but a port has to decide what replaces them.

### What `.github/workflows/` already covers, and what it lacks

`ci.yml` covers:

- every lint job except `mergeability`, `mergeability-selftest`, `railway-apply-wiring-selftest`,
  `release-process-selftest` and `docs-sync-optout-selftest` (`counted-selftest-lint` runs inside its `backup-schedules-selftest`, and `qa-cohort-selftest`
  inside its `post-deploy-selftest`);
- `build` and all five test jobs;
- `review-verdict`;
- `publish-image` and `publish-observability-images`.

Only the publish jobs read a repository secret.

`railway-config.yml` covers production only: `plan` on a pull request, `apply` on merge, scheduled
`drift`, and `smoke` (the `post-deploy-verify` equivalent). It has `RAILWAY_TOKEN_PROD` and
`vars.FRONT_DOOR_URL`.

What GitHub lacks, compared with `develop`'s `.gitlab-ci.yml`:

- **Gate-first ordering.** GitHub's `review-verdict` runs after build and tests, and waits.
- **The `mergeability` job.** GitHub blocks conflicted pull requests natively instead.
- **`publish-proxy-image`.** `ci.yml`'s header names it, but no such job exists there.
- **The `develop` → staging chain.** GitHub publishes the sidecar only on `main`, still moves
  `-latest`/`-main` tags, and has no `STAGING_BUILD_SHA`, no `railway-apply-staging` and no
  `staging-integration-tests`. The integration tier runs nowhere on GitHub.
- **A destructive-plan guard before the apply.** `railway-config.yml`'s `apply` takes a snapshot and
  wraps the apply in deploy-identity and stays-up, but does not run `railway-destructive-guard.sh`
  first, as both GitLab applies do.
- **Per-environment tokens.** `RAILWAY_TOKEN_STAGING` is not wired.

CI-SETUP §10 records why the deploy split was not ported.

---

## 5. Standing the pipeline up on a new host

Do these in order. Each step says how to tell it worked.

1. **Choose where each environment's secrets live** before creating any. On GitHub, create
   environments `staging` and `production`, each restricted to its deploying branch (`develop` for
   staging, `main` for production). On GitLab, protect `main`, `develop` and `staging`.
   *Verify:* a run on a feature branch cannot read either environment's secrets.
2. **Provide runners.** Hosted runners work for everything except a private-network target. For
   self-hosted runners, use Docker executors and, if you keep dind, `privileged = true` and
   `/certs/client`. *Verify:* one throwaway job runs `docker info` in a service container.
3. **Create the credentials** from §3, Protected or environment-scoped as the table says. Create each
   one protected the first time: a token that was exposed must be rotated, not re-flagged. Keep the
   review-verdict token read-only and **not** protected on GitLab. *Verify:* list them by name
   (never print values), and check each Protected flag or environment scope.
4. **Port lint, build and test first, with no gate yet** and no secrets at all. Start from `ci.yml`:
   it is already the Actions form of these jobs. Add the four GitLab-only lint jobs where they still
   apply (§4), but give no job a `needs:` on the gate yet. *Verify:* a pull request runs every lint,
   build and test job green.
5. **Add the gate, then branch protection.** Add `review-verdict` with no `needs:` of its own
   (`ci.yml`'s copy currently needs the build and tests; remove that). On a host that tests the
   source branch, as GitLab does, also add `mergeability` and `mergeability-selftest`. GitHub tests
   the merge commit and blocks a conflicted pull request itself, so it needs neither. Then make every
   lint job **and `build`** need the gate job(s), except `verdict-selftest` (and
   `mergeability-selftest` where it exists), which keep no `needs:`. The test jobs wait through
   `needs: build`, and the publish jobs through the tests.

   **On Actions, also give every job downstream of the gate `if: ${{ !failure() && !cancelled() }}`**:
   the lint jobs, `build`, the test jobs and the publish jobs. Combine it with any condition the job
   already has, for example `if: ${{ !failure() && !cancelled() && github.event_name == 'pull_request' }}`
   on `docs-sync`. The gate runs only on pull requests, so on a push it is skipped. Without the
   condition, every job below a skipped gate is skipped too (§4, the `needs:` row). GitLab needs
   nothing here: a job its rules leave out does not block later stages.

   Make the gate job(s), the build, the five test jobs and the lint jobs required checks.
   *Verify, in this order:*
   - **Push to an unreviewed pull request.** The gate ends red with "no fresh approval", the ungated
     self-test(s) run green, and **every other job is skipped**. That is the gate working, not a
     failure. On GitHub the gate first waits for a ruling until its deadline, and the gated jobs
     stay queued until it goes red.
   - **Post an approving verdict** (CI-SETUP §8 gives the exact first line). On GitHub a waiting gate
     picks it up; otherwise, re-run the gate. The gate turns green and every job behind it runs green.
   - **Push a new commit.** The gate goes red again ("stale").
   - **Push to `develop`** (or merge the pull request). The gate does not run, and **every lint, build
     and test job still runs green**. If any shows as skipped, a job below the gate is missing the
     `!failure() && !cancelled()` condition.
6. **Publishing.** Add the four publish jobs on `develop` pushes. Log in with the Docker Hub
   credentials, and keep the per-image checks before every push. *Verify:* a `develop` push creates
   `<project>-`, `proxy-`, `security-platform-` and four observability `-sha-<12>` tags in the registry, and they match
   the commit.
7. **Set the Railway `preserve()` secrets** on each Railway environment (`DEPLOYMENT.md` §9). Then run
   a plan by hand. *Verify:* `railway config plan` against each environment succeeds and
   `scripts/railway-destructive-guard.sh` exits 0 on it.
8. **The staging deploy.** Port `railway-apply-staging` with its whole script, in order: plan, guard,
   identity(snapshot(apply)), stays-up, front-door check. Add a concurrency group for the environment.
   *Verify:* a `develop` push ends with deploy-identity reporting a moved deployment, stays-up green,
   and `post-deploy-verify.sh` green at `STAGING_FRONT_DOOR_URL`.
9. **Staging integration tests.** Add the integration-test variables, including the private key as a
   file. *Verify:* the job's junit shows tests run, not skipped. `verify-test-results.sh` fails a run
   with zero tests.
10. **Production and the nightly checks.**
    - Production apply: from a pinned plan only, guard first, no `--confirm-destructive`.
    - Drift and post-deploy-verify: on a daily `schedule`, plus a manual trigger.

    *Verify:* trigger drift by hand. Expect exit 0 (no drift) or a red run that names the drift. A red
    run with an unreadable plan means the check is broken, not that there is drift.

**The first green run.** A pull request is green on every lint, build and test job. `review-verdict`
is green once a verdict exists. After the merge, the `develop` pipeline publishes and deploys staging,
and the integration suite passes.

Record the host's runner and project settings in `CI-SETUP.md` in the same change. Configuration that
exists only in a host's settings page does not exist for the next person.
