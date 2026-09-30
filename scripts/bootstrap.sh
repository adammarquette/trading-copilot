#!/usr/bin/env bash
# bootstrap.sh — create the GitHub Environments the release workflow's approval gate depends on.
#
#   scripts/bootstrap.sh [owner/repo] [--dry-run]
#
# WHY THIS EXISTS (gh#1187)
# -------------------------
# `environment: production` does not create or require anything. If the environment does not
# exist, GitHub CREATES IT AT RUN TIME WITH NO PROTECTION RULES and the job passes straight
# through — no warning, no annotation, no error. release.yml's `gate` would then approve nothing
# and `publish` would push a public version tag unattended.
#
# That setting is a repository console action CI cannot do. Configuration that exists only in a
# provider's web console does not exist (platform contract). This script is the recorded procedure.
#
# WHAT IT DOES NOT DO. It does not rewrite rulesets, labels, or the branch ladder — those already
# live on this repository. It deploys nothing and sets no repository variable or secret.
#
# HISTORY. It also created `aws-production` and read the repository's Actions OIDC subject for
# the AWS deploy roles. Both went with the AWS plan (ADR-0030, gh#1215, gh#1232). Deleting that
# environment from an existing repository is the operator's, in Settings > Environments; this
# script never deletes.
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

# ONE environment, a human approval on an irreversible consequence (ADR-0018, gh#1187):
#
#   - production  gates the version-tag publish on release.yml's `gate` job — a public GHCR
#                  version tag cannot be un-pulled.
#
# The names are hardcoded rather than parsed out of the workflows: this script takes a repo
# SLUG and may be run from anywhere, with no checkout to read. The other direction is held by
# scripts/tests/bootstrap-environments.test.sh, which reads this line and fails when it names
# more or fewer environments than the workflows do (gh#1232).
ENV_NAMES="production"

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

step "Done"
ok "$REPO environment bootstrap recorded."
info "Verify with: scripts/check-release-gate.sh (fails when a workflow-named environment is missing or unprotected)."
