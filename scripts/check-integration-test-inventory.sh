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
#   1. Every *IntegrationTests.cs / *SmokeTests.cs file is the SUBJECT of a row -- named in the row's first
#      cell, not merely mentioned somewhere in one. A description cell cross-referencing another suite is not
#      that suite's row, and treating it as one would let a suite ship with no row at all (PR #945 review).
#   2. No suite is the subject of MORE THAN ONE row. Two rows can disagree, and the reader acts on whichever
#      they land on -- gh#952 was a "suite not yet written" row left standing after the suite shipped and got a
#      second row. Counted over SOLE-subject rows only, which exempts a GROUP row naming several suites at once.
#
#      The exemption rests on ONE live case, so do not read it as broader than it is. §2 has two group rows (the
#      staging harness, 2 members; the SuggestionDrift trio, 3) covering 5 members, of which exactly ONE --
#      `BracketSizingStagingIntegrationTests.cs` -- also carries its own row. Counting group members instead of
#      rows reddens that one suite and nothing else; the other four members appear only in their group row and
#      are unaffected either way. Measured at gh#972; re-measure rather than trusting this sentence.
#
#      The exemption is row-shaped, not member-shaped, so a duplicate CAN be laundered by adding a second suite
#      name to the offending subject cell. Accepted as the cost of not failing that one legitimate row.
#   3. Every suite a row's subject names still exists (a row for a deleted -- or never-written -- file sends the
#      next reader looking for something that is not there; the two found in the gh#862 audit had never existed
#      in any commit, on any branch). Scoped the same way, so §2 can still DISCUSS a suite it does not list.
#   4. No two suite files share a basename, which the inventory's name-keyed rows cannot express.
#   5. The stated total matches the real one, so a future drift is visible in the document itself rather than
#      only in this check's output.
#
# What it does NOT check: whether a row's STATUS is true. A row is a claim that a suite exists; nothing here can
# tell you "Proposed" has outlived the suite shipping. That is the gh#952 defect's other half, and it stays a
# human obligation -- when a suite ships, retire the proposal row that asked for it.
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

# The inventory keys rows on FILE NAME, not path, so two suites sharing one basename in different folders are a
# state it cannot express: whichever row you write, the other suite has no row of its own and no row you can add
# will give it one. Caught here with an actionable message rather than left to surface as a permanently-missing
# suite on a required check -- the remedy is renaming a test file, which the missing-row error would never suggest.
mapfile -t DUPLICATE_BASENAMES < <(printf '%s\n' "${ON_DISK[@]}" | uniq -d)

# Names in the row's SUBJECT cell -- field 2 of a '|'-delimited line -- not anywhere in the row. A description
# cell legitimately cross-references OTHER suites (§2 has several: the sibling that widened a card, the staging
# gate that proves the venue half, the suite that guards a neighbouring filter), and counting those as rows
# breaks the check in BOTH directions: a suite with no row of its own passes because a neighbour name-dropped it,
# and a cross-reference to a since-deleted suite fails as a phantom "row" that does not exist to be removed.
# Scoping to the subject is what makes "every suite has a row" the thing actually enforced.
#
# The trade, stated so it is a decision and not an oversight: a cross-reference to a suite that is later DELETED
# now goes unnoticed here. That is accepted -- a dangling cross-reference costs a reader one failed grep, while
# the alternative costs the check the thing it exists for.
#
# Rows are excluded from the *inventory* only by prose: §2 also names two files it once listed and has since
# cleared (`OrderExecutionEndpointsIntegrationTests.cs` / `ProductionSmokeTests.cs`, which never existed in any
# commit), and that note must not read as a row either -- which the '^|' filter already handles.
mapfile -t NAMED < <(grep '^|' "$INVENTORY" | awk -F'|' '{print $2}' \
    | grep -o '[A-Za-z0-9_]*\(IntegrationTests\|SmokeTests\)\.cs' | sort -u)

# Two rows claiming the same suite is worse than none: they can disagree, and the check that guarantees a suite
# is rostered says nothing about being rostered ONCE. gh#952 was exactly that -- a gh#381 "Proposed (suite not
# yet written)" row left standing after gh#534 shipped the suite and added a second row saying Active. Both named
# a real file, so both passed, and a reader landing on the stale one rewrites a suite that already exists.
#
# Counted over SOLE-subject rows only. A subject cell naming several suites at once is the sanctioned group row
# -- the shared staging harness lists its members that way -- and its members legitimately keep rows of their own.
# One awk pass rather than a shell loop over rows: the loop form spawned two subprocesses per row and took
# minutes on Windows, the same cost that made the earlier nested-grep version unusable. A local check slow enough
# to skip is one that gets skipped.
mapfile -t DUPLICATE_ROWS < <(
    awk -F'|' '
        /^\|/ {
            subject = $2; n = 0; last = ""
            while (match(subject, /[A-Za-z0-9_]+(IntegrationTests|SmokeTests)\.cs/)) {
                n++; last = substr(subject, RSTART, RLENGTH)
                subject = substr(subject, RSTART + RLENGTH)
            }
            if (n == 1) { print last }   # sole-subject rows only; a group row lists its members and is exempt
        }
    ' "$INVENTORY" | sort | uniq -d
)

# Both directions of the set difference at once. `comm` rather than a nested grep loop on purpose: the loop form
# spawns one subprocess per pair, which at 90 suites is ~8k processes -- imperceptible on the Linux runner, but
# ~30s per invocation on a Windows dev box, and a local check slow enough to skip is one that gets skipped.
# Compared against the DEDUPED disk list: `comm` is a set operation, and a basename appearing twice would
# otherwise report as missing however many rows name it -- an unfixable failure. The duplicate itself is reported
# above as its own finding; ON_DISK keeps every entry so the count below stays a true file count.
mapfile -t missing < <(comm -23 <(printf '%s\n' "${ON_DISK[@]}" | uniq) <(printf '%s\n' "${NAMED[@]}"))
mapfile -t phantom < <(comm -13 <(printf '%s\n' "${ON_DISK[@]}" | uniq) <(printf '%s\n' "${NAMED[@]}"))

status=0

if [ ${#DUPLICATE_ROWS[@]} -gt 0 ]; then
    status=1
    echo "FAIL: ${#DUPLICATE_ROWS[@]} suite(s) are the subject of more than one row:" >&2
    printf '  %s\n' "${DUPLICATE_ROWS[@]}" >&2
    echo "" >&2
    echo "Two rows for one suite can disagree, and one of them will be the stale one a reader acts on. Keep the" >&2
    echo "row that tells the truth; if the other subsection still wants the cross-reference, leave a line of PROSE" >&2
    echo "pointing at it (prose is not a row, so it does not trip this)." >&2
    echo "" >&2
fi

if [ ${#DUPLICATE_BASENAMES[@]} -gt 0 ]; then
    status=1
    echo "FAIL: ${#DUPLICATE_BASENAMES[@]} suite file name(s) are used by more than one file:" >&2
    printf '  %s\n' "${DUPLICATE_BASENAMES[@]}" >&2
    echo "" >&2
    echo "The inventory keys rows on file name, so it cannot describe two suites that share one -- and no row you" >&2
    echo "add will fix it. Rename one of the files (its folder is not enough to tell them apart here)." >&2
    echo "" >&2
fi

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
    # Says what is actually verified. It used to claim "every row names a real file", which the gh#948 narrowing
    # to the row SUBJECT made false in the case that narrowing deliberately accepts: a description cell may
    # cross-reference a suite that is not on disk, and this check passes it on purpose (gh#952).
    echo "OK: all ${#ON_DISK[@]} integration/smoke suites are the subject of exactly one row, every row subject names a real file, and the stated count matches."
fi

exit "$status"
