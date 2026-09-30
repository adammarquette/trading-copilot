#!/usr/bin/env bash
# check-deploy-workflows.sh — fail when the release workflow is the wrong shape.
#
#   scripts/check-deploy-workflows.sh [workflows-dir]     (default: .github/workflows)
#
# WHY THIS EXISTS (gh#1187, gh#1232; ADR-0018, ADR-0030 decisions 3 / 11)
#
# release.yml turns a published GitHub release into an immutable image tag, and several ways for that to be wrong
# are silent and green:
#   * environment: ${{ inputs.x }} is a name check-release-gate.sh cannot vouch for, so the human approval can be
#     an inert gate that never asks;
#   * a publish job that does not wait for the approval retags an image nobody approved;
#   * :latest is whatever was tagged last;
#   * a publish job that rebuilds instead of retagging ships bytes CI never tested;
#   * a publish job that exposes no digest gives whatever deploys next nothing to pin.
#
# The workflow used to end in two AWS deploy jobs and be paired with deploy.yml; both went with the AWS plan
# (gh#1215, gh#1232), and this checker went from asserting an AWS deploy path to asserting what remains: the
# approval gate and the retag. A deploy job that follows a Railway deploy path gets its own assertions when it is
# added. Shape from TopstepX check-deploy-workflows.sh; this product's job names are ours.
#
# It reads the workflow file and takes the directory as an argument so scripts/tests/check-deploy-workflows.test.sh
# can run it against deliberately broken copies and prove each assertion goes red for the reason it exists.

set -euo pipefail

die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
ok()  { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }

WORKFLOWS_DIR="${1:-.github/workflows}"
[ -d "$WORKFLOWS_DIR" ] || die "no such directory: $WORKFLOWS_DIR"

RELEASE="$WORKFLOWS_DIR/release.yml"

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

if require_file "$RELEASE"; then
  require_job "$RELEASE" "verify-gate"
  require_job "$RELEASE" "gate"
  require_job "$RELEASE" "publish"

  # The human approval: a literal environment that check-release-gate.sh can vouch for, ordered after the check
  # that the environment is real, and in front of the retag.
  require_in_job "$RELEASE" "gate" "needs: verify-gate" "gate waits for the check that the approval gate is real"
  require_literal_environment "$RELEASE" "gate" "production"
  require_in_job "$RELEASE" "publish" "needs: gate" "publish waits for the human approval"

  # The retag: an immutable version tag pointing at the digest CI already built and tested.
  require_in_job "$RELEASE" "publish" "imagetools create" "publish retags the published digest"
  refuse_in_job "$RELEASE" "publish" "docker build " "publish never rebuilds (ADR-0018)"
  refuse_in_job "$RELEASE" "publish" "build-push-action" "publish never rebuilds through an action"
  refuse_in_job "$RELEASE" "publish" ":latest" "publish never references :latest"

  # What whatever deploys next has to pin.
  require_in_job "$RELEASE" "publish" "digest:" "publish exposes the image digest as an output"
  require_in_job "$RELEASE" "publish" "version:" "publish exposes the version as an output"
fi

info ""
if [ "$failed" -gt 0 ]; then
  die "$failed of $checked release-workflow assertion(s) failed.

A release that skips its approval, rebuilds instead of retagging, or is named by an expression looks green and
ships the wrong image — or is a gate that check-release-gate.sh cannot vouch for. Fix the workflow; do not delete
the assertion to make this green."
fi

ok "ok  $checked release-workflow assertion(s) held under $WORKFLOWS_DIR."
