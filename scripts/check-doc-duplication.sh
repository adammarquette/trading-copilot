#!/usr/bin/env bash
#
# Fails when a canonical rule is RESTATED in a document that does not own it, without citing the owner.
#
# WHY THIS EXISTS (gh#616)
# -----------------------
# A rule with two homes drifts, and the drift is invisible until an agent reads the stale copy and behaves
# wrong WHILE CITING A DOC AS ITS AUTHORITY. That is worse than no doc. A gh#616 audit found ten live instances:
# the board told agents to self-assign to avoid collisions while CONTRIBUTING.md said the assignee signal is
# worthless in a single-operator repo; the same-PR doc list named a different set of documents in each of the
# five places it appeared; the reviewer checklist dropped "bug fixes are regression-first" -- exactly where it
# would have been enforced; `Co-Authored-By:` was required in one file and omitted in the four others that
# stated the trailer rule.
#
# The token cost of duplication is minor. The drift cost is not.
#
# HOW IT DECIDES
# --------------
# For each rule below: find every line matching its DISTINGUISHING phrase -- wording specific enough that a
# match means "this line is stating the rule", not merely mentioning the topic. A match is a VIOLATION unless:
#
#   * it is in the rule's canonical file (the one allowed home), OR
#   * the line CITES the owner -- a markdown link, or a `§N` / `gh#N` / `R-#` reference. A pointer is the
#     desired shape, so a restatement that defers to its owner passes.
#
# Exempt paths are append-only or narrative: an ingest log rewrites history if edited, and ADRs reuse rule
# vocabulary as evidence for a decision rather than as a statement of the rule.
#
# Extending it: add a row to RULES. Keep the phrase narrow -- a phrase that matches the topic instead of the
# rule produces noise, and a noisy gate teaches people to ignore it (platform contract: "a local check that
# disagrees with CI is worse than no local check").

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

# id | canonical file (the one allowed home) | distinguishing regex
#
# Phrases name the RULE, not its topic. "in the same PR" was tried and rejected: it also matches genuine
# neighbouring rules (the data dictionary's migration-lockstep rule, "add its routing row in the same PR"),
# so it flagged correct docs. "same-PR rule" matches only text claiming to state the rule itself.
RULES=$(
    cat <<'EOF'
same-pr|documentation/trading-platform-engineering.md|same-PR rule|docs in lockstep
practice-accounts|documentation/deployment-runbook.md|practice accounts only outside production
no-secrets|documentation/trading-platform-engineering.md|no secrets in source
enforcement-below-model|documentation/trading-platform-engineering.md|enforcement lives below the model
fluent-syntax|documentation/trading-platform-engineering.md|never LINQ query-comprehension
test-first|documentation/trading-platform-engineering.md|no new public method without a failing test
claim-is-the-branch|CONTRIBUTING.md|branch is the claim
EOF
)

# Append-only logs and ADR narrative reuse the vocabulary without stating the rule.
is_exempt() {
    case "$1" in
        documentation/wiki/log.md) return 0 ;;
        documentation/adr/*) return 0 ;;
        *) return 1 ;;
    esac
}

# A statement that points at its owner is the shape we want, not a violation.
#
# Checked over a WINDOW, not the matched line: these rules are written as wrapped markdown bullets, so the
# citation routinely lands two or three lines below the distinguishing phrase. A line-only check flagged every
# correctly-cited summary in the repo -- false positives in a gate are how a gate gets ignored.
CITATION='\]\([^)]+\)|§[0-9]|gh#[0-9]|R-[0-9]'
cites_owner() {
    local file=$1 line=$2 from=$((${2} - 3))
    [ "$from" -lt 1 ] && from=1
    sed -n "${from},$((line + 3))p" "$file" 2>/dev/null | grep -qE "$CITATION"
}

violations=0
while IFS='|' read -r id canonical phrase alt; do
    [ -n "$id" ] || continue
    pattern="$phrase"
    [ -n "${alt:-}" ] && pattern="$phrase|$alt"

    while IFS=: read -r file line text; do
        [ -n "$file" ] || continue
        [ "$file" = "$canonical" ] && continue
        is_exempt "$file" && continue
        cites_owner "$file" "$line" && continue

        if [ "$violations" -eq 0 ]; then
            printf 'Canonical rules restated without citing their owner:\n\n'
        fi
        violations=$((violations + 1))
        printf '  %s:%s\n' "$file" "$line"
        printf '    rule  : %s (owned by %s)\n' "$id" "$canonical"
        printf '    text  : %s\n' "$(printf '%s' "$text" | sed 's/^[[:space:]]*//' | cut -c1-110)"
        printf '    fix   : delete the restatement and link %s, or move the rule if this file should own it.\n\n' "$canonical"
    done < <(grep -rInE "$pattern" --include='*.md' . 2>/dev/null | grep -v '^\./\.worktrees/' | grep -v '/node_modules/' | grep -v '^\./external/' | sed 's|^\./||')
done <<<"$RULES"

if [ "$violations" -gt 0 ]; then
    printf 'FAIL: %d restatement(s). One rule, one home; everywhere else links to it.\n' "$violations"
    exit 1
fi

printf 'OK: no canonical rule is restated outside its home without citing it.\n'
