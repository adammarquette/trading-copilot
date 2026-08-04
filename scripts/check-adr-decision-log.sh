#!/usr/bin/env bash
#
# Fails when an ADR's decision log drifts from its structural invariants (gh#600).
#
# WHY THIS EXISTS (gh#600)
# -----------------------
# The `## Decision log` convention is maintained by hand and keeps re-drifting. gh#492 first put ADR-0007's trail
# in date order and added its index; gh#524 did the same for ADR-0008; then gh#580 had to RE-fix ADR-0007 after
# five later updates (gh#530/#529/#532/#531/#577) accumulated AFTER `## Follow-ups`, so a reader again met a
# non-final "final" list. Three manual fixes for one recurring class. This asserts the structure in CI so the next
# drifting append fails at PR time -- not by a fourth manual re-fix, or a later reader tripping over stale order.
#
# INVARIANTS -- for every documentation/adr/NNNN-*.md that has a `## Decision log`:
#   1. `## Follow-ups` is the last section: no `## Update (...)` heading appears after it.
#   2. The index table matches the trail: one `| YYYY-MM-DD | ... |` row (in the Decision-log section) per
#      `## Update (YYYY-MM-DD)` heading, equal count, and each row's date equals its heading's date in order.
#   3. The `## Update` dates ascend in file order.
#
# SCOPE IS THE STRUCTURE, NEVER THE PROSE -- this reads headings, dates and the index table only. It must never
# touch the load-bearing rationale the ADRs deliberately keep (issue gh#600). Needs no .NET SDK, so it runs early
# beside the other no-build doc/config gates and fails fast on a docs-only PR.

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

DATE='[0-9]{4}-[0-9]{2}-[0-9]{2}'

fail=0
problem() { # file, message
    if [ "$fail" -eq 0 ]; then printf 'ADR decision-log invariants failed:\n\n'; fi
    fail=$((fail + 1))
    printf '  %s\n    %s\n\n' "$1" "$2"
}

shopt -s nullglob
for adr in documentation/adr/*.md; do
    grep -qE '^## Decision log' "$adr" || continue

    log_line=$(grep -nE '^## Decision log' "$adr" | head -1 | cut -d: -f1)

    # The index table lives between `## Decision log` and the NEXT `## ` heading; bound the search there so a
    # date-shaped table row inside a later `## Update` section can never be mistaken for an index row.
    index_end=""
    while IFS=: read -r ln _; do
        if [ "$ln" -gt "$log_line" ]; then index_end="$ln"; break; fi
    done < <(grep -nE '^## ' "$adr")
    [ -n "$index_end" ] || index_end=$(( $(wc -l < "$adr") + 1 ))

    # Index rows: `| YYYY-MM-DD | ...` in that range (the `| Date | Update |` header and `|---|` separator carry
    # no date, so they fall out). Take only the FIRST date on the row -- the row also links to a `#update-DATE--`
    # anchor, so the whole-line date pattern would otherwise capture the anchor date too.
    mapfile -t index_dates < <(
        sed -n "$((log_line + 1)),$((index_end - 1))p" "$adr" | grep -oE "^\|[[:space:]]*${DATE}" | grep -oE "$DATE")

    mapfile -t update_dates < <(grep -oE "^## Update \(${DATE}\)" "$adr" | grep -oE "$DATE")

    # --- Invariant 1: Follow-ups is last (no Update heading after it) ---
    followups_line=$(grep -nE '^## Follow-ups' "$adr" | head -1 | cut -d: -f1)
    if [ -n "$followups_line" ]; then
        while IFS=: read -r ln text; do
            if [ "$ln" -gt "$followups_line" ]; then
                problem "$adr" "a '## Update' heading appears AFTER '## Follow-ups' (line $followups_line): line $ln, \"${text}\". Move every update above Follow-ups so the section stays last (gh#580)."
                break
            fi
        done < <(grep -nE "^## Update \(${DATE}\)" "$adr")
    fi

    # --- Invariant 2: the index matches the trail (count, then date-by-date) ---
    if [ "${#index_dates[@]}" -ne "${#update_dates[@]}" ]; then
        problem "$adr" "the Decision-log index has ${#index_dates[@]} row(s) but the file has ${#update_dates[@]} '## Update' heading(s); they must be one-to-one. Add or remove the index row for the change that differs."
    else
        for i in "${!update_dates[@]}"; do
            if [ "${index_dates[$i]}" != "${update_dates[$i]}" ]; then
                problem "$adr" "index row $((i + 1)) dated '${index_dates[$i]}' does not match the '## Update' heading at the same position dated '${update_dates[$i]}'. The index must mirror the trail in order."
                break
            fi
        done
    fi

    # --- Invariant 3: Update dates ascend in file order ---
    prev=""
    for i in "${!update_dates[@]}"; do
        d="${update_dates[$i]}"
        if [ -n "$prev" ] && [[ "$d" < "$prev" ]]; then
            problem "$adr" "'## Update' dates are out of order: '$d' (heading $((i + 1))) precedes '$prev' just above it. Dates must ascend in file order (append newest last)."
            break
        fi
        prev="$d"
    done
done

if [ "$fail" -gt 0 ]; then
    printf 'FAIL: %d ADR decision-log invariant violation(s). Fix the ordering/index, never the prose (gh#600).\n' "$fail"
    exit 1
fi

printf 'OK: every ADR decision log keeps Follow-ups last, an index matching its trail, and ascending dates.\n'
exit 0
