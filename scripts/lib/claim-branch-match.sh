#!/usr/bin/env bash
#
# claim-branch-match.sh (gh#833) -- the ONE rule for "does this branch name claim issue <id>?", shared by
# scripts/claim.sh and its test (scripts/tests/claim.test.sh) so the check and its proof can never drift apart.
#
# A claim is a pushed remote branch whose name embeds the issue number. The canonical shape is
# `<type>/<id>_<title>` (CONTRIBUTING.md, "Claiming work"), but that is not the only shape work arrives in, and a
# claim must hold whatever tool created the branch:
#
#   * `wip/731-trade-journal-composition`          -- a HYPHEN after the id, not the canonical underscore
#   * `cursor/news-fuzzy-dedup-fallback-764-3bd5`  -- the id in the MIDDLE, because a Cursor Cloud agent's harness
#                                                     mandates `cursor/<descriptive-name>-<suffix>` and cannot
#                                                     choose otherwise (gh#833). Cloud sessions are a normal way
#                                                     work is done here, so every one of them being invisible to
#                                                     the check is a structural false-negative, not a naming nit.
#
# So the id is matched as a whole numeric TOKEN anywhere in the name, bounded by non-digits (or the ends), rather
# than only immediately after the first slash. The digit boundaries are load-bearing in the other direction: a
# near-miss id must never satisfy the check -- `1731` and `7310` must NOT match a check for `731`. Matching the
# wrong direction is the exact gh#375 failure ("reports unclaimed for every claimed issue"); this is its twin.
#
# Usage:  printf '%s\n' "$branch_names" | claim_branches_for <id>   # prints the lines that claim <id>
#
# This file only DEFINES a function; it runs nothing, so it is safe to source from a test.

# Filters branch names on stdin (one per line) to those that claim issue <id>. Never fails (empty in -> empty out).
claim_branches_for() {
    local id="$1"
    grep -E "(^|[^0-9])${id}([^0-9]|\$)" || true
}
