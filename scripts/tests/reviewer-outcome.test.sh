#!/usr/bin/env bash
#
# reviewer-outcome.test.sh (gh#994) -- proves scripts/lib/reviewer-outcome.sh degrades an Anthropic
# API / provisioning failure the PR author cannot fix (the real one #994 was filed for: a provisioned
# ANTHROPIC_API_KEY whose account had NO CREDIT -> HTTP 400 "Credit balance is too low", on EVERY PR)
# to a clean `infra` skip, while a genuine reviewer failure still classifies `fail` and reddens.
#
# The infra fixture is the VERBATIM result JSON `claude ... --output-format json` wrote on every PR while
# the key's account had no credit (the failing reviewer run on PR #997). Asserting it maps to `infra` is
# the prove-the-red: pre-fix, reviewer.yml treated any rc!=0 as a hard failure and reddened that exact
# case on every PR -- exactly the "red check nobody can fix" its own header warns trains people to ignore
# reds. The `fail` assertions are the not-weakened control: a real reviewer bug must still redden.
#
# Hermetic -- no network, no gh, no claude, no .NET; jq only (already required by reviewer.yml). Runs
# beside the other no-build shell gates in ci.yml.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/reviewer-outcome.sh
source "$DIR/../lib/reviewer-outcome.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# The EXACT JSON the CLI wrote on every PR while the provisioned key's account had no credit
# (gh#994 evidence, the reviewer run on PR #997).
cat > "$TMP/credit.json" <<'JSON'
{"is_error":true,"duration_api_ms":0,"num_turns":1,"stop_reason":"stop_sequence","total_cost_usd":0,"terminal_reason":"api_error","subtype":"success","api_error_status":400,"result":"Credit balance is too low","type":"result","duration_ms":823}
JSON

# A rate-limit / overloaded shape -- the same not-the-diff class, a different status, so the classifier is
# not overfit to 400 / the credit message.
cat > "$TMP/ratelimit.json" <<'JSON'
{"is_error":true,"terminal_reason":"api_error","api_error_status":429,"result":"rate limit exceeded","type":"result"}
JSON

# A genuine reviewer failure that is NOT an API error -- must still redden so a real regression shows.
cat > "$TMP/exec-error.json" <<'JSON'
{"is_error":true,"terminal_reason":"error_during_execution","result":"the tool loop failed","type":"result"}
JSON

# A normal, successful review (rc 0) -- the path once the account is funded.
cat > "$TMP/ok.json" <<'JSON'
{"is_error":false,"terminal_reason":"end_turn","result":"## Review\n\nLooks good.","type":"result"}
JSON

# The CLI crashed before writing any structured result -- an empty file. A genuine failure -> fail.
: > "$TMP/empty.json"

fail=0
assert_eq() { # <desc> <expected> <actual>
    if [ "$2" = "$3" ]; then printf 'ok    %s\n' "$1"
    else printf '::error::FAIL  %s\n      expected: %s\n      actual:   %s\n' "$1" "$2" "$3"; fail=1; fi
}

# THE FIX (prove-the-red): the exact production failure -- provisioned key, no credit -- degrades to a
# clean skip, not the red-on-every-PR of gh#994.
assert_eq "no-credit 400 (the gh#994 failure) classifies infra -> clean skip" \
    infra "$(classify_review_outcome 1 "$TMP/credit.json")"

# Not overfit: any API-layer refusal the author cannot fix is infra.
assert_eq "rate-limit 429 classifies infra" \
    infra "$(classify_review_outcome 1 "$TMP/ratelimit.json")"

# NOT WEAKENED: a real reviewer failure that is not an API error still reddens.
assert_eq "non-API execution error classifies fail -> reddens" \
    fail "$(classify_review_outcome 1 "$TMP/exec-error.json")"

# NOT WEAKENED: a crash that produced no parseable result still reddens.
assert_eq "empty/garbage result (CLI crash) classifies fail -> reddens" \
    fail "$(classify_review_outcome 1 "$TMP/empty.json")"

# The funded path: rc 0 is ok regardless of the body.
assert_eq "successful run (rc 0) classifies ok" \
    ok "$(classify_review_outcome 0 "$TMP/ok.json")"

# Defensive: a non-zero rc with no api_error signal is still a failure, even if the body parses.
assert_eq "rc!=0 with no api_error signal classifies fail" \
    fail "$(classify_review_outcome 1 "$TMP/ok.json")"

if [ "$fail" -ne 0 ]; then
    printf '\nFAIL: reviewer-outcome did not classify the reviewer CLI outcomes as gh#994 requires.\n' >&2
    exit 1
fi
printf '\nOK: an Anthropic API/provisioning failure degrades to a clean skip; a genuine reviewer failure still reddens.\n'
