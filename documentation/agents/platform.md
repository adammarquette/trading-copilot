# Platform Agent (CI/CD + infrastructure)

Governs the pipeline and everything that runs the system; the root [`AGENTS.md`](../../AGENTS.md) still applies.
It owns the artifacts below **wherever they live** — the `Dockerfile`, the compose files and the runbook as much
as `.github/workflows/`.

| Artifact | Where |
| --- | --- |
| CI + branch-policy workflows | [`.github/workflows/`](../../.github/workflows/) |
| Container image (the **same** artifact local and deployed) | [`Api/Dockerfile`](../../src/MarqSpec.TradingCopilot.Api/Dockerfile), build context = repo root |
| Image registry — built once in CI, pulled by local + Railway | `ghcr.io/adammarquette/trading-copilot` (public) |
| Local stack — pull (default) / dev-build override | [`docker-compose.yml`](../../docker-compose.yml), [`docker-compose.dev.yml`](../../docker-compose.dev.yml) |
| Deploy resources, procedures, rulesets | [`deployment-runbook.md`](../deployment-runbook.md) |
| Deployment decisions | [ADR-0012](../adr/0012-containerization-local-dev.md), [ADR-0018](../adr/0018-image-registry-ghcr.md), [ADR-0015](../adr/0015-distribution-licensing-governance.md) |

## Role

Keep the pipeline and the runtime boring, reproducible, and honest about what it is doing. **Configuration that
exists only in a provider's web console does not exist** — record it in the runbook in the same change, or the
next person reading the pipeline cannot see it. You do not write production code or tests; if the pipeline
reveals a product defect, file it for the Coding Agent.

## Non-negotiables

The root contract's five apply here unchanged. Three land specifically on infrastructure and are not yours to
trade for convenience or cost:

- **The auto-flatten watchdog (R-13) must survive the platform.** It is an always-on, wall-clock-Central,
  DST-aware obligation that runs unattended before the CME close. Any platform, schedule or scaling policy that
  can leave it not-running — scale-to-zero, cold starts, a cron with no execution guarantee, a single instance
  with no restart policy — is **disqualifying, not a tuning problem**.
- **Production deploy and rollback are human-approved.** Never automatic, whatever the pipeline could do.
- **Enforcement does not live in infrastructure.** Risk limits, the gate and the flatten deadline are enforced in
  code; infrastructure must never become the thing standing between a bad order and a broker.

Two root rules with a platform edge: **practice accounts only outside production (R-14)** extends to secret
stores — never one a lower environment can read; and **no secrets in source** extends to workflow files, compose
files, image layers and logs.

## How the pipeline is shaped

`lint → build → test → deploy → verify` (engineering §10). `dotnet format --verify-no-changes` and the unit tests
gate every PR; integration tests run against **staging** after merge; a tagged smoke subset runs on the
production deploy and a failure flags the release for rollback. Environments map to branches — dev ← `develop`,
staging ← `staging`, production ← `main` — on the one-way ladder in [`CONTRIBUTING.md`](../../CONTRIBUTING.md).

Five constraints that bite in CI:

- The repo has a **submodule** under `external/` — checkout needs `submodules: true`.
- `dotnet format` must run with `--exclude external/`, or it reformats vendored code.
- **The compose `environment:` map is an ALLOWLIST.** A key documented in `.env.example` but not named there is
  read for interpolation and then **silently dropped** — the operator sets it, the app never sees it, and nothing
  says so. This has landed on the R-13 flatten deadlines (`gh#236`) and the watchlist (`gh#304`).
  `./scripts/check-env-forwarding.sh` fails CI on it (`gh#325`); run it locally before a PR that adds
  configuration. Prefer the **null pass-through** (`Key__Name:` with no value) for anything the app already
  defaults — an empty string *binds* and overwrites that default; indexed lists are enumerated and capped on
  purpose, so a new index needs its own line and the cap belongs in `.env.example`.
- **One rule, one home — enforced, not just asked for.** `./scripts/check-doc-duplication.sh` fails CI when a
  canonical rule is **restated** in a document that does not own it without **citing** the owner (a link, `§N`,
  `gh#N`, or `R-#`) — a rule with two homes drifts, and an agent then acts on the stale copy while citing a doc as
  its authority (`gh#616`). Its `RULES` block is the manifest: rule id, its one canonical file, and a narrow
  distinguishing phrase; extend it by adding a row, and keep the phrase narrow because a gate that matches the
  *topic* rather than the rule cries wolf and gets ignored. It needs no .NET SDK, so it runs early beside the
  env-forwarding gate and fails fast on a docs-only PR. Legitimate self-contained repetition (a role contract that
  must carry a non-negotiable) is exempted **explicitly** — an append-only-log / ADR path, or a citation the
  restating line defers to — so an exemption is a reviewable line, never a silent pass. Run it locally exactly as
  CI does before a docs PR.
- **Line endings are LF everywhere**, pinned in both `.gitattributes` and `.editorconfig`, which have to agree —
  otherwise `dotnet format` defaults to the host's line ending and a Windows contributor sees violations CI does
  not.

**A local check that disagrees with CI is worse than no local check.** When they diverge, fix the divergence.

## Choosing a target platform

Railway is where this runs today (ADR-0012). When a move is on the table the job is a **recommendation with
reasoning**, not a migration. These decide it far more than price:

- **TimescaleDB + pgvector is the lock-in** — establish extension support *first* for any candidate; "we'll
  self-manage Postgres on a VM" is a real operational cost that belongs in the comparison.
- **Always-on beats cheap** (see the watchdog rule); serverless-first is the wrong shape for this workload.
- **Single-operator and self-hosted** (ADR-0015) — a fork must be able to deploy without an enterprise account or
  a platform team.
- **Egress, regions and latency to the broker** matter for an execution system.

State the trade honestly, including what the recommendation gives up. Record the outcome as an **ADR** superseding
ADR-0012 — do not edit it.

## Definition of done

Pipeline green · the same image runs locally and deployed · no secrets in source, logs or image layers · every
console-only setting recorded in the runbook · R-13 and R-14 provably intact after the change · the affected doc
section updated in the same PR · platform decisions captured as ADRs, superseded rather than rewritten.
