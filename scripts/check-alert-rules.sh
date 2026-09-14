#!/usr/bin/env bash
#
# Lints the Prometheus alerting rules and runs their promtool tests -- the gate #585 adds so alert-rule drift and
# missing coverage FAIL THE PR instead of being caught only when someone runs promtool by hand.
#
# WHY THIS EXISTS (gh#585)
# -----------------------
# observability/rules/tests/*.yml is an executable promtool suite, but nothing ran it. Two failures shipped past
# that gap, both found while doing gh#537:
#   * a rule can ship with no test at all -- three rules, two on the R-13 flatten path, had no assertion (gh#537);
#   * a test can silently drift from its rule -- gh#482 rewrote EventLogRetentionGap's description but never the
#     test's expectation, so the suite had been red against the live rules file for however long and nobody knew.
# An untested, ungated alert suite is the same silent-monitor failure class ADR-0019 exists to close, one layer
# up: the thing that is supposed to catch a broken alert is itself unchecked.
#
# HOW IT DECIDES
# --------------
# `promtool check rules`  -- syntax / lint of the rule files.
# `promtool test rules`   -- the unit tests over PromQL, which also catch a rule whose metric/label does not match
#                            what the app emits (it produces no series, fires nothing, and looks healthy).
#
# promtool is run from the PINNED prom/prometheus image (the SAME version docker-compose runs and the deployment
# runbook documents), so this one command is IDENTICAL locally and in CI -- the platform contract's rule that a
# local check disagreeing with CI is worse than no local check. promtool is deliberately NOT run via `go install`
# (Prometheus's replace directives break it); the pinned image is the supported path.
#
# The rules glob is ONE level deep on purpose: observability/rules/*.yml are RULES; observability/rules/tests/*.yml
# are the TESTS over them, not rules -- so `check rules` must not descend into tests/ (prometheus.yml documents the
# same one-level glob).

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

# Docker is required -- promtool runs from the pinned image below. Guard explicitly (like the sibling
# check-env-forwarding.sh) so "Docker is not installed / not running" reports itself honestly instead of
# masquerading as a malformed rule or a failed test further down (gh#585 review).
if ! command -v docker >/dev/null 2>&1; then
    echo "check-alert-rules: docker is required to run promtool from the pinned image -- install Docker and start the daemon." >&2
    exit 2
fi

# Pin to the version docker-compose.yml runs (prom/prometheus:v3.0.1) so the tests run against the same promtool
# that will evaluate the rules in production. Bump this and the compose image together.
PROM_IMAGE="prom/prometheus:v3.0.1"
OBS="$PWD/observability"

# Enumerate rule and test files as BASENAMES -- that is what promtool sees inside the container's working dir. Bash
# builtins only (no find/ls parsing); nullglob makes an empty array rather than a literal pattern when nothing matches.
shopt -s nullglob
rule_paths=("$OBS"/rules/*.yml)
test_paths=("$OBS"/rules/tests/*.yml)
shopt -u nullglob
rule_files=("${rule_paths[@]##*/}")
test_files=("${test_paths[@]##*/}")

if [ "${#rule_files[@]}" -eq 0 ]; then
    echo "FAIL: no rule files found under observability/rules/ -- nothing to check."
    exit 1
fi
if [ "${#test_files[@]}" -eq 0 ]; then
    echo "FAIL: no test files found under observability/rules/tests/ -- a rules file with no promtool tests is exactly the gap gh#585 closes."
    exit 1
fi

# Obtain the pinned image ONCE, up front, with a short retry. An anonymous Docker Hub pull on a shared CI-runner IP
# can hit a transient 429 (rate limit) that has nothing to do with this PR (gh#585 review); the retry rides out the
# common blip, and pulling here -- rather than letting the `docker run` below pull -- means an image/registry
# problem reports itself as one instead of as a rule/test failure. (Docker Hub AUTH, a free-tier token, is the
# fuller fix if 429s recur, but it needs a repo secret, so it stays a conscious later call, not an unprompted one.)
pulled=""
for attempt in 1 2 3; do
    if docker pull --quiet "$PROM_IMAGE" >/dev/null; then pulled=1; break; fi
    echo "check-alert-rules: pull of $PROM_IMAGE failed (attempt $attempt/3); retrying in $((attempt * 5))s..." >&2
    sleep $((attempt * 5))
done
if [ -z "$pulled" ]; then
    echo "check-alert-rules: could not obtain $PROM_IMAGE after 3 attempts -- is Docker running and Docker Hub reachable? (an anonymous pull may be rate-limited: 429 / toomanyrequests)" >&2
    exit 2
fi

# `check rules` from /obs/rules so the rule basenames resolve; `test rules` from /obs/rules/tests so each test's
# `rule_files: ../trading-alerts.yml` resolves relative to the test file (as the runbook's manual command does).
# `--pull never`: the image is already present (pulled above), so a non-zero exit here is promtool's verdict on the
# rules/tests, not a registry problem -- which is what makes the two failure messages below honest.
echo "==> promtool check rules  (${rule_files[*]})"
if ! docker run --rm --pull never -v "$OBS:/obs" -w /obs/rules --entrypoint promtool "$PROM_IMAGE" check rules "${rule_files[@]}"; then
    echo "FAIL: promtool reported a malformed rule file (check rules). Fix the rule before merge (gh#585)."
    exit 1
fi

echo "==> promtool test rules   (${test_files[*]})"
if ! docker run --rm --pull never -v "$OBS:/obs" -w /obs/rules/tests --entrypoint promtool "$PROM_IMAGE" test rules "${test_files[@]}"; then
    echo "FAIL: promtool reported an alert-rule test failure -- a rule regressed or a test drifted from its rule (gh#585)."
    exit 1
fi

echo "OK: alert rules lint clean and all promtool tests pass."
