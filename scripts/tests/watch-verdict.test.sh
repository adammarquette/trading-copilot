#!/usr/bin/env bash
#
# watch-verdict.test.sh (gh#1028) -- proves the `verdict:watching` LIFECYCLE in scripts/watch-verdict.sh: the
# label goes up once, comes down on every ending, and stays up across the one exit that is a hand-off rather
# than an ending (a green `checks`, with the reviewer spawn on the other side of it).
#
# gh#1028's acceptance asks for the interrupt path to be PROVED, not asserted, and the first round of that PR
# shows why it has to be a gate: the review table said "TERM -> label cleared" and was right, while the process
# it had signalled was still polling. A handler that clears and RETURNS is worse than no handler -- the label
# says nobody is watching while somebody still is, and the coordinator launches the second reviewer. So every
# signal case below asserts BOTH that the label came off AND that the process is dead with the right status.
#
# Hermetic: a stub `gh` and a stub verdict-state.sh in a temp tree, no network, no real repo, no .NET. Runs
# beside the other no-SDK gates in CI.
#
# ONE TRAP FOR WHOEVER EDITS THIS. A background job started from a NON-INTERACTIVE shell has SIGINT ignored on
# entry, and bash cannot trap a signal that was ignored on entry -- so an INT case launched with a bare `&`
# proves nothing and looks like a broken trap. `set -m` below turns on job control, which gives each job its
# own process group and a normal SIGINT disposition. Do not remove it.
set -uo pipefail
set -m

REAL="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/watch-verdict.sh"
[ -r "$REAL" ] || { printf '::error::cannot find watch-verdict.sh next to this test\n' >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/scripts" "$TMP/.github/scripts" "$TMP/bin"
cp "$REAL" "$TMP/scripts/watch-verdict.sh"

# The gate's script, stubbed to whatever the case under test needs. watch-verdict.sh resolves it relative to
# ITS OWN location, which is why the copy above sits in a matching tree rather than being run in place.
cat > "$TMP/.github/scripts/verdict-state.sh" <<'VERDICT_STUB'
#!/usr/bin/env bash
printf '%s\n' "${WV_VERDICT:-NONE||0||No verdict yet}"
VERDICT_STUB
chmod +x "$TMP/.github/scripts/verdict-state.sh"

# Stub gh: logs every label mutation, serves a check set, and can be told to fail either label write.
cat > "$TMP/bin/gh" <<'GH_STUB'
#!/usr/bin/env bash
case "$1 $2" in
  "repo view")    echo "o/r"; exit 0 ;;
  "label create") exit 0 ;;
  "pr edit")
    case "$*" in
      *--add-label*)    echo ADD    >>"$WV_LOG"; exit "${WV_ADD_RC:-0}" ;;
      *--remove-label*) echo REMOVE >>"$WV_LOG"; exit "${WV_REMOVE_RC:-0}" ;;
    esac
    exit 0 ;;
  "pr checks")
    case "${WV_CHECKS:-pending}" in
      green) printf 'build\tpass\thttp://x\n';    exit 0 ;;
      red)   printf 'build\tfail\thttp://x\n';    exit 8 ;;
      *)     printf 'build\tpending\thttp://x\n'; exit 8 ;;
    esac ;;
esac
exit 0
GH_STUB
chmod +x "$TMP/bin/gh"
export PATH="$TMP/bin:$PATH"

SCRIPT="$TMP/scripts/watch-verdict.sh"
fail=0
count() { grep -c "^$1\$" "$WV_LOG" 2>/dev/null || true; }

check() { # <label> <got> <want> <why>
    if [ "$2" = "$3" ]; then printf 'ok    %s: %s\n' "$1" "$2"
    else printf '::error::FAIL  %s: got %s, want %s -- %s\n' "$1" "$2" "$3" "$4"; fail=1; fi
}

# --- the signal paths ---------------------------------------------------------------------------------
# A handler that clears the label and returns leaves an UNLABELLED wait still running, which is the exact
# state verdict:watching exists to deny. Each case asserts the process is gone, not merely relabelled.
signal_case() { # <signal> <expected status>
    local sig="$1" want="$2" pid i rc
    export WV_LOG="$TMP/log.$sig"; : > "$WV_LOG"
    WV_CHECKS=pending bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 --deadline-min 5 \
        >"$TMP/out.$sig" 2>&1 &
    pid=$!
    # Signal only once the label is actually up, so the case cannot race the startup and pass vacuously.
    for i in $(seq 1 50); do [ "$(count ADD)" -ge 1 ] && break; sleep 0.2; done
    kill -"$sig" "$pid" 2>/dev/null
    for i in $(seq 1 50); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
    if kill -0 "$pid" 2>/dev/null; then
        printf '::error::FAIL  %s: the wait SURVIVED the signal -- the label is down while it still polls\n' "$sig"
        kill -9 "$pid" 2>/dev/null; fail=1; return
    fi
    rc=0; wait "$pid" 2>/dev/null || rc=$?
    check "$sig status"  "$rc"             "$want" 'a signalled shell reports 128 + the signal number'
    check "$sig applied" "$(count ADD)"    1       'the label goes up exactly once'
    check "$sig cleared" "$(count REMOVE)" 1       'and comes down exactly once, from the one EXIT path'
}
signal_case TERM 143
signal_case INT  130
# What these three catch is a handler that CLEARS AND RETURNS -- restore `trap clear_watching EXIT INT TERM`,
# the round-1 shape, and all three red with "the wait SURVIVED the signal". What they do NOT catch is a missing
# trap: delete any one of `trap 'exit 130' INT` / `143 TERM` / `129 HUP` and its case still passes, on Git Bash
# and on Linux alike, because bash runs the EXIT trap on a fatal signal anyway and the status is 128+signum
# either way. That is not a hole -- a missing trap is harmless here, and the returning handler is the bug that
# actually shipped -- but do not read a green run as proof the traps are load-bearing. They are not.
signal_case HUP  129

# --- green checks HAND OFF, they do not finish --------------------------------------------------------
# The author spawns the reviewer between `checks` and `verdict`. Clearing here would go dark for exactly that
# step and let a coordinator launch the second reviewer -- so a green `checks` deliberately leaves it up.
export WV_LOG="$TMP/log.green"; : > "$WV_LOG"
WV_CHECKS=green bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 >"$TMP/out.green" 2>&1; rc=$?
check 'green status'  "$rc"             0 'green checks exit 0'
check 'green applied' "$(count ADD)"    1 'the label goes up'
check 'green held'    "$(count REMOVE)" 0 'and STAYS up across the reviewer spawn (the hand-off)'

# --- every other ending clears ------------------------------------------------------------------------
export WV_LOG="$TMP/log.red"; : > "$WV_LOG"
WV_CHECKS=red bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 >"$TMP/out.red" 2>&1; rc=$?
check 'red status'  "$rc"             4 'a red check exits 4'
check 'red cleared' "$(count REMOVE)" 1 'a red check ENDS the wait, so the label comes down'

export WV_LOG="$TMP/log.cr"; : > "$WV_LOG"
WV_VERDICT='CHANGES-REQUESTED|deadbeefdeadbeef|1|99|Changes were requested on deadbee' \
  bash "$SCRIPT" verdict 1 --repo o/r --poll-seconds 1 >"$TMP/out.cr" 2>&1; rc=$?
check 'kickback status'  "$rc"             1 'changes requested exits 1'
check 'kickback cleared' "$(count REMOVE)" 1 'the verdict phase clears on its way out'

# --- both label writes degrade loudly, and neither changes the exit status -----------------------------
export WV_LOG="$TMP/log.addfail"; : > "$WV_LOG"
WV_CHECKS=green WV_ADD_RC=1 bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 \
    >"$TMP/out.addfail" 2>"$TMP/err.addfail"; rc=$?
check 'add-fail status'  "$rc"             0 'an unlabelled wait still works, it is just invisible'
check 'add-fail cleared' "$(count REMOVE)" 0 'never applied, so never removed'
if grep -q 'could not apply' "$TMP/err.addfail"; then printf 'ok    add-fail warns on stderr\n'
else printf '::error::FAIL  add-fail must warn on stderr -- a silent invisible wait is undiagnosable\n'; fail=1; fi

# The hand-off's loose end: `verdict` failing to re-apply must still try to take down whatever `checks` handed
# on, or a label this process never applied sits for its whole shelf life with nothing left to clear it.
export WV_LOG="$TMP/log.inherit"; : > "$WV_LOG"
WV_ADD_RC=1 WV_VERDICT='CHANGES-REQUESTED|deadbeefdeadbeef|1|99|Changes were requested on deadbee' \
  bash "$SCRIPT" verdict 1 --repo o/r --poll-seconds 1 >"$TMP/out.inherit" 2>"$TMP/err.inherit"; rc=$?
check 'inherited status'  "$rc"             1 'the wait still rules normally without a label of its own'
check 'inherited cleared' "$(count REMOVE)" 1 'and still tries to clear one an earlier phase may have left'
if grep -q 'handed one on' "$TMP/err.inherit"; then printf 'ok    inherited case says which failure it is\n'
else printf '::error::FAIL  the verdict-phase add failure must not claim the wait is merely invisible\n'; fail=1; fi

export WV_LOG="$TMP/log.rmfail"; : > "$WV_LOG"
WV_CHECKS=red WV_REMOVE_RC=1 bash "$SCRIPT" checks 1 --repo o/r --poll-seconds 1 \
    >"$TMP/out.rmfail" 2>"$TMP/err.rmfail"; rc=$?
check 'remove-fail status' "$rc" 4 'the EXIT trap must not overwrite the status the caller reads'
if grep -q 'could not remove' "$TMP/err.rmfail"; then printf 'ok    remove-fail warns on stderr\n'
else printf '::error::FAIL  remove-fail must warn -- a stuck label suppresses reviewers with no trace\n'; fail=1; fi

if [ "$fail" -ne 0 ]; then
    printf '\nFAIL: the verdict:watching lifecycle is not what gh#1028 requires.\n' >&2
    exit 1
fi
printf '\nOK: the label goes up once, survives the reviewer spawn, and comes down on every ending.\n'
