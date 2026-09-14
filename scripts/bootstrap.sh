#!/usr/bin/env bash
# bootstrap.sh — create the GitHub Environments the AWS release/deploy path depends on.
#
#   scripts/bootstrap.sh [owner/repo] [--dry-run]
#
# WHY THIS EXISTS (gh#1187)
# -------------------------
# `environment: aws-production` does not create or require anything. If the environment does not
# exist, GitHub CREATES IT AT RUN TIME WITH NO PROTECTION RULES and the job passes straight
# through — no warning, no annotation, no error. The production deploy would then assume
# trading-copilot-GitHubDeploy-production with a token that environment minted for anyone who named it.
#
# That setting is a repository console action CI cannot do. Configuration that exists only in a
# provider's web console does not exist (platform contract). This script is the recorded procedure.
#
# WHAT IT DOES NOT DO. It does not rewrite rulesets, labels, or the branch ladder — those already
# live on this repository. It does not `cdk deploy`. It does not invent an AWS account id, region,
# or hostname. The first apply of `trading-copilot-github-oidc` is the operator's, with their own
# credentials, because the workflows cannot assume a role that does not exist yet (gh#1188).
#
# CREATE-ONLY, NEVER OVERWRITE. An environment that already exists is read and reported, never
# written. An environment PUT that names `reviewers` replaces the whole reviewer list.
#
# Idempotent: safe to re-run. A read that does not succeed is fatal — creating on top of a guess
# is how the setting gets replaced.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
warn() { printf '\033[33m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
step() { printf '\n\033[1m%s\033[0m\n' "$*"; }

REPO="${1:-}"
DRY_RUN=false
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=true ;;
    */*) REPO="$arg" ;;
  esac
done

command -v gh >/dev/null 2>&1 || die "gh is required (https://cli.github.com)"
gh auth status >/dev/null 2>&1 || die "gh is not authenticated. Run: gh auth login"

if [ -z "$REPO" ]; then
  REPO="$(gh repo view --json nameWithOwner --jq .nameWithOwner)" \
    || die "could not determine the repository. Pass owner/repo."
fi

gh repo view "$REPO" >/dev/null 2>&1 || die "repository not found or not accessible: $REPO"
info "Bootstrapping GitHub Environments on $REPO"
$DRY_RUN && info "(dry run — nothing will be changed)"

gh_read() {
  local out status
  out=$(gh api "$@") && status=0 || status=$?
  [ "$status" -eq 0 ] || die "read failed (exit $status): gh api $1
Stopping rather than guessing. Nothing further has been written."
  printf '%s' "$out"
}

# ---------------------------------------------------------------------------
# Approval environments.
# ---------------------------------------------------------------------------
step "Approval environments"

# TWO environments, each a separate human approval on a separate consequence (ADR-0030
# decision 8 / 11, gh#1187):
#
#   - production       gates the version-tag publish on release.yml's `gate` job — a public
#                      GHCR version tag cannot be un-pulled;
#   - aws-production   gates WHAT RUNS — the production deploy jobs declare it, and
#                      trading-copilot-GitHubDeploy-production trusts ONLY a token carrying
#                      `sub = repo:<immutable>:environment:aws-production`.
#
# The names are hardcoded rather than parsed out of the workflows: this script takes a repo
# SLUG and may be run from anywhere, with no checkout to read. The other direction is held
# by a template test: GitHubOidcStackTests reads this line.
ENV_NAMES="production aws-production"

ENV_REVIEWERS_JQ='[.protection_rules[]?|select(.type=="required_reviewers")|.reviewers[]?|"\(.type):\(.reviewer.login // .reviewer.slug)"]|join(", ")'

for ENV_NAME in $ENV_NAMES; do
  env_status=0
  env_read="$(gh api "repos/$REPO/environments/$ENV_NAME" 2>&1)" || env_status=$?

  if [ "$env_status" -eq 0 ]; then
    reviewers="$(gh_read "repos/$REPO/environments/$ENV_NAME" --jq "$ENV_REVIEWERS_JQ")" || exit 1
    if [ -n "$reviewers" ]; then
      info "  $ENV_NAME exists and requires: $reviewers"
      info "  left untouched — a PUT here would REPLACE that reviewer list"
    else
      warn "  $ENV_NAME exists but requires NO reviewers — the approval it gates is inert"
      warn "  Add a reviewer in Settings > Environments > $ENV_NAME, or delete it and re-run this script."
    fi
  elif printf '%s' "$env_read" | grep -q 'HTTP 404'; then
    ACTOR_LOGIN="$(gh_read "user" --jq .login)" || exit 1
    ACTOR_ID="$(gh_read "user" --jq .id)" || exit 1
    info "  creating $ENV_NAME, required reviewer: $ACTOR_LOGIN"
    if ! $DRY_RUN; then
      printf '{"reviewers":[{"type":"User","id":%s}]}' "$ACTOR_ID" \
        | gh api -X PUT "repos/$REPO/environments/$ENV_NAME" --input - >/dev/null
      left="$(gh_read "repos/$REPO/environments/$ENV_NAME" --jq "$ENV_REVIEWERS_JQ")" || exit 1
      if [ -n "$left" ]; then
        ok "  $ENV_NAME now requires: $left"
      else
        warn "  $ENV_NAME was created but requires NO reviewers. The approval it gates is inert; fix it by hand."
      fi
    else
      info "  [dry-run] PUT repos/$REPO/environments/$ENV_NAME"
    fi
  else
    die "read failed (exit $env_status): gh api repos/$REPO/environments/$ENV_NAME
$env_read

Stopping rather than guessing. Creating the environment on top of a read that did not succeed would REPLACE
whatever is there, reviewers included. Nothing further has been written."
  fi
done

# ---------------------------------------------------------------------------
# Actions OIDC subject — READ ONLY.
# ---------------------------------------------------------------------------
step "Actions OIDC subject (read-only)"

# Repositories created after 2026-07-15 mint an immutable Actions OIDC sub
# (owner@id/name@id). A trust policy written for repo:owner/name never matches.
# This read reports the prefix the tokens actually carry. It WRITES NOTHING.
oidc_status=0
oidc_prefix="$(gh api "repos/$REPO/actions/oidc/customization/sub" --jq .sub_claim_prefix 2>&1)" || oidc_status=$?
if [ "$oidc_status" -eq 0 ]; then
  if [ -z "$oidc_prefix" ]; then
    warn "  actions/oidc/customization/sub answered with an empty sub_claim_prefix"
  else
    info "  Actions OIDC sub_claim_prefix=$oidc_prefix"
    case "$oidc_prefix" in
      *@*) ok "  prefix is immutable (owner@id/name@id) — trading-copilot-GitHubDeploy-* must trust this exact segment" ;;
      *) warn "  prefix is name-only ($oidc_prefix). A stack that trusts owner@id/name@id will not assume." ;;
    esac
  fi
else
  warn "  could not read actions/oidc/customization/sub (exit $oidc_status)"
  printf '%s\n' "$oidc_prefix" | sed 's/^/  | /' >&2
fi

step "Done"
ok "$REPO environment bootstrap recorded."
info ""
info "Still operator-supplied, never invented here (ADR-0030 decision 14):"
info "  GitHub Actions variables AWS_ACCOUNT_ID and AWS_REGION"
info "  first cdk deploy of trading-copilot-github-oidc (your credentials, not OIDC — the roles do not exist yet)"
info "  RootDomain, Hostname, ProjectXDataTier, AlertsEmail, -c outbound= on the first environment apply"
info "Do not cdk deploy from this script. Prove-live staging is gh#1188."
