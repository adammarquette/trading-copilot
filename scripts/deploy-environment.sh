#!/usr/bin/env bash
# deploy-environment.sh — move a published digest onto one AWS environment.
#
#   scripts/deploy-environment.sh <environment> <version> <digest>
#
#   environment   staging | production
#   version       the image tag without the leading v
#   digest        sha256:<64 lowercase hex> — never :latest
#
# WHY THIS EXISTS (gh#1187, ADR-0030 decision 3 / 5 / 11)
#
# `cdk deploy --parameters ImageDigest=… Version=…` is what moves the task definition. The
# EnvironmentStack OWNS the SSM history parameters and writes them from those same CloudFormation
# parameters, so this script NEVER `put-parameter`s. An unversioned `{{resolve:ssm}}` does not
# redeploy — an identical template is *no changes* and the old digest keeps running.
#
# WHAT IT DOES NOT DO. It does not build an image, assume an IAM role, install the SDK or the
# CDK CLI, invent an account / region / hostname, or probe a live origin (prove-live is gh#1188).
# The workflow that calls it has already assumed trading-copilot-GitHubDeploy-<env>.
#
# Outbound is still a fork (ADR-0030). Pass it as DEPLOY_OUTBOUND — a default here would choose
# for the operator. Account and region are passed as synth context (never literals in this
# file) so staging can Lookup the existing zone (gh#1188). Do not pass --no-lookups on apply.
# Other stack parameters (Hostname, ProjectXDataTier, AlertsEmail) are forwarded from DEPLOY_*
# when set, and omitted when not (CloudFormation keeps previous values on update; first create
# fails naming the missing parameter). DEPLOY_ROOT_DOMAIN is required: Program.cs looks the
# staging zone up whenever account/region are set, and a missing -c rootDomain= throws before
# any stack applies. Production must not reuse the staging domain (that would Create a second
# staging.marqspec.com zone).
#
# THE TEST SEAMS. `DEPLOY_ENVIRONMENT_AWS` and `DEPLOY_ENVIRONMENT_CDK` replace `aws` and
# `npx cdk` so the self-test never needs a credential.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

AWS_BIN="${DEPLOY_ENVIRONMENT_AWS:-aws}"
CDK_BIN="${DEPLOY_ENVIRONMENT_CDK:-}"

run_aws() { "$AWS_BIN" "$@"; }

run_cdk() {
  if [ -n "$CDK_BIN" ]; then
    "$CDK_BIN" "$@"
    return
  fi
  npx cdk "$@"
}

usage() {
  die "usage: scripts/deploy-environment.sh <staging|production> <version> <digest>

  <version>  the published tag without the leading v
  <digest>   sha256: followed by 64 lowercase hex characters; never :latest"
}

[ $# -eq 3 ] || usage

ENV_NAME="$1"
VERSION="$2"
DIGEST="$3"

case "$ENV_NAME" in
  staging|production) ;;
  *) die "environment must be staging or production, not '$ENV_NAME'" ;;
esac

[ -n "$VERSION" ] || die "version is empty"
case "$VERSION" in
  latest|:latest|*latest*) die "refusing version '$VERSION': a deploy never references :latest (ADR-0030)" ;;
esac
printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$' \
  || die "version '$VERSION' is not MAJOR.MINOR.PATCH"

[ -n "$DIGEST" ] || die "digest is empty"
case "$DIGEST" in
  *latest*) die "refusing digest '$DIGEST': a deploy never references :latest (ADR-0030)" ;;
esac
printf '%s' "$DIGEST" | grep -Eq '^sha256:[0-9a-f]{64}$' \
  || die "digest '$DIGEST' is not sha256: followed by 64 lowercase hex characters"

STACK="trading-copilot-${ENV_NAME}"

OUTBOUND="${DEPLOY_OUTBOUND:-}"
[ -n "$OUTBOUND" ] || die "DEPLOY_OUTBOUND is empty; the tasks' outbound path is undecided (ADR-0030) and nothing here chooses for the operator"

ROOT_DOMAIN="${DEPLOY_ROOT_DOMAIN:-}"
[ -n "$ROOT_DOMAIN" ] || die "DEPLOY_ROOT_DOMAIN is empty; credentialed synth looks the staging zone up and needs -c rootDomain="
if [ "$ENV_NAME" = "production" ] && [ "$ROOT_DOMAIN" = "staging.marqspec.com" ]; then
  die "DEPLOY_ROOT_DOMAIN=staging.marqspec.com on production would Create a second staging.marqspec.com zone (gh#1188)"
fi

REGION="$(run_aws configure get region 2>/dev/null || true)"
if [ -z "$REGION" ]; then
  REGION="${AWS_DEFAULT_REGION:-${AWS_REGION:-}}"
fi
[ -n "$REGION" ] || die "AWS region is empty; configure-aws-credentials did not set AWS_DEFAULT_REGION"

ACCOUNT="$(run_aws sts get-caller-identity --query Account --output text)"
[ -n "$ACCOUNT" ] || die "sts get-caller-identity returned no account"
[ "$ACCOUNT" != "None" ] || die "sts get-caller-identity returned no account"

# Optional parameters — forwarded only when the operator set them. Never defaulted.
extra_params=()
add_param() {
  local name="$1" value="$2"
  [ -n "$value" ] || return 0
  extra_params+=(--parameters "${name}=${value}")
}
add_param RootDomain "${ROOT_DOMAIN}"
add_param Hostname "${DEPLOY_HOSTNAME:-}"
add_param ProjectXDataTier "${DEPLOY_PROJECTX_DATA_TIER:-}"
add_param AlertsEmail "${DEPLOY_ALERTS_EMAIL:-}"

info "deploying $STACK  version=$VERSION  digest=$DIGEST  outbound=$OUTBOUND"
context_args=(
  -c "outbound=${OUTBOUND}"
  -c "account=${ACCOUNT}"
  -c "region=${REGION}"
  -c "rootDomain=${ROOT_DOMAIN}"
)
(
  cd "$REPO_ROOT/infra"
  if [ "${#extra_params[@]}" -gt 0 ]; then
    run_cdk deploy "$STACK" \
      --require-approval never \
      "${context_args[@]}" \
      --parameters "ImageDigest=${DIGEST}" \
      --parameters "Version=${VERSION}" \
      "${extra_params[@]}"
  else
    run_cdk deploy "$STACK" \
      --require-approval never \
      "${context_args[@]}" \
      --parameters "ImageDigest=${DIGEST}" \
      --parameters "Version=${VERSION}"
  fi
)

ok "deployed $ENV_NAME  $VERSION  $DIGEST"
info "probe the hostname from the runbook; this script does not invent one and does not curl one"
