#!/usr/bin/env bash
# check-deploy-workflows.sh — fail when the AWS deploy path is the wrong shape.
#
#   scripts/check-deploy-workflows.sh [workflows-dir]     (default: .github/workflows)
#
# WHY THIS EXISTS (gh#1187, ADR-0030 decision 3 / 4 / 8 / 11)
#
# Several of the ways a digest-deploy can be wrong are silent and green:
#   * an unversioned {{resolve:ssm}} is *no changes* on an identical template;
#   * aws ssm put-parameter over a CloudFormation-managed history parameter is drift;
#   * environment: ${{ inputs.environment }} is a name check-release-gate.sh cannot vouch for;
#   * environment: staging is a second manual approval on every release;
#   * :latest is whatever was tagged last;
#   * a twelve-digit account id in an ARN is a secret-shaped literal in a public repository.
#
# This reads the workflow files. Shape from TopstepX check-deploy-workflows.sh; this product's
# stack names and ADR citations are ours.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

WORKFLOWS_DIR="${1:-.github/workflows}"
[ -d "$WORKFLOWS_DIR" ] || die "no such directory: $WORKFLOWS_DIR"

RELEASE="$WORKFLOWS_DIR/release.yml"
DEPLOY="$WORKFLOWS_DIR/deploy.yml"

failed=0
checked=0

fail() {
  printf '\033[31mNO\033[0m  %s\n' "$*" >&2
  failed=$((failed + 1))
}

pass() {
  ok "ok  $*"
}

strip_comments() {
  awk '
    /^[[:space:]]*#/ { next }
    { sub(/[[:space:]]+#.*$/, ""); print }
  ' "$1"
}

job_block() {
  local file="$1" job="$2"
  strip_comments "$file" | awk -v job="$job" '
    $0 == "jobs:" { injobs = 1; next }
    injobs && $0 ~ "^  [A-Za-z0-9_-]+:" {
      name = $0
      sub(/^  /, "", name)
      sub(/:.*/, "", name)
      printing = (name == job)
      next
    }
    injobs && printing && $0 ~ /^  / { print; next }
    injobs && printing && $0 !~ /^  / && $0 !~ /^$/ { exit }
  '
}

require_file() {
  local path="$1"
  checked=$((checked + 1))
  if [ ! -f "$path" ]; then
    fail "missing $path"
    return 1
  fi
  pass "present  $path"
}

require_job() {
  local file="$1" job="$2" block
  checked=$((checked + 1))
  block="$(job_block "$file" "$job")"
  if [ -z "$block" ]; then
    fail "$file has no job '$job'"
    return 0
  fi
  pass "job      $file / $job"
}

require_in_job() {
  local file="$1" job="$2" needle="$3" label="$4"
  local block
  checked=$((checked + 1))
  block="$(job_block "$file" "$job")"
  case "$block" in
    *"$needle"*) pass "$label" ;;
    *) fail "$file job '$job' never says: $needle  ($label)" ;;
  esac
}

refuse_in_job() {
  local file="$1" job="$2" needle="$3" label="$4"
  local block
  checked=$((checked + 1))
  block="$(job_block "$file" "$job")"
  case "$block" in
    *"$needle"*) fail "$file job '$job' contains $needle  ($label)" ;;
    *) pass "$label" ;;
  esac
}

job_environment_line() {
  local file="$1" job="$2"
  job_block "$file" "$job" | awk '
    $0 ~ /^    steps:/ { exit }
    $0 ~ /^    environment:/ { print; exit }
  '
}

require_no_environment() {
  local file="$1" job="$2" line
  checked=$((checked + 1))
  line="$(job_environment_line "$file" "$job")"
  if [ -n "$line" ]; then
    fail "$file job '$job' declares an environment: $line
  Staging is already behind the production approval on gate; a named environment is a second
  manual approval, and loosening that gate is the inert-gate class check-release-gate.sh refuses."
    return 0
  fi
  pass "no environment: on $file / $job"
}

require_literal_environment() {
  local file="$1" job="$2" expected="$3" line rest
  checked=$((checked + 1))
  line="$(job_environment_line "$file" "$job")"
  rest="${line#*:}"
  rest="${rest#"${rest%%[![:space:]]*}"}"
  rest="${rest%%[[:space:]]*}"
  case "$rest" in
    *'${{'*)
      fail "$file job '$job' names the environment as an expression ($rest).
  check-release-gate.sh refuses a \${{ }} name; write the literal out."
      ;;
    "$expected")
      pass "environment: $expected on $file / $job"
      ;;
    *)
      fail "$file job '$job' environment is '$rest', not '$expected'"
      ;;
  esac
}

require_text() {
  local file="$1" needle="$2" label="$3"
  checked=$((checked + 1))
  if grep -Fq -- "$needle" "$file"; then
    pass "$label"
  else
    fail "$file never says: $needle  ($label)"
  fi
}

refuse_account_arn() {
  local file="$1"
  checked=$((checked + 1))
  if grep -E -n 'arn:aws:iam::[0-9]{12}:' "$file"; then
    fail "$file contains a twelve-digit account-id ARN (public repository; use vars.AWS_ACCOUNT_ID)"
  else
    pass "no account-id ARN in $file"
  fi
}

refuse_long_lived_key() {
  local file="$1"
  checked=$((checked + 1))
  if grep -E -n 'AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|secrets\.AWS_' "$file"; then
    fail "$file names a long-lived AWS key (ADR-0030 decision 4; use OIDC)"
  else
    pass "no long-lived AWS key in $file"
  fi
}

if require_file "$RELEASE"; then
  require_job "$RELEASE" "publish"
  require_in_job "$RELEASE" "publish" "digest:" "publish exposes the image digest as an output"
  require_job "$RELEASE" "deploy-staging"
  require_job "$RELEASE" "deploy-production"

  require_in_job "$RELEASE" "deploy-staging" "needs: publish" "deploy-staging needs publish"
  require_no_environment "$RELEASE" "deploy-staging"
  require_in_job "$RELEASE" "deploy-staging" "id-token: write" "deploy-staging requests an OIDC token"
  require_in_job "$RELEASE" "deploy-staging" "GitHubDeploy-staging" "deploy-staging assumes GitHubDeploy-staging"

  require_in_job "$RELEASE" "deploy-production" "needs: deploy-staging" "deploy-production needs deploy-staging"
  require_literal_environment "$RELEASE" "deploy-production" "aws-production"
  require_in_job "$RELEASE" "deploy-production" "id-token: write" "deploy-production requests an OIDC token"
  require_in_job "$RELEASE" "deploy-production" "GitHubDeploy-production" "deploy-production assumes GitHubDeploy-production"

  for job in deploy-staging deploy-production; do
    require_in_job "$RELEASE" "$job" "deploy-environment.sh" "$job runs the shared deploy script"
    refuse_in_job "$RELEASE" "$job" ":latest" "$job never references :latest"
    refuse_in_job "$RELEASE" "$job" "put-parameter" "$job never writes SSM"
    refuse_in_job "$RELEASE" "$job" "resolve:ssm" "$job never uses {{resolve:ssm}}"
  done
  refuse_account_arn "$RELEASE"
  refuse_long_lived_key "$RELEASE"
fi

if require_file "$DEPLOY"; then
  require_text "$DEPLOY" "workflow_dispatch" "deploy.yml is workflow_dispatch"
  require_text "$DEPLOY" "--ref main" "dispatch is documented as --ref main"
  require_text "$DEPLOY" "type: choice" "environment input is a choice"
  require_text "$DEPLOY" "imagetools inspect" "rollback resolves the digest from the version tag"

  require_job "$DEPLOY" "deploy-staging"
  require_job "$DEPLOY" "deploy-production"
  require_in_job "$DEPLOY" "deploy-staging" "inputs.environment == 'staging'" "staging job is guarded by the staging choice"
  require_in_job "$DEPLOY" "deploy-production" "inputs.environment == 'production'" "production job is guarded by the production choice"
  require_no_environment "$DEPLOY" "deploy-staging"
  require_literal_environment "$DEPLOY" "deploy-production" "aws-production"
  require_in_job "$DEPLOY" "deploy-staging" "GitHubDeploy-staging" "dispatch staging assumes GitHubDeploy-staging"
  require_in_job "$DEPLOY" "deploy-production" "GitHubDeploy-production" "dispatch production assumes GitHubDeploy-production"

  for job in deploy-staging deploy-production; do
    require_in_job "$DEPLOY" "$job" "deploy-environment.sh" "$job runs the shared deploy script"
    refuse_in_job "$DEPLOY" "$job" ":latest" "$job never references :latest"
    refuse_in_job "$DEPLOY" "$job" "put-parameter" "$job never writes SSM"
    refuse_in_job "$DEPLOY" "$job" "resolve:ssm" "$job never uses {{resolve:ssm}}"
  done
  refuse_account_arn "$DEPLOY"
  refuse_long_lived_key "$DEPLOY"
fi

info ""
if [ "$failed" -gt 0 ]; then
  die "$failed of $checked deploy-workflow assertion(s) failed.

A digest that enters the stack through SSM, :latest, or an expression-named environment is a deploy that
looks green and runs the wrong image — or that check-release-gate.sh cannot vouch for. Fix the workflow;
do not delete the assertion to make this green."
fi

ok "ok  $checked deploy-workflow assertion(s) held under $WORKFLOWS_DIR."
