#!/usr/bin/env bash
# image-reference.test.sh — the reference the release pushes is lowercase and one owner/repo.
#
# No network. Runs beside the other no-SDK gates in CI (gh#1187).
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$DIR/../image-reference.sh"

fail=0
check() {
  local expected="$1" got status=0
  shift
  got="$("$@" 2>/dev/null)" || status=$?
  if [ "$status" -ne 0 ]; then
    printf '::error::FAIL  %s exited %s\n' "$*" "$status"
    fail=1
    return
  fi
  if [ "$got" = "$expected" ]; then
    printf 'ok    %s -> %s\n' "$*" "$got"
  else
    printf '::error::FAIL  %s -> %s (want %s)\n' "$*" "$got" "$expected"
    fail=1
  fi
}

check "ghcr.io/adammarquette/trading-copilot" bash "$SCRIPT" "adammarquette/trading-copilot"
check "ghcr.io/adammarquette/trading-copilot" bash "$SCRIPT" "AdamMarquette/Trading-Copilot"

if bash "$SCRIPT" "not-a-pair" >/dev/null 2>&1; then
  printf '::error::FAIL  a missing slash must be refused\n'
  fail=1
else
  printf 'ok    refuses a missing slash\n'
fi

if [ "$fail" -eq 0 ]; then
  printf 'ok  image-reference.sh\n'
  exit 0
fi
exit 1
