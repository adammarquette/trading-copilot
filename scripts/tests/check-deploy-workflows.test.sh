#!/usr/bin/env bash
#
# check-deploy-workflows.test.sh (gh#1232) -- proves scripts/check-deploy-workflows.sh goes RED for each way the
# release workflow can be wrong, and stays GREEN on the real one.
#
# A gate asserted in a comment is not a gate. check-deploy-workflows.sh is only worth having if it fails for the
# reason each assertion exists, so this runs it against the real release.yml (must pass) and against deliberately
# broken copies (each must fail). The checker takes the workflows directory as an argument precisely so this can.
#
# The broken copies are the not-weakened control: if someone deletes an assertion to make the checker green, the
# matching case here turns red. Hermetic -- no network, no gh, no .NET.
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHECKER="${CHECKER:-$DIR/../check-deploy-workflows.sh}"
REAL="${REAL_WORKFLOWS:-$DIR/../../.github/workflows}"

# A new suite declares how many assertions it expects, so a dropped case cannot stay green (platform contract).
EXPECTED_ASSERTIONS=12
assertions=0
failures=0

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

check() { # <description> <condition-result: 0 = holds>
  assertions=$((assertions + 1))
  if [ "$2" -eq 0 ]; then printf 'ok    %s\n' "$1"
  else printf '::error::FAIL  %s\n' "$1"; failures=$((failures + 1)); fi
}

# A workflows directory holding a copy of the real release.yml with a sed script applied ('' = unchanged).
variant() { # <name> <sed-script>
  mkdir -p "$TMP/$1"
  if [ -n "$2" ]; then sed "$2" "$REAL/release.yml" > "$TMP/$1/release.yml"
  else cp "$REAL/release.yml" "$TMP/$1/release.yml"; fi
  printf '%s\n' "$TMP/$1"
}

# A variant with lines APPENDED. release.yml's last job is `publish`, so an appended step belongs to it.
variant_append() { # <name> <text>
  mkdir -p "$TMP/$1"
  cp "$REAL/release.yml" "$TMP/$1/release.yml"
  printf '%s\n' "$2" >> "$TMP/$1/release.yml"
  printf '%s\n' "$TMP/$1"
}

run_checker() { bash "$CHECKER" "$1" >"$TMP/out.txt" 2>&1; }

# 1. The real workflow passes. Without this, "everything fails" would satisfy every red case below.
run_checker "$REAL"; rc=$?
check "the real release.yml passes the checker (rc=$rc)" "$rc"

# 2. publish must wait for the human approval.
d="$(variant publish-not-gated '/^  publish:/,$ s/^    needs: gate$/    needs: verify-gate/')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that skips the approval is refused" "$?"

# 3. The approval environment must be a literal name, not an expression.
d="$(variant gate-expression 's/^    environment: production$/    environment: ${{ inputs.target }}/')"
run_checker "$d"; [ $? -ne 0 ]; check "an expression-named approval environment is refused" "$?"

# 4. The approval gate must name an environment at all.
d="$(variant gate-no-environment '/^    environment: production$/d')"
run_checker "$d"; [ $? -ne 0 ]; check "a gate job with no environment (an inert gate) is refused" "$?"

# 5. publish must not reference :latest.
d="$(variant_append publish-latest '      - run: docker pull ghcr.io/example/app:latest')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that references :latest is refused" "$?"

# 6. publish must retag, not rebuild.
d="$(variant_append publish-rebuild '      - run: docker build -t example/app .')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that rebuilds instead of retagging is refused" "$?"

# 7. publish must expose the digest a deploy would pin.
d="$(variant publish-no-digest '/^      digest: /d')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that exposes no digest is refused" "$?"

# 8. A missing workflow file is a failure, not a pass.
mkdir -p "$TMP/empty"
run_checker "$TMP/empty"; [ $? -ne 0 ]; check "a workflows directory with no release.yml is refused" "$?"

# 9. The approval gate must wait for the check that the approval environment is real (an inert-gate check that runs
#    after the approval it vouches for cannot vouch for it).
d="$(variant gate-unordered '/^  gate:/,/^  publish:/ { /^    needs: verify-gate$/d }')"
run_checker "$d"; [ $? -ne 0 ]; check "a gate job that does not wait for verify-gate is refused" "$?"

# 10. publish must not rebuild through an action either.
d="$(variant_append publish-action '      - uses: docker/build-push-action@v6')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that rebuilds through build-push-action is refused" "$?"

# 11. publish must actually retag.
d="$(variant publish-no-retag 's/imagetools create/imagetools inspect/')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that never retags is refused" "$?"

# 12. publish must expose the version a deploy would pin, as well as the digest.
d="$(variant publish-no-version '/^      version: /d')"
run_checker "$d"; [ $? -ne 0 ]; check "a publish job that exposes no version is refused" "$?"

echo "----"
[ "$assertions" -eq "$EXPECTED_ASSERTIONS" ] || { echo "::error::ran $assertions assertions, expected $EXPECTED_ASSERTIONS"; exit 1; }
[ "$failures" -eq 0 ] || { echo "::error::$failures of $assertions assertions failed"; exit 1; }
echo "OK: all $assertions assertions passed."
