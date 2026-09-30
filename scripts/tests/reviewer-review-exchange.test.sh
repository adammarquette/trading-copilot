#!/usr/bin/env bash
#
# reviewer-review-exchange.test.sh (gh#1225) -- proves .github/scripts/reviewer-review.sh can trade its App JWT for
# an installation token INSIDE GitHub Actions, where `gh` refuses to run at all without GH_TOKEN.
#
# The bug: the exchange is `gh api -H "Authorization: Bearer <jwt>" -X POST /app/installations/<id>/access_tokens`,
# and it ran without GH_TOKEN. Locally `gh` has stored auth, so it worked; on a runner `gh` printed "To use GitHub
# CLI in a GitHub Actions workflow, set the GH_TOKEN environment variable" and the script blamed the App
# configuration. It stayed hidden because the step was skipped until the model account had credit (gh#994).
#
# The stub `gh` below reproduces that guard exactly, so this test is red against the unfixed script (proved in the
# PR) and green once the exchange call carries a GH_TOKEN. The two failure-mode assertions are the not-weakened
# control: an exchange that genuinely fails must still fail closed and post nothing.
#
# Hermetic: no network, no real gh, no secrets. It needs openssl (to sign a throwaway JWT), which the runner and
# Git Bash both have.
set -uo pipefail   # no -e: every assertion must run and report, not stop at the first red

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$DIR/../../.github/scripts/reviewer-review.sh"

# A new suite declares how many assertions it expects, so a dropped case cannot stay green (platform contract).
EXPECTED_ASSERTIONS=6
assertions=0
failures=0

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/bin" "$TMP/home"

openssl genrsa -out "$TMP/app.pem" 2048 >/dev/null 2>&1
printf 'a review body\n' > "$TMP/body.md"

cat > "$TMP/bin/gh" <<'STUB'
#!/usr/bin/env bash
# Stub gh. Reproduces the Actions guard, then answers the two calls reviewer-review.sh makes.
if [ "${GITHUB_ACTIONS:-}" = "true" ] && [ -z "${GH_TOKEN:-}" ]; then
  echo "gh: To use GitHub CLI in a GitHub Actions workflow, set the GH_TOKEN environment variable." >&2
  exit 4
fi
args="$*"
echo "$args" >> "$STUB_LOG"
case "$args" in
  *"/access_tokens"*)
    [ "${STUB_FAIL_EXCHANGE:-}" = "1" ] && { echo "HTTP 401: A JSON web token could not be decoded" >&2; exit 1; }
    case "$args" in
      *"Authorization: Bearer "*) echo "installation-token-xyz" ;;
      *) echo "the exchange must authenticate with the App JWT, not GH_TOKEN" >&2; exit 5 ;;
    esac ;;
  *"/pulls/"*"/reviews"*)
    [ "${GH_TOKEN:-}" = "installation-token-xyz" ] || { echo "reviews must use the installation token" >&2; exit 6; }
    echo "review 42 submitted as trading-copilot-reviewer[bot] — state COMMENTED" ;;
  *) echo "unexpected gh call: $args" >&2; exit 7 ;;
esac
STUB
chmod +x "$TMP/bin/gh"

check() { # <description> <condition-result: 0 = holds>
  assertions=$((assertions + 1))
  if [ "$2" -eq 0 ]; then printf 'ok    %s\n' "$1"
  else printf '::error::FAIL  %s\n' "$1"; failures=$((failures + 1)); fi
}

run_script() { # env assignments are passed in by the caller via `env`
  "$@" bash "$SCRIPT" review 7 COMMENT "$TMP/body.md"
}

base=(env -u GH_TOKEN -u GITHUB_TOKEN PATH="$TMP/bin:$PATH" HOME="$TMP/home" GH_CONFIG_DIR="$TMP/home"
      STUB_LOG="$TMP/calls.log" REVIEWER_APP_ID=123 REVIEWER_APP_INSTALLATION_ID=456
      REVIEWER_APP_PRIVATE_KEY_FILE="$TMP/app.pem" REVIEWER_REPO=example/repo)

# 1-2. Inside Actions: GITHUB_ACTIONS=true, no stored auth, no GH_TOKEN -- the exact CI environment.
: > "$TMP/calls.log"
rc=0; out="$(run_script "${base[@]}" GITHUB_ACTIONS=true 2>&1)" || rc=$?
check "in Actions the exchange succeeds and the script exits 0 (rc=$rc)" "$rc"
grep -q "submitted as trading-copilot-reviewer\[bot\]" <<<"$out"; check "in Actions the review is posted as the bot" "$?"

# 3. Outside Actions (a local run with stored auth) must keep working -- the fix must not break the local path.
: > "$TMP/calls.log"
rc=0; out="$(run_script "${base[@]}" 2>&1)" || rc=$?
check "outside Actions the script still exits 0 (rc=$rc)" "$rc"

# 4-5. Not-weakened control: an exchange that genuinely fails must fail closed and post nothing.
: > "$TMP/calls.log"
rc=0; out="$(run_script "${base[@]}" GITHUB_ACTIONS=true STUB_FAIL_EXCHANGE=1 2>&1)" || rc=$?
[ "$rc" -ne 0 ]; check "a failed exchange exits non-zero (rc=$rc)" "$?"
! grep -q "/reviews" "$TMP/calls.log"; check "a failed exchange posts no review" "$?"

# 6. The failure message must not send the operator to the wrong place: it should name the GH_TOKEN guard as a
#    possible cause, not only the App configuration. Run OUTSIDE Actions so the stub's own guard text (which contains
#    "GH_TOKEN") cannot satisfy this: the only source of that word left is the script's message.
: > "$TMP/calls.log"
rc=0; out="$(run_script "${base[@]}" STUB_FAIL_EXCHANGE=1 2>&1)" || rc=$?
grep -qi "GH_TOKEN" <<<"$out"; check "the exchange-failure message names GH_TOKEN as a possible cause" "$?"

echo "----"
[ "$assertions" -eq "$EXPECTED_ASSERTIONS" ] || { echo "::error::ran $assertions assertions, expected $EXPECTED_ASSERTIONS"; exit 1; }
[ "$failures" -eq 0 ] || { echo "::error::$failures of $assertions assertions failed"; exit 1; }
echo "OK: all $assertions assertions passed."
