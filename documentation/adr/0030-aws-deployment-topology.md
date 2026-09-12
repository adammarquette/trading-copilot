# ADR-0030: AWS deployment topology — Fargate behind an ALB, Timescale on EFS, CDK in C#, GHCR by digest

**Status:** Accepted · **Date:** 2026-09-12 · **Deciders:** Adam (operator/maintainer)
**Extends:** [ADR-0012](0012-containerization-local-dev.md) (containerize / compose / config-driven DB / no
secrets in the image) and [ADR-0018](0018-image-registry-ghcr.md) (build once in CI, publish to GHCR). Neither
Decision is rewritten; this record names the AWS consumer of that image.
**Cites:** [ADR-0015](0015-distribution-licensing-governance.md) (self-hosted, fork-first, public image).
**Relates to:** PRD `R-13` (auto-flatten — always-on), `R-14` (practice / live), `R-18` (JWT);
[ADR-0002](0002-observability.md) (OTLP boundary), [ADR-0003](0003-authentication.md),
[ADR-0013](0013-failure-recovery-model.md), [ADR-0019](0019-alerting-channel-and-thresholds.md);
engineering §8 / §10; [deployment runbook](../deployment-runbook.md). Issue: `gh#1185`, parent epic `gh#1184`.

**Pattern library (shape only):** TopstepX
[ADR-0023](https://github.com/adammarquette/MarqSpec.Mcp.TopstepX/blob/develop/documentation/adr/0023-aws-deployment-topology.md)
— cite it; do not paste it. That record's account IDs, hosted-zone IDs, hostnames, Cognito issuer, and
MCP-specific constraints are **theirs**. This product keeps its own JWT (R-18) and its own flatten duty (R-13).

## Context

The always-on cloud today is Railway (engineering §8, the [runbook](../deployment-runbook.md)). The AWS epic
(gh#1184) moves that home. Without a topology record, a CDK stack would guess environment count, the Railway
sunset, and what is allowed to trigger a production deploy. Those are product and platform calls, not
construct defaults.

gh#1185 leaned the table below before this claim. No later comment on the issue named a different pick. This
ADR records those leans and the four explicit choices the card required. It does not add `infra/`, workflows,
or an AWS apply.

## Decision

**One CDK app in C# under `infra/`, instantiated for two AWS environments (staging and production). Each
environment is an Application Load Balancer in front of two Fargate services — the GHCR app image by digest,
and the Timescale + pgvector image this repo already tests (`timescale/timescaledb-ha:pg17`) by digest with
its data on EFS. GitHub deploys through OIDC. Production is always-on (R-13) and human-approved. Railway
stays in parallel until AWS staging has proven flatten on practice.**

1. **IaC is AWS CDK in C# (`infra/`).** Same language as the product and as the pattern library. Not Terraform,
   not hand-edited CloudFormation. Template tests will ride `dotnet test` when the CDK child lands. This record
   does not create `infra/`.

2. **Runtime is ECS Fargate + ALB, TLS at the edge.** No App Runner. No Lambda-as-the-app — R-13 is an
   unattended, session-long duty and cannot live on request-scoped compute. The ALB is the reverse proxy
   (certificate, host rule, `/health` target); the task does not hold a certificate.

3. **The image stays GHCR ([ADR-0018](0018-image-registry-ghcr.md)).** ECS pulls **by digest**. A deploy never
   references `:latest`, and a floating branch tag (`:develop` / `:staging` / `:main`) is not what a task
   definition runs. Merge-to-branch publish to GHCR stands; the digest is what AWS runs.

4. **Auth to AWS is GitHub OIDC deploy roles.** No long-lived AWS access keys in GitHub, in a workflow, or
   anywhere else.

5. **Secrets are Secrets Manager shells, injected as ECS `valueFrom`.** SSM Parameter Store holds only deploy
   *history* (image digest / version) that the pipeline writes. Hand `put-parameter` is not a deploy path.
   Secrets never enter the image ([ADR-0012](0012-containerization-local-dev.md)).

6. **The data plane is Fargate Postgres + EFS, not RDS.** This product's store is Timescale + pgvector
   (engineering §2; compose and the integration factory already pin `timescale/timescaledb-ha:pg17`). RDS for
   PostgreSQL cannot run that image, so a managed RDS instance would make every Timescale / pgvector proof
   describe a database nobody deploys. Timescale Cloud is a second vendor outside this repo's IaC and is
   rejected for the same reason. **EC2 + EBS is the named escalation** if EFS NFS `fsync` cost is later
   measured as intolerable — not the plan.

7. **App auth stays the existing JWT (R-18, [ADR-0003](0003-authentication.md)).** Do not import the pattern
   library's Cognito user pool, MCP resource server, or host callbacks. The Internet-exposed surface remains
   the BFF + SignalR hub this product already ships.

8. **Production is always-on, and production deploy and rollback are human-approved (R-13).** Desired count
   ≥ 1, with a restart policy that brings the task back without an operator. Scale-to-zero is **disqualifying**
   on production and on any environment that could hold a live flatten. A merge to `main` must not start a
   production deploy. Rollback is a human action against a previously deployed digest, not an automatic
   revert.

9. **Two AWS environments: staging and production.** `develop` stays local compose
   ([ADR-0012](0012-containerization-local-dev.md)). The runbook's three-branch Railway map is the *running*
   cloud; AWS does not grow a third "dev" environment.

10. **Staging is practice-only (R-14) and never attaches a live account**, so it never runs R-13 against live.
    Scale-to-zero on staging is therefore allowed as a cost control. While flatten is still being proven on
    practice — the gate before Railway can sunset — staging's default remains desired count ≥ 1.

11. **AWS deploy trigger is a release, not a merge.** Adopt the pattern library's shape: a version tag /
    GitHub Release publishes the digest to staging, then a gated production approval deploys **that same
    digest**. ADR-0018's merge-to-branch GHCR tags remain the registry's "current" labels; they are not an
    AWS deploy trigger. Production cannot fire because `main` moved.

12. **Railway runs in parallel until AWS staging has proven R-13 on practice; then it sunsets in a dated
    Update on this record.** A one-step cutover would put flatten between clouds. Until that Update, the
    runbook continues to describe Railway as the running cloud.

13. **Observability keeps the existing OTLP boundary ([ADR-0002](0002-observability.md)).** On AWS, an OTLP
    collector sidecar in the app task exports to CloudWatch (metrics and logs). Do not stand up Prometheus /
    Loki / Tempo as further Fargate services on the flatten path, and do not add X-Ray as a second tracing
    system. [ADR-0019](0019-alerting-channel-and-thresholds.md) flatten / liveness / Pushover paging is **not**
    dropped; CloudWatch alarms (task count, unhealthy target, deploy rollback) sit beside that path, they do
    not replace it. Local compose may keep the opt-in LGTM stack the runbook already describes.

14. **Account id, region, and hostname pattern are operator-supplied before the first CDK apply.** This
    record does not invent them. A `*.marqspec.com` family is a *candidate* named on gh#1185, not a decision.
    The pattern library's account IDs, hosted zones, and hostnames are not ours.

15. **Fork-first and public ([ADR-0015](0015-distribution-licensing-governance.md)).** The GHCR image stays
    public, so ECS pulls with no registry credential. A flip to private is a dated Update and a
    `repositoryCredentials` fallback, not a silent change.

## Alternatives considered

**A third AWS "dev" environment** matching today's Railway `develop` slot. Rejected. Local compose already
*is* dev ([ADR-0012](0012-containerization-local-dev.md)); a third always-on (or even scale-to-zero) AWS env
adds cost and another place a live credential could be wired by mistake, for no rehearsal the staging
practice account does not already provide.

**Keep merge-to-branch as the AWS deploy trigger** (`:main` moves → production). Rejected. R-13 production
must not start because a merge succeeded. The registry may still tag `:main` on that merge
([ADR-0018](0018-image-registry-ghcr.md)); the *deploy* is a separate, human-approved release.

**Cut Railway over in one step** once the first AWS stack exists. Rejected. Flatten would sit between
clouds with no proven AWS rehearsal. Parallel, then a dated sunset Update after staging has proven R-13 on
practice.

**RDS for PostgreSQL, or Timescale Cloud.** Rejected — see Decision 6. Managed convenience is real; a
different database than the one CI tests is not acceptable for the store the flatten path reads.

**App Runner or Lambda as the app host.** Rejected — see Decision 2. Scale-to-zero and request-scoped
compute are the same class of failure for R-13.

**Terraform.** Rejected. HCL would sit outside `dotnet format`, `dotnet test`, and the existing audit gates;
C# CDK in the solution rides them. The pattern library made the same call for the same reason; this
product's reason is this repo's CI, not that record's account.

**Long-lived AWS keys in GitHub.** Rejected. A static key in a public repository's settings has no expiry
bound to a ref or a GitHub environment; OIDC does.

**Import Cognito (or any second issuer) for the BFF.** Rejected — see Decision 7. R-18 is already JWT on
this product; a second issuer is a different auth system, not a topology detail.

**Self-host LGTM on Fargate, or add X-Ray as the AWS trace store.** Rejected for the AWS landing — three
more stateful services next to flatten, or a second tracing system beside OTLP. CloudWatch via the existing
OTLP boundary is the AWS landing; ADR-0019 paging stays.

**Invent an AWS account id, region, or hostname so CDK can start.** Rejected. The operator supplies those
before apply. Guessing them is how a topology record becomes a second, wrong inventory.

## Consequences

**Positive**
- CDK and workflow children inherit env count, trigger, Railway sunset, digest-only deploys, and the R-13
  always-on / human-approved production rule instead of guessing them.
- Local compose and GHCR publish keep their jobs ([ADR-0012](0012-containerization-local-dev.md),
  [ADR-0018](0018-image-registry-ghcr.md)); AWS is a new consumer, not a rewrite of "build once."
- Staging can rehearse flatten on practice without a live account (R-14) and without a third AWS bill.
- The public image and JWT surface stay forkable ([ADR-0015](0015-distribution-licensing-governance.md),
  R-18).

**Negative / costs**
- Two clouds run until the sunset Update — Railway cost continues through the proof.
- Fargate Postgres on EFS pays NFS `fsync` cost; the EC2 + EBS escalation is named, not measured.
- Production has no scale-to-zero escape; the always-on bill is the R-13 bill.
- Account, region, and DNS do not exist in this record. The first `cdk apply` is blocked on the operator,
  not on a guessed id.
- A 503 window during a single-task deploy is accepted until a later increment argues for more than
  desired count 1; this record requires ≥ 1, it does not require a multi-task roll.

**The runbook and engineering §8 still describe Railway as the running cloud.** A procedure for a stack
that does not exist would be the stale-doc break in the other direction. They gain a pointer here; AWS
procedures land with the CDK / workflow children.

## What this does not decide

- AWS account id, region, hosted zone, or hostname (operator supplies before apply).
- Task CPU / memory, AZ count, NAT vs. public IP vs. VPC endpoints.
- The GitHub environment *name* on the production gate (the pattern is a `required_reviewers`
  environment; the name lands with the workflow child).
- Backup cadence, WAF, budget alarms, cost tags.
- CDK stack and project names under `infra/`.

## Follow-ups

- CDK app under `infra/` (epic child — not this card).
- OIDC roles + the release-triggered deploy workflow (epic child — not this card). Not a merge-to-`main`
  deploy.
- Operator supplies account id, region, and hostname pattern before the first apply.
- Dated **Update** on this record when Railway sunsets, after AWS staging has proven R-13 on practice.
- Measure EFS vs. the EC2 + EBS escalation on staging if WAL/`fsync` cost shows up under a real session.
