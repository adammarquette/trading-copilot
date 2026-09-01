#!/usr/bin/env bash
#
# watch-verdict-check-read.test.sh (gh#1040) -- proves `read_checks()` in scripts/watch-verdict.sh no longer
# lets a GraphQL read failure masquerade as "zero checks."
#
# The bug: `gh pr checks` (GraphQL) swallowed its own failure under `2>/dev/null || true`. When GraphQL was
# rate-limited it produced no output, the loop body never ran, and `pending_checks` / `passed_checks` both
# stayed 0 -- indistinguishable from a genuinely check-less PR. On an APPROVED PR that read as "Approved ...
# waiting on 0 check(s)" for the full 45-minute deadline, self-contradicting and silent (#1036).
#
# The fix has two parts this file exercises separately:
#   1. a GraphQL read failure now falls back to REST (`gh api .../check-runs`), which the issue's own field
#      notes found stayed healthy throughout the outage -- so a rate-limited GraphQL call no longer stalls a
#      PR that is actually green.
#   2. when BOTH GraphQL and the REST fallback fail to produce a readable answer, that state is now tracked
#      explicitly (`checks_readable`) rather than defaulting to "no checks yet," and the wait gives up loudly
#      after a bounded number of attempts instead of running out the full deadline blind.
#
# Hermetic: a stub `gh` and a stub verdict-state.sh in a temp tree, no network, no real repo, no .NET. Runs
# beside the other no-SDK gates in CI, same pattern as scripts/tests/watch-verdict.test.sh (gh#1028).
set -uo pipefail

REAL="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/watch-verdict.sh"
[ -r "$REAL" ] || { printf '::error::cannot find watch-verdict.sh next to this test\n' >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/scripts" "$TMP/.github/scripts" "$TMP/bin"
cp "$REAL" "$TMP/scripts/watch-verdict.sh"

cat > "$TMP/.github/scripts/verdict-state.sh" <<'VERDICT_STUB'
#!/usr/bin/env bash
printf '%s\n' "${WV_VERDICT:-NONE||0||No verdict yet}"
VERDICT_STUB
chmod +x "$TMP/.github/scripts/verdict-state.sh"

# Stub gh:
#   - `pr checks`   simulates GraphQL. WV_CHECKS=ratelimited prints NOTHING and exits 1, the shape a rate
#     limit actually takes (no output, non-zero exit) -- distinct from `pending`/`red`, which print output.
#   - `api .../pulls/<n>`                 simulates the REST head-sha lookup the fallback needs first.
#   - `api .../commits/<sha>/check-runs`  simulates the Checks API half of the REST fallback.
#   - `api .../commits/<sha>/status`      simulates the CLASSIC COMMIT STATUS half -- a separate set
#     GraphQL's statusCheckRollup merges in that the Checks API alone never returns (review finding 1 on
#     PR #1046: a fallback reading only check-runs can call a still-red/still-pending legacy status "green").
# Any of the three REST calls can be told to fail via WV_REST_SHA_FAIL / WV_REST_CHECKS=unreadable /
# WV_REST_STATUS=unreadable, so a test can drive "GraphQL down, REST also down" without touching a network.
cat > "$TMP/bin/gh" <<'GH_STUB'
#!/usr/bin/env bash
case "$1 $2" in
  "repo view")    echo "o/r"; exit 0 ;;
  "label create") exit 0 ;;
  "pr edit")      exit 0 ;;
  "pr checks")
    case "${WV_CHECKS:-pending}" in
      green)       printf 'build\tpass\thttp://x\n';    exit 0 ;;
      red)         printf 'build\tfail\thttp://x\n';    exit 8 ;;
      # Empty stdout, non-zero exit, nothing on stderr worth matching -- deliberately the SAME shape a real
      # "no checks reported yet" takes. The fix must not tell these apart by message text; it disambiguates
      # by asking REST, which is exactly what cases 3 and 4 below are testing from two different answers.
      ratelimited) printf 'GraphQL: API rate limit exceeded for user ID (ratelimit)\n' >&2; exit 1 ;;
      none)        exit 1 ;;
      *)           printf 'build\tpending\thttp://x\n'; exit 8 ;;
    esac ;;
esac
case "$1" in
  api)
    case "$2" in
      repos/o/r/pulls/*)
        [ "${WV_REST_SHA_FAIL:-0}" = "1" ] && exit 1
        echo "deadbeefcafef00dfeedfacecafebeef"
        exit 0 ;;
      repos/o/r/commits/*/check-runs)
        case "${WV_REST_CHECKS:-unreadable}" in
          green)      printf 'build\tcompleted\tsuccess\n'; exit 0 ;;
          red)        printf 'build\tcompleted\tfailure\n'; exit 0 ;;
          pending)    printf 'build\tin_progress\t\n';       exit 0 ;;
          none)       exit 0 ;;
          unreadable) exit 1 ;;
        esac ;;
      repos/o/r/commits/*/status)
        case "${WV_REST_STATUS:-none}" in
          none)       exit 0 ;;
          red)        printf 'legacy-ci\tfailure\n'; exit 0 ;;
          pending)    printf 'legacy-ci\tpending\n';  exit 0 ;;
          unreadable) exit 1 ;;
        esac ;;
    esac ;;
esac
exit 0
GH_STUB
chmod +x "$TMP/bin/gh"
export PATH="$TMP/bin:$PATH"

SCRIPT="$TMP/scripts/watch-verdict.sh"
fail=0
check() { # <label> <got> <want> <why>
    if [ "$2" = "$3" ]; then printf 'ok    %s: %s\n' "$1" "$2"
    else printf '::error::FAIL  %s: got %s, want %s -- %s\n' "$1" "$2" "$3" "$4"; fail=1; fi
}

# --- 1. GraphQL rate-limited but REST reports green: `checks` phase must still conclude green -------------
# This is the direct fix for the reported defect's `checks`-phase shape: a rate-limited GraphQL call must not
# read as "no checks reported yet" when the PR genuinely has green checks -- REST proves it.
out="$TMP/out.checks-fallback"
WV_CHECKS=ratelimited WV_REST_CHECKS=green \
  timeout 15 bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 --deadline-min 5 >"$out" 2>&1
rc=$?
check 'checks-fallback status' "$rc" 0 'REST fallback finds the checks green even though GraphQL failed'
if grep -q 'check(s) green' "$out"; then printf 'ok    checks-fallback reports green via REST\n'
else printf '::error::FAIL  checks-fallback: expected a green report, got:\n%s\n' "$(cat "$out")"; fail=1; fi

# --- 2. THE REPORTED BUG, reproduced directly: an APPROVED PR, GraphQL rate-limited, REST green ------------
# Before the fix this hangs (or times out) printing "Approved ... waiting on 0 check(s)" -- self-contradicting,
# because passed_checks and pending_checks were both 0 from a read failure, not from an empty check set.
out="$TMP/out.verdict-fallback"
WV_VERDICT='APPROVED|deadbeefcafef00dfeedfacecafebeef|1|0|Approved at head deadbee' \
  WV_CHECKS=ratelimited WV_REST_CHECKS=green \
  timeout 15 bash "$SCRIPT" verdict 1 --repo o/r --poll-seconds 1 --deadline-min 5 >"$out" 2>&1
rc=$?
check 'approved-fallback status' "$rc" 0 'approved + green-via-REST must exit 0, not stall on a GraphQL outage'
if grep -qi 'waiting on 0 check' "$out"; then
  printf '::error::FAIL  approved-fallback: still prints the self-contradicting "waiting on 0 check(s)"\n'
  fail=1
fi

# --- 3. GraphQL AND REST both unreadable: must bail out LOUD and BOUNDED, never as "no checks reported" ----
# Point 2 of the fix: an unreadable state must never render as a legitimate empty-checks state, and a wait
# that cannot read checks at all must not silently run out its full deadline -- it should give up quickly and
# say so, so an agent can act instead of waiting 45 minutes on an API problem.
out="$TMP/out.both-down"
start=$(date +%s)
WV_CHECKS=ratelimited WV_REST_SHA_FAIL=1 \
  timeout 30 bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 --deadline-min 10 --max-unreadable 2 \
  >"$out" 2>&1
rc=$?
elapsed=$(( $(date +%s) - start ))
check 'both-down status' "$rc" 2 'an unreadable check state is reported as a hard stop (exit 2), not a timeout at 10m'
# A generous margin, not a tight SLA -- see the matching comment on the verdict-phase case below.
if [ "$elapsed" -lt 60 ]; then printf 'ok    both-down bails out in %ss, not the full 10m deadline\n' "$elapsed"
else printf '::error::FAIL  both-down: took %ss -- did not bail out early on repeated read failures\n' "$elapsed"; fail=1; fi
if grep -qi 'could not read check state' "$out"; then printf 'ok    both-down names the failure explicitly\n'
else printf '::error::FAIL  both-down: must say it could not READ checks, not imply there are none:\n%s\n' "$(cat "$out")"; fail=1; fi
if grep -qi 'no checks reported yet' "$out"; then
  printf '::error::FAIL  both-down: must never render an unreadable state as "no checks reported yet"\n'
  fail=1
fi

# --- 4. a genuinely check-less PR (both sources agree: empty) is UNCHANGED -- still waits, not a false bail -
# Guards against over-correcting: an early PR with no checks yet must still read as "no checks reported yet"
# and keep polling past --max-unreadable's count, not get misclassified as an unreadable state and bailed out
# early. Run it live (a real deadline would take real minutes) and kill it after several polls have gone by.
out="$TMP/out.none-yet"
WV_CHECKS=none WV_REST_CHECKS=none \
  bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 --deadline-min 10 --max-unreadable 2 \
  >"$out" 2>&1 &
pid=$!
for i in $(seq 1 50); do
  [ "$(grep -c 'no checks reported yet' "$out" 2>/dev/null || true)" -ge 4 ] && break
  sleep 0.2
done
still_running=1
kill -0 "$pid" 2>/dev/null || still_running=0
kill -TERM "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
check 'none-yet still running' "$still_running" 1 'must keep polling well past --max-unreadable, not bail out'
if grep -qi 'no checks reported yet' "$out"; then printf 'ok    none-yet reads as the legitimate empty state\n'
else printf '::error::FAIL  none-yet: expected "no checks reported yet" somewhere in:\n%s\n' "$(cat "$out")"; fail=1; fi
if grep -qi 'could not read check state' "$out"; then
  printf '::error::FAIL  none-yet: a genuinely empty check set must not be reported as a read failure\n'
  fail=1
fi

# --- 5. REST must read classic commit statuses too, not just the Checks API (review finding 1) -------------
# The Checks API and the legacy `POST .../statuses` API are TWO SEPARATE sets GraphQL's statusCheckRollup
# merges. A fallback that reads only check-runs silently narrows to a partial set, and "I read a partial set"
# must never render as "everything passed" -- that would wave a still-red or still-pending PR through, worse
# than the timeout this PR set out to fix.
out="$TMP/out.legacy-status-red"
WV_CHECKS=ratelimited WV_REST_CHECKS=green WV_REST_STATUS=red \
  timeout 15 bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 --deadline-min 5 >"$out" 2>&1
rc=$?
check 'legacy-status-red status' "$rc" 4 'a red legacy commit status must fail the read same as a red check-run'
if grep -q 'legacy-ci' "$out"; then printf 'ok    legacy-status-red names the legacy status context\n'
else printf '::error::FAIL  legacy-status-red: expected the legacy context named in the failure, got:\n%s\n' "$(cat "$out")"; fail=1; fi
if grep -q 'check(s) green' "$out"; then
  printf '::error::FAIL  legacy-status-red: reported green while a legacy commit status is still failing\n'
  fail=1
fi

out="$TMP/out.legacy-status-pending"
WV_CHECKS=ratelimited WV_REST_CHECKS=green WV_REST_STATUS=pending \
  timeout 6 bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 --deadline-min 5 >"$out" 2>&1
rc=$?
check 'legacy-status-pending status' "$rc" 124 'keeps waiting -- a still-pending legacy status must not resolve to green'
if grep -q 'check(s) green' "$out"; then
  printf '::error::FAIL  legacy-status-pending: reported green while a legacy status is still pending\n'
  fail=1
else printf 'ok    legacy-status-pending: never falsely reports green\n'
fi

# --- 6. `verdict`'s OWN unreadable-bailout: APPROVED, but GraphQL AND REST both fail (review finding 2) -----
# Case 3 above exercises the `checks` command's bailout branch; `verdict` has a SEPARATE branch with its own
# message construction and its own exit path for the identical shape -- an approved PR whose checks cannot be
# read by either source. It must bail out loud and bounded too, never render as "waiting on 0 check(s)."
out="$TMP/out.verdict-both-down"
start=$(date +%s)
WV_VERDICT='APPROVED|deadbeefcafef00dfeedfacecafebeef|1|0|Approved at head deadbee' \
  WV_CHECKS=ratelimited WV_REST_SHA_FAIL=1 \
  timeout 30 bash "$SCRIPT" verdict 1 --repo o/r --poll-seconds 1 --deadline-min 10 --max-unreadable 2 \
  >"$out" 2>&1
rc=$?
elapsed=$(( $(date +%s) - start ))
check 'verdict-both-down status' "$rc" 2 'verdict phase must also bail out (exit 2), not wait the full 10m deadline'
# A generous margin, not a tight SLA -- the point is "well short of 10 real minutes," and CI/dev-box load can
# stretch a couple of 1s polls plus stub-gh process spawns further than a quiet machine would.
if [ "$elapsed" -lt 60 ]; then printf 'ok    verdict-both-down bails out in %ss, not the full 10m deadline\n' "$elapsed"
else printf '::error::FAIL  verdict-both-down: took %ss -- did not bail out early on repeated read failures\n' "$elapsed"; fail=1; fi
if grep -qi 'could not read check state' "$out"; then printf 'ok    verdict-both-down names the failure explicitly\n'
else printf '::error::FAIL  verdict-both-down: must say it could not READ checks:\n%s\n' "$(cat "$out")"; fail=1; fi
if grep -qi 'waiting on 0 check' "$out"; then
  printf '::error::FAIL  verdict-both-down: must never print the self-contradicting "waiting on 0 check(s)"\n'
  fail=1
fi

if [ "$fail" -ne 0 ]; then
    printf '\nFAIL: read_checks() does not yet distinguish "could not read" from "no checks" (gh#1040).\n' >&2
    exit 1
fi
printf '\nOK: a GraphQL read failure falls back to REST, and an unreadable state never passes as empty.\n'
