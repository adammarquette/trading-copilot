#!/usr/bin/env bash
#
# Fails when the integration-test inventory and the suites on disk disagree (gh#862).
#
# WHY THIS EXISTS
# ---------------
# documentation/integration-test-audit.md §2 calls itself a "living inventory -- one row per suite". It is the
# map an agent reads to answer "is this path covered?", and a map that silently omits a quarter of the territory
# answers that question WRONG -- it reports an uncovered safety path as unwritten when the suite exists, or leaves
# a real gap invisible because nobody notices the row was never added.
#
# It has drifted twice. A reconciliation under gh#490 (2026-07-30) added five shipped suites the inventory had
# missed; by the 2026-08-14 audit it was 22 short again, and by the time gh#862 was picked up two days later, 27.
# A third manual pass buys another fortnight. The ADR index next door does not drift, and the difference is not
# diligence -- it is that gh#600 fails CI when it does.
#
# WHAT IT CHECKS
# --------------
#   1. Every *IntegrationTests.cs / *SmokeTests.cs file has a row naming it in the inventory.
#   2. Every suite a ROW names still exists (a row for a deleted -- or never-written -- file sends the next
#      reader looking for something that is not there; the two found in the gh#862 audit had never existed in
#      any commit, on any branch). Scoped to rows so §2 can still DISCUSS a suite it does not list.
#   3. The stated total matches the real one, so a future drift is visible in the document itself rather than
#      only in this check's output.
#
# Deliberately SDK-free and dependency-free (grep + find), so it runs in the fast tier beside
# check-doc-duplication.sh and check-env-forwarding.sh rather than waiting on a build.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

INVENTORY="documentation/integration-test-audit.md"
SUITE_ROOT="src/MarqSpec.TradingCopilot.IntegrationTests"

[ -f "$INVENTORY" ] || { echo "error: $INVENTORY not found" >&2; exit 1; }
[ -d "$SUITE_ROOT" ] || { echo "error: $SUITE_ROOT not found" >&2; exit 1; }

# The suites on disk, by file name. TestHost/ scaffolding and factories are not suites and carry no row.
mapfile -t ON_DISK < <(find "$SUITE_ROOT" \( -name '*IntegrationTests.cs' -o -name '*SmokeTests.cs' \) -printf '%f\n' | sort)

# Names appearing in a TABLE ROW -- a line starting with '|'. Deliberately not "mentioned anywhere": the inventory
# also discusses suites in prose, including a note naming two files it once listed and has since cleared
# (`OrderExecutionEndpointsIntegrationTests.cs` / `ProductionSmokeTests.cs`, which never existed in any commit).
# Counting a historical note as a row would resurrect exactly the phantom the note exists to record removing.
mapfile -t NAMED < <(grep '^|' "$INVENTORY" | grep -o '[A-Za-z0-9_]*\(IntegrationTests\|SmokeTests\)\.cs' | sort -u)

# Both directions of the set difference at once. `comm` rather than a nested grep loop on purpose: the loop form
# spawns one subprocess per pair, which at 90 suites is ~8k processes -- imperceptible on the Linux runner, but
# ~30s per invocation on a Windows dev box, and a local check slow enough to skip is one that gets skipped.
mapfile -t missing < <(comm -23 <(printf '%s\n' "${ON_DISK[@]}") <(printf '%s\n' "${NAMED[@]}"))
mapfile -t phantom < <(comm -13 <(printf '%s\n' "${ON_DISK[@]}") <(printf '%s\n' "${NAMED[@]}"))

status=0

if [ ${#missing[@]} -gt 0 ]; then
    status=1
    echo "FAIL: ${#missing[@]} suite(s) on disk have no row in $INVENTORY §2:" >&2
    printf '  %s\n' "${missing[@]}" >&2
    echo "" >&2
    echo "Add a row per suite -- what it guards and which failure mode it can fail on. A suite absent from the" >&2
    echo "inventory is invisible to the next agent asking whether a path is already covered." >&2
    echo "" >&2
fi

if [ ${#phantom[@]} -gt 0 ]; then
    status=1
    echo "FAIL: ${#phantom[@]} row(s) name a suite that does not exist:" >&2
    printf '  %s\n' "${phantom[@]}" >&2
    echo "" >&2
    echo "Remove the row. If the suite is genuinely planned and not yet written, say so in PROSE outside the" >&2
    echo "table (§2 already does this for two names it once listed) -- a row reads exactly like a shipped suite," >&2
    echo "and sends the next reader grepping for a file that is not there." >&2
    echo "" >&2
fi

# The stated total, so drift is visible in the document and not only here. Matched loosely (the sentence may be
# reworded) but the NUMBER must be right.
stated="$(grep -o '\*\*[0-9]\+\*\* suite files' "$INVENTORY" | head -1 | grep -o '[0-9]\+' || true)"
if [ -z "$stated" ]; then
    status=1
    echo "FAIL: $INVENTORY does not state its suite count." >&2
    echo "  Add a sentence of the form '**<n>** suite files' to §2, so the next drift is visible in the document." >&2
elif [ "$stated" -ne "${#ON_DISK[@]}" ]; then
    status=1
    echo "FAIL: the inventory states **$stated** suite files; there are ${#ON_DISK[@]} on disk." >&2
fi

if [ "$status" -eq 0 ]; then
    echo "OK: all ${#ON_DISK[@]} integration/smoke suites have a row, every row names a real file, and the stated count matches."
fi

exit "$status"
