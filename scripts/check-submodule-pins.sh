#!/usr/bin/env bash
#
# Fails a PR that moves a submodule pin under external/ without declaring it, and ALWAYS fails a pin that
# moves BACKWARD (unless an explicit override is present). Warns when a new pin is not on the submodule's
# default branch.
#
# WHY THIS EXISTS (gh#774)
# -----------------------
# A submodule pin has been moved invisibly twice, and the second time it silently reverted a safety fix that
# had already merged:
#
#   46c06ca  PR #687  external/MarqSpec.Client.ProjectX  9203242 -> 84e2e3c  (the gh#673 fix)      forward  OK
#   4684565  PR #690  external/MarqSpec.Client.ProjectX  84e2e3c -> 9203242  (a SignalR PR!)       BACKWARD -- reverted the fix
#
# PR #690 mentions nothing about the venue client; its diff simply carried a one-line
# `external/MarqSpec.Client.ProjectX | 2 +-`. develop was pinned to the pre-fix client for five days, so
# POST /api/Order/place was retryable again -- a place that booked but lost its ack could be sent twice.
# Review cannot see it (an opaque 40-char hash), CI did not inspect external/**, and the tracker read
# "completed" while the fix was absent from the tree. This is the failure mode a machine must catch.
#
# HOW IT DECIDES
# --------------
# For each gitlink under external/ whose pin THIS PR moved -- the three-dot diff from the merge-base of BASE and
# HEAD (scripts/lib/submodule-pin-diff.sh), so a bump already merged to the base branch is never misattributed to a
# PR that only forked after it and never touched external/ (gh#839):
#
#   * DECLARATION (hard) -- the change must be declared: a `submodule-bump` label, or the PR title/body naming
#     the submodule (its path `external/<name>` or basename). An UNRELATED PR carrying a pin fails here.
#   * DIRECTION (hard)   -- resolve old...new against the submodule's OWN remote via the GitHub compare API
#     (no clone needed, per the gh#774 audit note). status "behind" == the new pin is an ANCESTOR of the old
#     one == a BACKWARD move; it always fails, declared or not, UNLESS an override (`submodule-downgrade`
#     label, or `submodule-downgrade` in the body) states the rare intentional downgrade.
#   * DEFAULT-BRANCH ANCESTRY (soft) -- warn when the new pin is not an ancestor of the submodule's default
#     branch (it was recorded from an in-flight branch tip that could be force-pushed / GC'd out from under us
#     -- the quieter hazard PR #687's own note flagged for 9203242).
#
# Every failure names the submodule, BOTH short SHAs, and the direction of travel -- the whole point is that a
# bare hash is unreadable.
#
# REGRESSION (gh#774 acceptance): re-run against the real incident and it must fail --
#   BASE_SHA=4684565~1 HEAD_SHA=4684565 ./scripts/check-submodule-pins.sh
# reproduces the backward-move failure on external/MarqSpec.Client.ProjectX (84e2e3c -> 9203242). The gh#839
# three-dot change preserves this: the merge-base of a commit and its own parent IS that parent, so the diff is
# unchanged for a PR that itself moves the pin -- only a bump inherited from the base branch stops counting.
#
# INPUTS (env; the CI job in .github/workflows/branch-policy.yml sets them from the PR context):
#   BASE_SHA   base commit; the pin diff is taken from its MERGE-BASE with HEAD (three-dot), so only THIS PR's own
#              pin moves count and a bump already on the base branch is not misattributed here (default: origin/develop; gh#839)
#   HEAD_SHA   head commit to diff TO     (default: HEAD)
#   PR_TITLE   PR title    (declaration text; empty locally => treated as undeclared, so a local run shows
#   PR_BODY    PR body      what CI would say)
#   PR_LABELS  PR label names, newline- or comma-separated
#   GH_TOKEN   token for `gh api` against the submodule repos (public, so the repo-scoped CI token suffices)
#
# A local check that disagrees with CI is worse than no local check (platform contract): this is the SAME
# script CI runs. Locally, declaration comes from the PR, so pass PR_BODY=... to simulate a declared bump.

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1

# The changed-gitlink diff rule, shared with scripts/tests/submodule-pins.test.sh so the guard and its proof cannot
# drift (gh#839). cwd is the repo root after the cd above, so this repo-relative path resolves.
# shellcheck source=lib/submodule-pin-diff.sh
source scripts/lib/submodule-pin-diff.sh

BASE_SHA="${BASE_SHA:-origin/develop}"
HEAD_SHA="${HEAD_SHA:-HEAD}"
DECL_TEXT="${PR_TITLE:-}
${PR_BODY:-}"
PR_LABELS="${PR_LABELS:-}"

fail=0

err() { printf '::error::%s\n' "$1"; printf 'FAIL  %s\n' "$1"; fail=1; }
warn() { printf '::warning::%s\n' "$1"; printf 'WARN  %s\n' "$1"; }
info() { printf '%s\n' "$1"; }

# case-insensitive substring test
contains_ci() { printf '%s' "$2" | grep -qiF -- "$1"; }
# whole-token label test (labels are newline/comma separated)
has_label() { printf '%s' "$PR_LABELS" | tr ',' '\n' | grep -qixF -- "$1"; }

short() { printf '%s' "${1:0:12}"; }

# gh api that returns empty on any failure rather than aborting the run (set -e is off deliberately).
compare_status() { gh api "repos/$1/$2/compare/$3...$4" --jq '.status' 2>/dev/null || true; }

# The changed gitlinks (mode 160000) under external/ that THIS PR introduced. git diff --raw gives the old/new
# submodule SHAs directly, so no checkout is needed; the diff is from the merge-base (three-dot) so a bump already
# on the base branch is not misread as this PR reverting it (gh#839). Row: ":<omode> <nmode> <osha> <nsha> <st>\t<path>".
mapfile -t rows < <(changed_submodule_gitlinks "$BASE_SHA" "$HEAD_SHA")

if [ "${#rows[@]}" -eq 0 ]; then
    info "OK: this PR moves no submodule pin under external/ (from the merge-base of ${BASE_SHA}...${HEAD_SHA})."
    exit 0
fi

for row in "${rows[@]}"; do
    meta="${row%%$'\t'*}"          # ":<omode> <nmode> <osha> <nsha> <status>"
    path="${row#*$'\t'}"           # "external/<name>"
    read -r omode nmode osha nsha status _ <<<"${meta#:}"
    name="${path##*/}"

    zero='0000000000000000000000000000000000000000'
    added=false;   [ "$omode" = "000000" ] || [ "$osha" = "$zero" ] && added=true
    removed=false; [ "$nmode" = "000000" ] || [ "$nsha" = "$zero" ] && removed=true

    # The submodule's own remote, from .gitmodules, mapped to owner/repo for the compare API.
    url="$(git config -f .gitmodules --get "submodule.${path}.url" 2>/dev/null || true)"
    slug="${url##*github.com/}"; slug="${slug%.git}"   # .gitmodules urls are https://github.com/<owner>/<repo>
    owner="${slug%%/*}"; repo="${slug##*/}"

    info ""
    info "Submodule ${path}: $(short "$osha") -> $(short "$nsha") (${status})"

    # --- DECLARATION (hard) -----------------------------------------------------------------------------------
    declared=false
    has_label 'submodule-bump' && declared=true
    contains_ci "$path" "$DECL_TEXT" && declared=true
    contains_ci "$name" "$DECL_TEXT" && declared=true
    if [ "$declared" != true ]; then
        err "${path} pin changed ($(short "$osha") -> $(short "$nsha")) but the PR does not declare it. A one-line submodule hash is invisible in review (gh#774, PR #690). Declare it: add the 'submodule-bump' label, or name '${path}' in the PR body."
    fi

    # --- DIRECTION (hard, always) -----------------------------------------------------------------------------
    override=false
    has_label 'submodule-downgrade' && override=true
    contains_ci 'submodule-downgrade' "$DECL_TEXT" && override=true

    if [ "$added" = true ]; then
        info "  (new submodule; no prior pin to compare direction against)"
    elif [ "$removed" = true ]; then
        info "  (submodule removed; no direction to check)"
    else
        cmp="$(compare_status "$owner" "$repo" "$osha" "$nsha")"
        case "$cmp" in
            ahead)
                info "  direction: FORWARD (new pin is a descendant of the old)." ;;
            identical)
                info "  direction: no move (identical)." ;;
            behind)
                behind_by="$(gh api "repos/$owner/$repo/compare/$osha...$nsha" --jq '.behind_by' 2>/dev/null || echo '?')"
                if [ "$override" = true ]; then
                    warn "${path} pin moves BACKWARD ($(short "$osha") -> $(short "$nsha"); new is ${behind_by} commit(s) behind old) — allowed by the submodule-downgrade override. The justification must be in the PR body."
                else
                    err "${path} pin moves BACKWARD ($(short "$osha") -> $(short "$nsha"); new is ${behind_by} commit(s) behind old, i.e. an ANCESTOR of the current pin). This is what silently reverted a merged fix in PR #690 (gh#774). If the downgrade is intentional, add the 'submodule-downgrade' label or 'submodule-downgrade:' with a reason in the body."
                fi
                ;;
            diverged)
                warn "${path} pin DIVERGED ($(short "$osha") -> $(short "$nsha")): the new pin is not a descendant of the old one, so this is a branch swap rather than a clean bump. Confirm it is intended." ;;
            "")
                warn "${path}: could not resolve $(short "$osha")...$(short "$nsha") on ${owner}/${repo} (a SHA may not exist on the remote — a force-pushed or GC'd commit). Direction unverified." ;;
            *)
                warn "${path}: unexpected compare status '${cmp}' for $(short "$osha")...$(short "$nsha")." ;;
        esac

        # --- DEFAULT-BRANCH ANCESTRY (soft) -------------------------------------------------------------------
        default="$(gh api "repos/$owner/$repo" --jq '.default_branch' 2>/dev/null || true)"
        if [ -n "$default" ]; then
            anc="$(compare_status "$owner" "$repo" "$nsha" "$default")"
            case "$anc" in
                ahead | identical)
                    : ;; # new pin is on (an ancestor of) the default branch — fine
                behind | diverged)
                    warn "${path}: the new pin $(short "$nsha") is not an ancestor of the submodule's default branch '${default}' (it was recorded from an in-flight branch tip that could be force-pushed or GC'd — the hazard PR #687 flagged for 9203242)." ;;
                "")
                    warn "${path}: could not confirm $(short "$nsha") is on '${default}' (the SHA may not exist on the remote)." ;;
            esac
        fi
    fi
done

info ""
if [ "$fail" -ne 0 ]; then
    info "FAIL: a submodule pin change is undeclared or moves backward. See the messages above (gh#774)."
    exit 1
fi
info "OK: every submodule pin change is declared and moves forward."
exit 0
