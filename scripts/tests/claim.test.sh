#!/usr/bin/env bash
#
# claim.test.sh (gh#833) -- proves scripts/lib/claim-branch-match.sh recognises a claim by the issue id as a whole
# token in ANY branch shape work actually arrives in (the canonical `<type>/<id>_<title>`, a `wip/<id>-...`, and a
# Cursor Cloud `cursor/<name>-<id>-<suffix>`), and REFUSES a near-miss id. Pure function over a fixture branch
# list -- no network, no gh, no remote -- so it is deterministic and runs beside the other no-SDK gates in CI.
#
# This is the failing check written first (gh#833 acceptance): against the old `^[a-z]+/${ID}_` match the wip/ and
# cursor/ cases below fail; against claim_branches_for they pass and the near-miss ids stay refused.
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/claim-branch-match.sh
source "$DIR/../lib/claim-branch-match.sh"

# The three shapes that survived the 2026-08-14 branch sweep (all live claims, gh#833), plus near-miss ids and the
# canonical shape as controls.
BRANCHES='wip/731-trade-journal-composition
cursor/news-fuzzy-dedup-fallback-764-3bd5
cursor/board-cleanup-wireframe-655-b180
feature/1731_unrelated-near-miss
feature/7310_unrelated-near-miss
feat/731_canonical-underscore-shape
qa/789_kill-flatten-audit-postgres'

fail=0
matches() { printf '%s\n' "$BRANCHES" | claim_branches_for "$1" | grep -qxF "$2"; }

expect_claimed() { # <id> <branch> <why>
    if matches "$1" "$2"; then printf 'ok    #%s claims %s\n' "$1" "$2"
    else printf '::error::FAIL  #%s should claim %s -- %s\n' "$1" "$2" "$3"; fail=1; fi
}
expect_unclaimed() { # <id> <branch> <why>
    if matches "$1" "$2"; then printf '::error::FAIL  #%s must NOT claim %s -- %s\n' "$1" "$2" "$3"; fail=1
    else printf 'ok    #%s does not claim %s\n' "$1" "$2"; fi
}

# The two branches claim.sh reported UNCLAIMED for, and the mid-name case that motivated the fix (gh#833).
expect_claimed 731 'wip/731-trade-journal-composition' 'a hyphen after the id is still a claim'
expect_claimed 764 'cursor/news-fuzzy-dedup-fallback-764-3bd5' 'a Cursor Cloud branch carries the id in the middle'
expect_claimed 655 'cursor/board-cleanup-wireframe-655-b180' 'the id mid-name is a claim whatever the words around it'

# The canonical shape must keep matching, whatever the type prefix.
expect_claimed 731 'feat/731_canonical-underscore-shape' 'the documented <type>/<id>_<title> shape still claims'
expect_claimed 789 'qa/789_kill-flatten-audit-postgres' 'the canonical shape claims regardless of type prefix'

# Near-miss ids must NOT false-positive -- gh#375's failure in the other direction (gh#833 acceptance).
expect_unclaimed 731 'feature/1731_unrelated-near-miss' '1731 must not satisfy a check for 731'
expect_unclaimed 731 'feature/7310_unrelated-near-miss' '7310 must not satisfy a check for 731'

# An id present in no branch claims nothing.
expect_unclaimed 999 'feat/731_canonical-underscore-shape' 'an unrelated id claims no branch'

if [ "$fail" -ne 0 ]; then
    printf '\nFAIL: claim-branch-match did not behave as gh#833 requires.\n' >&2
    exit 1
fi
printf '\nOK: every branch shape a claim can take is recognised, and near-miss ids are refused.\n'
