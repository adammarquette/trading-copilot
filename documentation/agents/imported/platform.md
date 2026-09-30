# Platform Agent (CI + the container stack)

Governs the pipeline and everything that runs the system; the root [`AGENTS.md`](../../AGENTS.md) still applies.
This contract **never auto-loads** — see [`README.md`](README.md). It owns the artifacts below **wherever they
live**, not just a directory.

| Artifact | Where |
|---|---|
| CI pipeline — lint, build, test, evals, image publish. **Defined once per host; keep them in step** | [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml), `.github/scripts/`, `.github/licenses/` — and, on GitLab, [`.gitlab-ci.yml`](../../.gitlab-ci.yml), `.gitlab/ci/` (`CI-SETUP.md` §0) |
| Railway deploy — plan / apply / drift | [`.github/workflows/railway-config.yml`](../../.github/workflows/railway-config.yml) · the `deploy` stage of [`.gitlab-ci.yml`](../../.gitlab-ci.yml) + `.gitlab/ci/railway-plan-artifact.sh` · [`.railway/railway.ts`](../../.railway/railway.ts) (`DEPLOYMENT.md` §9, `CI-SETUP.md` §10) |
| Deploy-identity assertion — *did the apply **ship**, or replay?* | [`scripts/railway-deploy-identity.sh`](../../scripts/railway-deploy-identity.sh) + its self-test. Wraps both GitLab applies and brackets GitHub's; requires the environment to produce a deployment id it did not have before. `railway redeploy` replays the previous deployment's captured config and reports `SUCCESS` — nothing here calls it, but the class of failure is not specific to it (`CI-SETUP.md` §10, `gl#430`) |
| Snapshot-before-destruction guard — **fails closed**, except where a declaration says there is nothing to protect | [`scripts/railway-snapshot-guard.sh`](../../scripts/railway-snapshot-guard.sh) + its self-test, reading the **`refreshable`** field of [`scripts/railway-data-refreshable.json`](../../scripts/railway-data-refreshable.json) (`gl#658`) — **and nothing else in that document**; its `schedules` field answers a different question for a different script, and production answered the two the other way round from `gl!535` until `gl#678`. In an environment declared data-refreshable — **staging, and since `gl#678` (2026-09-23) production too**, by the maintainer's ruling that a destructive production apply may lose data that can only be re-seeded — it takes no backup and says so with the recorded reason, and every other refusal is unchanged. **Wired to every apply path since `gl#465`**: inside `railway-deploy-identity.sh run` on both GitLab applies (that nesting is fixed — the other order presents it with two `--plan` arguments and it refuses every deploy), a fail-closed step before GitHub's apply action, and the **documented entry point for a hand apply**, which is why no file here tells anyone to run a bare `railway config apply` any more. A project token **can** create the backup and **cannot** read `workflowStatus`, so CI always takes the fallback wait (`DEPLOYMENT.md` §10) |
| Backup schedules — **retired in both environments, by decision** | [`scripts/railway-backup-schedules.sh`](../../scripts/railway-backup-schedules.sh) + its self-test. The maintainer ruled on 2026-09-22 that neither environment gets scheduled backups, because the data is not critical and can be refreshed (`gl#658`); staging's three, set that morning by `gl#465`, were removed the same day by the new `remove` verb, and production never had any. **The trap was that the schedules were doing a second job** — Railway caps a *manual* backup at 50% of the volume and the cap bites the *first* one, so a schedule was also what kept the snapshot guard runnable. Both halves are now settled per environment in `railway-data-refreshable.json`, **in two independent fields** — `schedules` for this script, `refreshable` for the guard — because production answered them differently until `gl#678`: no schedules, and still snapshot me. **They were one boolean until `gl!535`'s review**, and the cost was precise: `check` demanded a DAILY schedule on every production volume and pointed the operator at the `apply` that re-creates what the ruling retired, while three documents in the same commit said nothing was owed there. `apply` refuses where `schedules` is `"none"` and `remove` unless it is; `check` expects whichever state is declared and reports the other (`DEPLOYMENT.md` §10) |
| Sidecar image (the **same** artifact local and deployed) | [`Dockerfile`](../../Dockerfile), published to `docker.io/amarquette/gauntletai` as `<project>-sha-<12>` (plus moving `<project>-develop` / `<project>-latest` markers nothing deployed reads). **Only production pins an explicit `-sha-<12>`; staging runs the `-sha-<12>` each `develop` push builds** (`gl#657`), and `docker-compose.yml` pins production's, so "the same artifact" is checkable: the pin names the build, and the digest confirms it (`DEPLOYMENT.md` §6). **What CI checks is the weaker half** — `tools/verify-local-mirrors-deploy.sh` compares the two *tag strings*, so it proves the same build is **named**, not that the same bytes are **running**. `docker compose up --build` writes a workstation build under that same immutable tag locally and nothing re-pulls it, so confirm bytes with a digest comparison, never with a green `openemr-pin` job |
| The stack — OpenEMR, MySQL, the proxy, the sidecar | [`docker-compose.yml`](../../docker-compose.yml), [`.env.example`](../../.env.example) |
| The front door | [`reverse-proxy/`](../../reverse-proxy/) — `nginx.conf.template`, linted in CI |
| Production pin window — *may production run this image?* | [`.railway/production-pin-window.mjs`](../../.railway/production-pin-window.mjs) + its self-test, run by `production-pin-window` on merge requests into `develop` that touch `.railway/**`. Accepts, **per service**, a promotion (a `develop` build staging ran), a rollback (one of production's own last `PRODUCTION_PIN_WINDOW` pins — 3, defined only there) or a shared image in lockstep (openemr, mysql, pgvector equal to staging's literal), read offline from `develop`'s first-parent history by evaluating `railway.ts` as of each commit, so a promotion whose smoke test fails can roll back to what production ran before (`gl#681`, `DEPLOYMENT.md` §9 *Rolling back after a failed smoke test*). It replaced `gl!549`'s "production equals staging" rule rather than joining it. Judges only the pins an MR moves |
| Deterministic replayer image — staging only | [`security-platform/`](../../security-platform/) — `security-platform-sha-<12>` from `publish-security-platform-image` on `develop`. The Railway service is idle `serve` (`GET /health`, no domain, no production key). Concrete cases belong to `gl#808` and live front-door runs to `gl#827`; what is here is image, CI, deploy and allowlist plumbing only (`gl#807`, `DEPLOYMENT.md`, `CI-SETUP.md`) |
| Post-deploy verification — *does the deployed system answer?* | [`scripts/post-deploy-verify.sh`](../../scripts/post-deploy-verify.sh) + its self-test, run by the `smoke` (GitHub) / `post-deploy-verify` (GitLab, production) jobs and, since `gl#685`, by `railway-apply-staging` against staging after every apply. **`staging-integration-tests` then runs the integration suite after every `develop` apply** (`gl#594`, `CI-SETUP.md` §10). Probes `/<project>/ready` since `gl#685` — `/health` is liveness only and stayed 200 through a crash loop (`CI-SETUP.md` §10) |
| Stays-up check — *did what shipped stay up?* | [`scripts/railway-stays-up.sh`](../../scripts/railway-stays-up.sh) + its self-test and the recorded crash loop in `scripts/fixtures/railway-stays-up/`. Runs after deploy-identity on both GitLab applies, GitHub's apply, and the hand-apply runbook; reads the deployment's status, `deploymentStopped`, instance status and its own log, because Railway kept a crash-looping deployment at SUCCESS for 78 minutes (`gl#685`, `CI-SETUP.md` §10) |
| Observability — a **second** stack, in three wirings | [`observability/`](../../observability/) (separate project) · [`docker-compose.observability.yml`](../../docker-compose.observability.yml) (overlay, for a containerized sidecar) · **declared for STAGING since 2026-09-21, and for PRODUCTION too since `gl#678`'s 2026-09-24 promotion (carried to `develop`'s source by `gl#704`) — `railway status --environment <env>` lists both per environment; re-check rather than trust this line** — the staging apply is the unattended one every **`develop`** push triggers (`gl#657`), so that half goes stale at a merge, not on anyone's decision: `prometheus` + `grafana` in [`.railway/railway.ts`](../../.railway/railway.ts), from `observability/{prometheus,grafana}/Dockerfile`. `OBSERVABILITY_IMAGES.staging` **tracks `develop`** (`TRACKS_DEVELOP`), so each push deploys the `prometheus-`/`grafana-sha-<12>` its own `publish-observability-images` built — which retired the hand-built `*-sha-4768e5d79880` pins and the "grafana image is six panels short" gap with them, from the first `develop` apply after `gl#657`. **Production's entry is filled** with two explicit CI tags of the one `develop` build staging ran when it was promoted — `gl#540`, `gl#678`, `DEPLOYMENT.md` §9 *Promoting the observability tier to production* names the same procedure for the next promotion, and its `iac:selftest` case (gl#704) refuses production ever regressing to empty. Loki (`gl#596`) and Tempo (`gl#618`) are **not** in that map: each has its own staging-only one, `LOKI_IMAGE_BY_ENV` / `TEMPO_IMAGE_BY_ENV`, each with **no production key**, which `iac:selftest` refuses. Staging's Tempo tracks `develop`, so that first apply **creates** Tempo on staging; staging's Loki entry is empty by the maintainer's ruling of 2026-09-24, until `gl#679`, and `iac:selftest` pins that too. `OBSERVABILITY_FOR` requires BOTH pins non-empty, so a half-created tier is impossible (`DEPLOYMENT_TOPOLOGY.md` § *Observability: three wirings*) |
| Release process — the board's Done → closed half, from git ancestry | [`scripts/release-process.sh`](../../scripts/release-process.sh) + its self-test. `label` runs as `release-image-labels` on every `develop` push and stamps each open `work::done` issue in that build with `image::<sha12>`, carried over until released; `release` is the operator's step in the production procedure, publishing the GitLab Release `<project>-sha-<12>` and closing what shipped. Both take `--dry-run`. The job writes to the tracker with its own Protected `RELEASE_API_TOKEN` (`api` scope, Reporter role or above — a Guest token gets `403`, `gl#796`), never `GITLAB_API_TOKEN` (`gl#789`, `DEPLOYMENT.md` §9, `CI-SETUP.md` §10) |
| Counted-selftest lint — the fenced list below, checked against the tree instead of hand-typed | [`tools/counted-selftest-lint.sh`](../../tools/counted-selftest-lint.sh) (+ self-test) checks the fenced list below against every `*-selftest.sh` with an `EXPECTED_*`/`MIN_ASSERTIONS` constant, missing entries and stale ones both. Runs as `counted-selftest-lint` on GitLab and rides `backup-schedules-selftest` on GitHub (`gl#751`). `CI-SETUP.md` §1's job total is deliberately **not** lint-covered — a hand-edited constant collides the same way a hand-typed sentence did (`gl!654` review F1) — and is read from `POST /projects/2045/ci/lint` instead |
| Pipeline reference · portable pipeline spec · runbook · physical view | [`CI-SETUP.md`](../CI-SETUP.md) · [`PIPELINE_REPRODUCTION.md`](../PIPELINE_REPRODUCTION.md) · [`DEPLOYMENT.md`](../DEPLOYMENT.md) · [`DEPLOYMENT_TOPOLOGY.md`](../DEPLOYMENT_TOPOLOGY.md) |

## Role

**Never write to a shared checkout that is not your own branch's worktree.** That applies whenever you are
reviewing — root `AGENTS.md`'s routing table sends any agent reviewing any change to
[`code-reviewer.md`](code-reviewer.md), whatever role dispatched it — and just as much when you are
authoring: probing CI, compose or the proxy from someone else's checkout is the same hazard. The commands
that cause it, the safe alternatives and why are in `code-reviewer.md`'s *You do not write to the shared
working tree*; two of the five incidents it documents were authors, not reviewers (`gl#560`).

Keep the pipeline and the runtime boring, reproducible, and honest about what it is doing. **Compose is the
deployment** for local work, and a hosted environment runs the same topology (it pins the published sidecar
image; compose builds it from source). **Merging deploys it — on GitHub**: `.railway/**` is applied on
merge, so a **pin edit** reaches the environment with no operator step (`DEPLOYMENT.md` §9). Until `gl#591`
the proxy was worse — it rebuilt from a branch, so a proxy *source* change did too. It is pinned now, so
every service promotes the same way. **On GitLab the branches are a promotion chain, and only production is
pinned** (`gl#657`): every **`develop`** push builds each component once as `<component>-sha-<12>` of that
commit and reaches the **staging** environment automatically — `railway-apply-staging` plans with
`STAGING_BUILD_SHA` set to the commit, runs the destructive guard, and applies only on a proven
**non-destructive** verdict (exit 0; the guard runs plain, **not** `--require-clean`, because pending changes are
the point of an apply). A failure there is fixed forward on `develop`; nothing reverts. The `staging` **branch**
builds and deploys nothing — it only gates `main`. `main` promotes by pin, and **production** stays an operator
step, because `RAILWAY_TOKEN_PROD` is Protected, no plan is pinned pre-merge, and `railway-apply-production`
fails closed (`CI-SETUP.md` §10, `gl#504`, `gl#424`) — and, handed a pin, runs the same destructive guard and
applies without `--confirm-destructive`, so a destructive production plan has no CI route (`gl#641`). Staging is the gate: production is promoted from a build
staging ran, never from a merge and never from a rebuild. Every service references an image tag — the proxy was
the lone exception until `gl#591` — and production's references are **explicit `-sha-<12>` pins**, so promoting
is **a deliberate re-pin**: copy the sha staging ran into `SIDECAR_IMAGE_BY_ENV.production` (and its siblings)
and apply. Still confirm "what is live" against Railway's deployment record rather
than this tree — the two disagreed for seven weeks (`gl#504`), and only the record can show they now agree. The runbook is the product. **Configuration that exists only on someone's workstation does not
exist**: record it in [`DEPLOYMENT.md`](../DEPLOYMENT.md) and [`.env.example`](../../.env.example) in the same
change, or the next person reading the stack cannot see it.

You do not write production code or tests. If the pipeline reveals a product defect, file it for the Coding
Agent.

## Move the work item as you go

You are a change-making role, so the board rule that binds the Coding Agent binds you identically
([`MR_WORKFLOW.md`](../MR_WORKFLOW.md)) — a pipeline or compose change is as invisible on a stale board as a
`src/` one:

- **`work::inprogress`** when you pick the work up — before the branch. Normally the Coordinator has already
  set it, having delegated the item to you off `work::todo`; set it yourself when working standalone.
- **`work::review`** when you open **or update** the MR and spawn the reviewer.

**Both apply again on every `Request changes` round.** Set `work::inprogress` when you are *assigned* the
findings — the verdict alone does not move the item, the agent picking it up does — and back to
`work::review` when you push and the reviewer is spawned again.

**If you are not the lead, you stop at `work::review`** (root `AGENTS.md`;
[`multi-agent.md`](multi-agent.md)). The lead is the Claude Code Coordinator. You are not it if your tool is not
Claude Code — your signed claim on the issue names your tool — and then the review steps in this contract
are not yours: open the MR, move the item to `work::review`, post a signed hand-back note, and stop. Do not
spawn a reviewer, wait on a verdict or take findings; a fix round is yours only after the lead moves the
item back to `work::todo` and you claim it. A Claude agent dispatched by the lead, or working standalone
with no other tool's claim on its issue, follows this contract as written.

**With no Coordinator estimate on the issue, post and revise the time to complete** at those same moves, as
[`src/AGENTS.md`](../../src/AGENTS.md) says, from [`task-sizing.md`](task-sizing.md) §*Time to complete*.

**On an `approved` verdict, leave the item in `work::review` and hand it to the Coordinator** — the only role
that sets `work::awaitingapproval`, after checking the reviewer's acceptance-criteria ticks — with a **hand-off note on the issue** naming the MR and the approving verdict's note id (`approved: !N note M`);
with no Coordinator session running the item waits in `work::review` until its board sweep finds it
(`gl#684`). After the merge it is
`work::done`, which here means *merged and not necessarily live*: on GitHub `.railway/**` applies on merge;
**on GitLab it depends which branch** (`CI-SETUP.md` §10). A merge to **`develop`** is live on **staging**
once its pipeline's `railway-apply-staging` is green — that job deploys the images the same pipeline built
(`gl#657`) — and a merge to `staging` changes no environment. **Production stays an operator step**, so a
merged platform change routinely is not in effect there. Closing the issue is what records that it is.

If another tracked item has to land first, use **`work::blocked`** and **name it in a note**. An operator
action on its own is not a blocker the board can show — file it as an item and block on that, or leave yours
where it is and say so. If what you have is a *question* rather than a dependency, **leave the item where it
is** and write the question into the issue: `work::planning`'s only exit is `work::backlog`, so live work
parked there drops off the active board and, if it is later promoted to `work::todo` — by the maintainer
or in a sprint cut — gets dispatched to a fresh agent.

Each move is `add_labels` **and** `remove_labels` — this GitLab is Free and does not enforce `work::` as a
scoped label, so adding one without removing the last puts the item in two columns.

**Stage a note or MR description you compose as a heredoc-to-file at a path unique to your role, the
issue/MR and round** (`README.md` §*Staging outbound content*), never a bare name like `note.md` — the
scratchpad is shared by every agent in the session, and a concurrent agent on a different item, or another
role on the same one, reaches for the same name. Read back the same unique path if you verify the file
before posting it.

## Non-negotiables

The root contract's rules apply unchanged. Four land specifically on the platform:

- **No secrets in source** extends to workflow files, compose files, `.env.example`, image layers and logs
  ([`ENGINEERING_STANDARDS.md`](../ENGINEERING_STANDARDS.md) §11). `.env.example` carries shapes and defaults,
  never a real value.
- **The one-origin invariant** ([`DEPLOYMENT.md`](../DEPLOYMENT.md) §2) is a platform constraint, not a config
  preference. Each environment's `OpenEmr__BaseUrl` must equal that environment's OpenEMR `site_addr_oath`, and
  both must be the **front door** — never a container's own hostname. Break it and every SMART launch dies before
  a login form, with an error that names neither file.
- **Trust boundaries are topology.** What is published versus network-internal is load-bearing
  ([`DEPLOYMENT_TOPOLOGY.md`](../DEPLOYMENT_TOPOLOGY.md)): an endpoint whose authorization argument is "only the
  proxy can reach it" stops being safe the moment a port is published or a network is flattened. Re-read the
  topology doc before changing either, and say in the MR which boundary moved.
- **Enforcement does not live in infrastructure.** Authorization and no-PHI are enforced in code; the proxy and
  the network are defence in depth, never the thing standing between a caller and patient data.

## What bites in CI

[`CI-SETUP.md`](../CI-SETUP.md) owns the job list; these are the traps, and they have all been paid for once:

- **The false-green guard is load-bearing.** `verify-test-results.sh` exists because a test job can exit 0 having
  run nothing. Never route around it, and never add a test job that does not go through it (§5). **On GitLab
  it must run in `script:`, never `after_script:`** — a failing `after_script` leaves the job green, so a guard
  placed there prints its complaint and changes nothing. It does not have to be the *last* line of `script:`:
  `unit-tests` runs coverage-summary steps after it (`gl#614`), and since `script:` stops at the first failing
  line, those later steps running at all is itself evidence the guard passed.
- **A gate asserted in a comment is not a gate.** Everything here fails *permissive* when it breaks: an
  unparseable timestamp, a preflight looser than the parser, a self-test that skips a missing file. For any
  guard you touch, write or extend the test that makes it go **red for the reason it exists**, prove it by
  mutation in both directions under [`ENGINEERING_STANDARDS.md` §8.4](../ENGINEERING_STANDARDS.md) (the
  standing rule and its trap list; the traps below are the CI-specific ones), and run it —
  `verdict-state-selftest.sh` and `railway-snapshot-guard-selftest.sh` are
  where those live. **`railway-snapshot-guard.sh` is the one guard that fails *closed*** and must stay that
  way: when it cannot take a verified volume snapshot, the destructive operation does not run
  (`DEPLOYMENT.md` §10). Its self-test asserts twice on every red case — non-zero exit *and* the guarded
  command not executed — because the first without the second is the failure it exists to prevent. **And a
  suite must check its subject is a *program* before checking what it says:** a refusal exit code that
  collides with the shell's own (2 for both here) means an unparseable guard still prints `ok` for every
  refusal case, so `bash -n` it before case 1. **Prove each assertion by mutation, not by reading it.**
  Break the thing it names and watch it go red, then revert. `gl#673` found assertions in the two
  verdict suites that could not fail. One re-read a variable the line above had already asserted. One
  fed input that an earlier arm refused first. One printed its evidence instead of asserting it. Two
  accepted an exit of 0 as a refusal. And the YAML cases read a rule for what it *contained*, not
  for what it *does*. **Then break it a second way.** Round 1 of `gl#673` banned `||` and the bare
  `when: never`, and review walked past both with `|` and `when: "never"`. A check written against
  the one spelling you tried is the same defect one step later. **And when the next spelling keeps
  appearing, stop listing spellings.** Round 3 found an inline comment and first-match-wins
  ordering still getting through, so the rules list is now pinned exactly. Where the property
  really is "exactly this configuration", check equality, after the same normalisation the
  consumer applies (YAML's comment stripping, here). **Then check that what you compared is what
  the consumer uses.** Round 4 found the pin reading the first `rules:` list while YAML keeps the
  last copy of a repeated key, so a second copy walked straight past an exact comparison.
- **The verdict gate's API half has no self-test, so it has a hand check instead.** The carry decision and
  the `git merge-tree` replay are driven by `verdict-state-selftest.sh` against throwaway repositories.
  Reading the shas, count and messages out of the API is not covered, and neither is the fetch of an
  orphaned head: remove that fetch and every suite stays green. **Before you push a change to
  `.gitlab/ci/verdict-state.sh` or `post-verdict.sh`, run
  `GITLAB_API_TOKEN=… CI_PROJECT_ID=2045 bash .gitlab/ci/verdict-state.sh --replay <iid>` against one
  real merge request.** It must print a `base=… head=… commits=… msgs=…` line. `CI-SETUP.md` §8 calls
  this the standing check and records that it is how the previous design's inert guard was found.
  **Since `gl#716` run `--mr-shas <iid>` on the same merge request too**: the commit list the provenance
  check compares against is the other API read no suite reaches, and it must list the MR's head.
  **Run this natively** — since `gl#749` bounded every network call in both scripts, a native run
  on this workstation is fast (well under a minute even on the two merge requests that once
  looked hung) and, unlike a container built from a fresh clone, it runs the file you are about
  to push, not `develop`'s copy. If you do want it isolated from this workstation's Application
  Control tax, pipe your committed `HEAD` into `alpine:3.21` with `git archive HEAD` (commit
  first — uncommitted edits are not in it) rather than cloning — `--replay` touches
  no git, so a clone there only runs the wrong copy of the script and costs minutes on a cold
  image (`gl#749`, `CI-SETUP.md` §8 *Running the verdict reader locally on Windows*).
- **Every gate here runs during a pipeline, and none of them runs when the merge happens.** GitLab CE has
  no merge-time hook, and GitLab's Approve button and auto-merge never consult the reader. "Pipelines must
  succeed" is on for project 2045 since 2026-09-27, so a red pipeline — `review-verdict`, `evals` — locks the
  merge button and an armed auto-merge waits for a green one on the current head; that is the pipeline's
  read of the verdict, not a merge-time one. `gl#568` found 13 merges whose verdict was stale when the button
  was pressed. `.gitlab/ci/merge-gate.sh` is the merge-time check. It refuses unless a fresh approval
  names the head being merged, and refuses while auto-merge is armed, and `--merge` pins `sha=`. But it
  is the **sanctioned** path, not the only enforced one. A red `review-verdict` locks the button, but only
  as the head pipeline read it; nothing re-reads the verdict at merge time (`CI-SETUP.md` §8 *Merge time*).
- **Read a gate's POSITIVE statement; the absence of a failure line is evidence of nothing.** A suite that
  never reached its cases prints no failure lines either, so "no `FAIL` in the output" is not a pass — it is
  the shape of both a pass and a no-op. Assert on **exit 0 *and* the success line**, and know that a naive
  `grep '^ok '` matches none of the four suites here: `railway-destructive-guard-selftest.sh` and
  `post-deploy-verify-selftest.sh` emit their `ok` ANSI-green, `railway-snapshot-guard-selftest.sh` says
  `SELF-TEST PASSED` instead, and `verdict-state-selftest.sh` indents its line. Strip the escapes before
  matching and check the token that suite actually prints. **"Counted" means the suite checks its own
  tally against a hand-declared `EXPECTED_ASSERTIONS=` / `EXPECTED_CASES=` / `EXPECTED=` /
  `MIN_ASSERTIONS=` constant** — a suite that only refuses "no case ran at all" catches zero, not
  shrinkage, and does not count by this definition. **The list below used to be a running ordinal count
  in prose, and it was wrong in both directions at once**: `gl!538` first counted it, and by the time
  `gl#751` replaced it, the count had drifted to crediting four suites that print a self-referential `N
  of N` with no independent constant behind it (so a case dropped inside a subshell, as
  `railway-snapshot-guard-selftest.sh` once did, would have read as a smaller, still-green pass) while
  missing four suites `gl!625` found that do declare one. A prose count of *how many* cannot catch that
  kind of membership error, and two concurrent MRs bumping the same number always conflicted — so
  there is no number here now, only the list, **sorted by path — insert a new suite in path order, not
  at the end, so two concurrent additions land at different lines instead of both racing the closing
  marker** — and `tools/counted-selftest-lint.sh` (run in CI) fails when either list below and the tree
  disagree in either direction: a suite missing, a stale entry (deleted file, or one that lost its
  constant) left behind, and, for the second list, a suite that gained a constant but was not moved.

  <!-- counted-selftest-list:start -->
  - `.github/scripts/verdict-state-selftest.sh` — `MIN_ASSERTIONS` (`gl#668`)
  - `.gitlab/ci/merge-gate-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#568`)
  - `.gitlab/ci/mergeability-gate-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#674`)
  - `.gitlab/ci/mr-description-optout-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#713`)
  - `.gitlab/ci/verdict-state-e2e-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#562`; runs in `mergeability-selftest`)
  - `.railway/production-pin-window-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#681`; against `develop`'s real history, so it needs the full clone)
  - `observability/alerts/alert-rules-gate-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#654`/`gl#661`)
  - `reverse-proxy/nginx-behavior-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#768`, `gl#810`, `gl#816`; runs a real nginx against the rendered template, unlike `nginx-config-lint`'s `nginx -t`)
  - `scripts/contract-schemas-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#625`)
  - `scripts/post-deploy-verify-selftest.sh` — `EXPECTED_CASES` (`gl#540`, moves with cases per `gl#550`)
  - `scripts/qa-cohort-credentials-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#748`)
  - `scripts/railway-apply-wiring-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#641`)
  - `scripts/railway-backup-schedules-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#465`)
  - `scripts/railway-deploy-identity-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#659`, the last to be born uncounted)
  - `scripts/railway-snapshot-guard-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#465`; catching a subshell-swallowed `fail` is what motivated the constant)
  - `scripts/railway-stays-up-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#685`)
  - `scripts/release-process-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#789`)
  - `scripts/verify-line-citations-selftest.sh` — `EXPECTED_ASSERTIONS`
  - `security-platform/run-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#807`)
  - `tools/apk-add-retry-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#845`)
  - `tools/counted-selftest-lint-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#751`, and self-referential: this very list is what it checks)
  - `tools/docs-sync-paths-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#815`; reads `docs-sync`'s literals from both hosts)
  - `tools/nuget-audit-report-selftest.sh` — `EXPECTED_ASSERTIONS` (`gl#665`)
  - `tools/pipefail-reader-lint-selftest.sh` — `EXPECTED` (`gl#724`)
  - `tools/sast-scan-selftest.sh` — `EXPECTED` (`gl#613`)
  - `tools/secret-scan-selftest.sh` — `EXPECTED` (`gl#613`)
  - `tools/security-gate-selftest.sh` — `EXPECTED` (`gl#811`; also runs first in each deploy-time scan job)
  <!-- counted-selftest-list:end -->

  **Not yet counted** — each refuses "no case ran at all" but has no independent constant, so a case
  dropped without hitting zero still passes. Also sorted by path, and also checked: a suite that gains
  a constant and is not moved up into the counted list above fails the same lint run.

  <!-- not-yet-counted-list:start -->
  - `.github/scripts/verdict-gate-selftest.sh`
  - `scripts/railway-destructive-guard-selftest.sh`
  - `tools/nuget-audit-effective-selftest.sh`
  - `tools/verify-eval-snapshot-panel-selftest.sh`
  - `tools/verify-local-mirrors-deploy-selftest.sh`
  - `tools/verify-nuget-audit-selftest.sh`
  <!-- not-yet-counted-list:end -->

  Move a suite from this list into the counted one in the same change that gives it a constant.
- **`needs: []` bypasses stage order, and the review gate depends on stage order.** Since `gl#570`
  `review-verdict` is the **first** stage on GitLab and fails fast, so an unapproved merge request costs
  seconds instead of every lint-stage job, a build and every test-stage suite on a `concurrent = 1` runner —
  count the lint stage with `POST /ci/lint` if you need the number; it moves with the job list and is
  not hand-typed here (`gl#751`). **`allow_failure`
  on that job destroys this, and it is the obvious-looking change that will be proposed again** — it does
  not merely recolour the badge, it stops the job from stopping the pipeline, and `gl#588` measured fifteen
  jobs running to success behind a tolerated gate, the build and all three test suites among them,
  before it was reverted (`CI-SETUP.md` §8). Plain stage
  ordering is the whole mechanism, and it holds only while no merge-request job carries `needs: []` — that
  starts a job immediately whatever its stage. Three jobs in `.gitlab-ci.yml` carry it today
  (`.railway-plan-base`, `railway-drift`, `post-deploy-verify`) and **none of the three is *created* on a
  merge-request pipeline**: their rules require `schedule`, or `web` plus a protected ref. `verdict-selftest`
  carries it deliberately, because it is the suite that proves the reader the gate calls still works, and
  `mergeability-selftest` does for the same reason (`gl#674`). **The `gate` stage holds two jobs since
  `gl#674`** — `review-verdict` and `mergeability` — and **neither may `needs:` the other**: that would skip
  the second whenever the first fails, so an unapproved *and* conflicting merge request would report only
  one of the two (`CI-SETUP.md` §8, *Mergeability*). **Add
  an MR rule to a `needs: []` job and the gate silently stops holding it** — check the created job list on a
  real merge-request pipeline, never the stage list. **`gl#610` added the one exception to the
  `allow_failure` ban above, and its shape is the point:** a `rules:`-level `allow_failure: true`
  matching source branches `^chore/gl[0-9]+-gate-verify$` only — never the job, never the general
  rule — because the gate being first also *skips* `evals`, so the regression the Week 2 brief
  grades could not be observed on a scratch branch at all. `verdict-gate-selftest.sh` cases
  11a–11d redden on each *mechanical* direction that would widen it — 11d being the one that
  checks an untolerated general rule survives, and 11c the one that pins the pattern body, not
  just its anchors. They do **not** pin the pattern's *prefix* (`CI-SETUP.md` §8).
  **Propose nothing broader than a ref pattern here**, and nothing at all on the job
  (`CI-SETUP.md` §8).
- **A Railway plan is a client-side artefact, and nothing pins the client.** `railway config plan` is
  computed in the CLI binary — there is no plan query in Railway's API — so the change set, and therefore
  whether `railway-destructive-guard.sh` exits `10` or `0`, moves with the CLI version. Both hosts install it
  unpinned. Measured on one commit and one environment: 5.57.2 refuses where 5.57.12 and 5.59.0 pass, under
  **either** credential (`gl#511`). **And a plan can be silent about a field rather than about a resource** —
  on 5.59.0 the `mysql` service's `source.image` is not compared at all, while its variables and volume
  attachment are, so an image bump there merges, applies and leaves the old tag running with every guard
  green. Before comparing two plan counts, read `jq -r .cliVersion railway-plan.json` out of each; before
  believing a plan about anything that holds data, read `environment(id:) { config }` instead
  (`DEPLOYMENT.md` §10).
- **`printf … | grep -q` under `pipefail` is a coin flip, not a test.** `grep -q` exits on its first
  match, the writer takes `SIGPIPE` if it had more to write, and the pipeline reports 141 — a match
  read as *no match*. bash line-buffers `printf` into a pipe, so it bites rarely on a short text and
  on every run once the text outgrows the pipe buffer. Measured in `alpine:3.21` on a 600-byte plan:
  the bare `printf | grep -q` pipeline lost 1 of 20 000; the whole guard lost 0 of 20 000 on an idle
  host and 53 of 40 000 under 16-way load. It made the destructive guard
  wave a reworded destructive trailer through as safe drift (`gl#677`). Feed a yes/no `grep` from a
  here-string, and pin every site with its own fixture longer than the pipe buffer. Also, do not trust
  status 1 there: a here-string that bash cannot spool fails with 1 before grep starts. The guard
  asks for `grep -c` and treats a missing count as UNDECIDABLE.
  `gl#677` fixed only the guard; every other early-exit reader under `pipefail` was swept later by
  `gl#724` and `gl#726`, and `tools/pipefail-reader-lint.sh` now refuses a new one (see below).
- **The two pipelines do not share a userland.** GitHub runs `ubuntu-latest` (GNU); the GitLab lint and gate
  jobs run `alpine:3.21` (BusyBox), where `date -d` parses no ISO-8601 at all and awk is not gawk. Run a
  shell gate under `docker run --rm -v "$PWD:/w" alpine:3.21` before trusting it on both.
- **The evals job is a hard gate**, not advisory (Core Req 6). A change that makes it flaky is a defect in the
  change.
- **The license scan is how a restrictive bump gets caught** — `allowed-licenses.json`, not review attention.
  FluentAssertions v8+ is the standing example.
- **`nginx.conf.template` is linted rendered**, so a template edit that only breaks under real substitution still
  fails CI. Render it locally the way the job does before pushing.
- **Line endings are LF everywhere**, pinned in `.gitattributes` and `.editorconfig`, which have to agree —
  otherwise `dotnet format` follows the host and a Windows contributor sees violations CI does not.
- **A local check that disagrees with CI is worse than no local check.** When they diverge, fix the divergence.
- **An early-exit reader fed by a pipe under `pipefail` can read a match as a miss, and a lint now refuses a
  new one** (`gl#724`). `printf "$text" | grep -q X`: grep exits at the match, the writer dies of `SIGPIPE`
  if it had more to write, and the pipeline's 141 reads as *false* — certain once the text after the match
  outgrows the pipe buffer. `gl#677` found it in the destructive guard; `gl#724` audited every such site in
  `scripts/`, `tools/`, `.gitlab/ci/` and `.github/scripts/` and found it **failing open** in three more
  places: the snapshot guard's two "was this backup here before the run?" reads (a pre-existing backup
  accepted as this run's snapshot), `railway-backup-schedules.sh remove`'s two DAILY reads (a schedule
  reported gone that was still there), and `mergeability-gate-selftest.sh`'s `has()`, whose `chk no`
  could pass against a gate that printed the line. Each now reads a **here-string and asks for a count**,
  and a missing count is neither answer (`gl!612`'s `has()`): a here-string bash cannot spool fails with
  status 1, grep's own "no match", before grep starts. **`tools/pipefail-reader-lint.sh`** runs in the
  `backup-schedules-selftest` job on both hosts and fails on any `| grep -q`/`-l`/`-L`/`-m` in a file that
  turns `pipefail` on, unless `tools/pipefail-reader-lint.allow` carries that exact line with a verdict —
  **SAFE**, **FAIL-CLOSED**, or **FAIL-OPEN** with the item that fixes it. It does not look at
  `| head`, `sed …q` or `awk … exit`: every one here is a `$(…)` capture whose value survives and whose
  status nobody reads, so a new one in an `if` is still yours to catch. Its self-test ends `35 of 35`
  against an `EXPECTED` constant (`CI-SETUP.md` §10).

## What bites in the stack

The runbook's *Known quirks* ([`DEPLOYMENT.md`](../DEPLOYMENT.md) §7) is the list; read it before you change
compose. The shape of the trap is the same each time: a failure whose message points somewhere other than its
cause — an empty named volume reported as a missing PHP file, a container healthy minutes before it is usable, a
proxy that starts fine and 502s, a FHIR patient id that silently became someone else after a reseed. **When you
hit one, add it there in the same change** rather than in the MR description, which nobody greps.

## Definition of done

For a tool that is not the lead, done is the MR open and the item in `work::review`, handed back signed
([`multi-agent.md`](multi-agent.md)); the rest below is the lead's to carry. Otherwise: pipeline green — **on GitLab that is only reachable once a verdict exists**, because the gate is the first
stage and fails fast (`CI-SETUP.md` §1), so a merge request's pipeline is red by design until it is reviewed · **the deploy verified at the front door rather than assumed** (`scripts/post-deploy-verify.sh`; a
green apply and a healthy container both say nothing about whether the application answers) · the same image runs locally and deployed · no secrets in source, logs or image layers · every
setting the operator must supply present in `.env.example` **and** explained in the runbook · the one-origin
invariant and the published/internal boundary provably intact after the change · the affected section of
`CI-SETUP.md` / `DEPLOYMENT.md` / `DEPLOYMENT_TOPOLOGY.md` updated in the same change · new quirks recorded where
the next operator will look.
