#!/usr/bin/env bash
#
# bootstrap-environments.test.sh (gh#1232) -- scripts/bootstrap.sh creates exactly the approval environments the
# workflows name: no fewer, no more.
#
# WHY. `environment: <name>` on a job does not create or require anything; a name GitHub has never seen is
# auto-created at run time WITH NO PROTECTION RULES and the job passes straight through (gh#1187). bootstrap.sh is
# the recorded procedure that creates each one with a required reviewer, so its list has to track the workflows.
#   * FEWER -- a workflow names an environment bootstrap.sh never creates: a fresh fork's first run meets an
#     unprotected gate. check-release-gate.sh catches this on the live repository; this catches it in the diff.
#   * MORE  -- bootstrap.sh creates an environment no workflow names: an approval that gates nothing, which reads
#     as a safety control and is not one. `aws-production` was exactly that after the AWS deploy jobs went (gh#1232).
#
# Until gh#1232 the only thing tying bootstrap.sh's list to anything was GitHubOidcStackTests, a C# test in the
# infra/ CDK app, and it checked one direction for one environment. That app is gone; this replaces it.
#
# The workflow side is read through check-release-gate.sh --list, the SAME discovery the live gate uses, so the
# two cannot disagree about which environments a workflow names. Hermetic: no network, no gh.
set -uo pipefail   # no -e: every assertion must run and report, not stop at the first red

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$DIR/../.." && pwd)"
GATE="$ROOT/scripts/check-release-gate.sh"
BOOTSTRAP="$ROOT/scripts/bootstrap.sh"

# A new suite declares how many assertions it expects, so a dropped case cannot stay green (platform contract).
EXPECTED_ASSERTIONS=7
assertions=0
failures=0

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

check() { # <description> <condition-result: 0 = holds>
  assertions=$((assertions + 1))
  if [ "$2" -eq 0 ]; then printf 'ok    %s\n' "$1"
  else printf 'FAIL  %s\n' "$1"; failures=$((failures + 1)); fi
}

# The environments a bootstrap script declares on its one ENV_NAMES="..." line, one per line, sorted. Prints
# nothing and fails when there is no such line -- an unparseable script must not read as "creates nothing".
bootstrap_envs() {
  local line
  line="$(grep -E '^ENV_NAMES="[^"]*"' "$1")" || return 1
  line="${line#ENV_NAMES=\"}"; line="${line%\"}"
  printf '%s\n' $line | sed '/^$/d' | sort -u
}

# The environments the workflows under a directory name, through the live gate's own discovery.
workflow_envs() {
  bash "$GATE" --list "$1" | sed '/^$/d' | sort -u
}

# 0 when the two lists match. On a mismatch prints what bootstrap is missing and what it has in excess.
compare() { # <workflows-dir> <bootstrap-file>
  local wf bs missing extra
  wf="$(workflow_envs "$1")" || { echo "  workflow discovery failed for $1"; return 2; }
  bs="$(bootstrap_envs "$2")" || { echo "  no ENV_NAMES=\"...\" line in $2"; return 2; }
  [ -n "$wf" ] || { echo "  discovery found no environment under $1 (an empty match is not a pass)"; return 2; }
  missing="$(comm -23 <(printf '%s\n' "$wf") <(printf '%s\n' "$bs"))"
  extra="$(comm -13 <(printf '%s\n' "$wf") <(printf '%s\n' "$bs"))"
  [ -z "$missing" ] && [ -z "$extra" ] && return 0
  [ -n "$missing" ] && echo "  named by a workflow, not created by bootstrap.sh: $(echo $missing)"
  [ -n "$extra" ] && echo "  created by bootstrap.sh, named by no workflow: $(echo $extra)"
  return 1
}

# --- the real tree ---------------------------------------------------------------------------------------------
out="$(compare "$ROOT/.github/workflows" "$BOOTSTRAP")"; status=$?
[ -n "$out" ] && printf '%s\n' "$out"
check "bootstrap.sh creates exactly the environments the workflows name" "$status"

grep -qx production <<<"$(workflow_envs "$ROOT/.github/workflows")"
check "the release approval environment 'production' is still named by a workflow" $?

grep -qx production <<<"$(bootstrap_envs "$BOOTSTRAP")"
check "bootstrap.sh still creates 'production'" $?

# --- fixtures: each direction of drift must go red -------------------------------------------------------------
mkdir -p "$TMP/wf"
cat > "$TMP/wf/release.yml" <<'YAML'
name: Release
on: release
jobs:
  gate:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - run: true
YAML

listed="$(bash "$GATE" --list "$TMP/wf")"; status=$?
[ "$status" -eq 0 ] && [ "$listed" = "production" ]
check "check-release-gate.sh --list prints exactly the fixture's environment and exits 0 (got: '$listed', exit $status)" $?

printf 'ENV_NAMES="production"\n' > "$TMP/bootstrap-exact.sh"
compare "$TMP/wf" "$TMP/bootstrap-exact.sh" >/dev/null
check "a matching fixture pair passes (the comparison can say yes)" $?

printf 'ENV_NAMES="production aws-production"\n' > "$TMP/bootstrap-orphan.sh"
out="$(compare "$TMP/wf" "$TMP/bootstrap-orphan.sh")"; status=$?
[ "$status" -eq 1 ] && [[ "$out" == *"named by no workflow: aws-production"* ]]
check "an orphan environment in bootstrap.sh goes red, naming it" $?

printf 'ENV_NAMES="aws-production"\n' > "$TMP/bootstrap-missing.sh"
out="$(compare "$TMP/wf" "$TMP/bootstrap-missing.sh")"; status=$?
[ "$status" -eq 1 ] && [[ "$out" == *"not created by bootstrap.sh: production"* ]]
check "a workflow environment bootstrap.sh does not create goes red, naming it" $?

echo
if [ "$assertions" -ne "$EXPECTED_ASSERTIONS" ]; then
  echo "FAIL  ran $assertions assertions, expected $EXPECTED_ASSERTIONS -- a case was dropped or added without updating the count"
  exit 1
fi
if [ "$failures" -gt 0 ]; then
  echo "FAIL  $failures of $assertions assertion(s) failed"
  exit 1
fi
echo "ok  $assertions/$EXPECTED_ASSERTIONS -- bootstrap.sh creates exactly the environments the workflows name"
