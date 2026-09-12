#!/usr/bin/env bash
# check-release-gate-selftest.sh — require that check-release-gate.sh can still go red.
#
#   scripts/check-release-gate-selftest.sh
#
# Non-zero exit is not sufficient: the gate also exits 1 for "gh is required". Each case
# matches on the words that name ITS OWN fault (gh#1187).

set -euo pipefail

red() { printf '\033[31m%s\033[0m\n' "$*" >&2; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }
info() { printf '%s\n' "$*"; }
die() { red "$*"; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$REPO_ROOT/scripts/check-release-gate.sh"
[ -f "$GATE" ] || die "not found: $GATE"

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT
failures=0

expect_red() {
  local label="$1" dir="$2" needle="$3"
  local out status=0
  out="$(bash "$GATE" "$dir" 2>&1)" || status=$?
  if [ "$status" -eq 0 ]; then
    red "SELF-TEST FAILED  $label"
    info "  The gate accepted a fixture it must reject."
    printf '%s\n' "$out" | sed 's/^/  | /'
    failures=$((failures + 1))
    return
  fi
  case "$out" in
    *"$needle"*) ok "rejected  $label" ;;
    *)
      red "SELF-TEST FAILED  $label — exited $status but never said: $needle"
      printf '%s\n' "$out" | sed 's/^/  | /'
      failures=$((failures + 1))
      ;;
  esac
}

mkdir -p "$FIXTURES/empty"
printf 'name: nothing\non: push\njobs:\n  x:\n    runs-on: ubuntu-latest\n    steps:\n      - run: true\n' > "$FIXTURES/empty/ci.yml"
expect_red "no environment key" "$FIXTURES/empty" "no \`environment:\` key"

mkdir -p "$FIXTURES/expr"
printf 'name: expr\non: push\njobs:\n  x:\n    runs-on: ubuntu-latest\n    environment: ${{ inputs.environment }}\n    steps:\n      - run: true\n' > "$FIXTURES/expr/deploy.yml"
expect_red "expression name" "$FIXTURES/expr" "UNCHECKABLE"

mkdir -p "$FIXTURES/dispatch-only"
# A workflow_dispatch input named environment must not be discovered as a job-level gate.
cat > "$FIXTURES/dispatch-only/deploy.yml" <<'YAML'
name: Redeploy
on:
  workflow_dispatch:
    inputs:
      environment:
        type: choice
        options: [staging, production]
jobs:
  deploy-staging:
    runs-on: ubuntu-latest
    steps:
      - run: true
YAML
expect_red "dispatch input is not a gate" "$FIXTURES/dispatch-only" "no \`environment:\` key"

expect_red "missing directory" "$FIXTURES/no-such-dir" "no such directory"

info ""
if [ "$failures" -gt 0 ]; then
  die "$failures release-gate self-test assertion(s) failed."
fi
ok "ok  check-release-gate.sh still rejects an inert or uncheckable gate."
