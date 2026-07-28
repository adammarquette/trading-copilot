#!/usr/bin/env bash
#
# Claims a work item for this session: checks whether anyone already has it, then creates the worktree,
# branches off develop, and pushes the branch EMPTY so the claim is globally visible before any work starts.
#
#   scripts/claim.sh 375                    # infers type=feature and the slug from the issue title
#   scripts/claim.sh 375 bug my-short-slug  # explicit type and slug
#   scripts/claim.sh 375 --check            # report only; claim nothing
#
# WHY THIS EXISTS (gh#375)
# -----------------------
# Parallel sessions duplicated a full session's work in one evening: gh#289 (#299 merged / #301 discarded),
# gh#295 (#316 / #319), gh#330 (#363 / #364), and one four-line compile break that produced THREE issues and
# THREE fixes -- #353 was opened ten minutes before the fix that merged. It was first and it was correct; it lost
# a race, not an argument.
#
# The cause is that a session claims work by creating a LOCAL worktree, which no other session can see, and the
# first globally visible artifact -- the pushed branch -- appears only when the work is essentially finished.
# This moves the push to the front. Cost: about a second. Benefit: the claim exists from the moment work starts.
#
# WHY NOT THE OTHER SIGNALS
# -------------------------
#   * Issue assignee  -- single-operator repo, every issue reads `adammarquette`. No information.
#   * Board column    -- manual and demonstrably stale (auto-add leaves Status empty; cards go unmoved).
#   * Local worktree  -- catches same-machine collisions only, and a worktree is not proof of an ACTIVE claim
#                        (29 stale ones were swept in a single evening).
# The remote branch is the only signal that is both global and self-describing: `<type>/<id>_<title>` embeds the
# issue number, so a claim is greppable without a registry to maintain.
#
# WHAT THIS DOES NOT DO
# ---------------------
# It does not make claiming atomic. Two sessions can check within the same second and both proceed. It narrows
# the collision window from HOURS OF WORK to seconds; a genuinely atomic claim needs a lock, which is
# disproportionate here. See CONTRIBUTING.md for the staleness rule that stops claims from becoming permanent.
set -euo pipefail

# The MAIN clone, never the worktree this script happens to live in. Agents run this from inside a worktree far
# more often than from the main checkout, and `dirname $BASH_SOURCE/..` would then resolve to that worktree --
# so `git worktree add .worktrees/<id>` would NEST a worktree inside a worktree. The common git dir is shared by
# every worktree and always points at the main clone's `.git`, so its parent is the root regardless of where
# this is invoked from.
REPO_ROOT="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")"
STALE_AFTER_HOURS=4

die() { echo "error: $*" >&2; exit 1; }

ID="${1:-}"
[ -n "$ID" ] || die "usage: scripts/claim.sh <issue-id> [type] [slug]   (type: feature|bug|hotfix)"
[[ "$ID" =~ ^[0-9]+$ ]] || die "the work-item id must be the tracking GitHub issue NUMBER, got '$ID'"

CHECK_ONLY=false
TYPE="feature"
SLUG=""
for arg in "${@:2}"; do
    case "$arg" in
        --check)               CHECK_ONLY=true ;;
        feature|bug|hotfix)    TYPE="$arg" ;;
        *)                     SLUG="$arg" ;;
    esac
done

cd "$REPO_ROOT"
git fetch origin --prune --quiet

# ---------------------------------------------------------------------------------------------------------
# 1. Is it already claimed?
# ---------------------------------------------------------------------------------------------------------
# The separator before the id is a SLASH, not an underscore -- `<type>/<id>_<title>`. Matching on `_<id>_`
# never fires, which is worse than no check at all: it reports "unclaimed" for every genuinely claimed issue,
# permitting exactly the duplicate work this guards against. Anchor on `/<id>_`.
EXISTING="$(git ls-remote --heads origin 2>/dev/null | sed 's|.*refs/heads/||' | grep -E "^[a-z]+/${ID}_" || true)"

if [ -n "$EXISTING" ]; then
    echo "CLAIMED — a remote branch for #${ID} already exists:"
    while IFS= read -r branch; do
        [ -n "$branch" ] || continue
        TIP="$(git log -1 --format='%ct' "origin/${branch}" 2>/dev/null || echo 0)"
        NOW="$(date -u +%s)"
        AGE_H=$(( (NOW - TIP) / 3600 ))
        BASE="$(git merge-base "origin/${branch}" origin/develop 2>/dev/null || echo '')"
        AHEAD="$(git rev-list --count "${BASE}..origin/${branch}" 2>/dev/null || echo '?')"
        FLAG=""
        [ "$AGE_H" -ge "$STALE_AFTER_HOURS" ] && FLAG="  <-- STALE (>= ${STALE_AFTER_HOURS}h)"
        echo "    ${branch}  (${AHEAD} commit(s), last activity ${AGE_H}h ago)${FLAG}"
    done <<< "$EXISTING"
    echo ""
    echo "If it is stale, say so ON THE ISSUE before taking it over (CONTRIBUTING.md, 'Claiming work')."
    echo "Announcing is what makes a wrong staleness call recoverable instead of a second collision."
    exit 1
fi

LOCAL_WT="$(git worktree list | grep -E "[/\\\\]${ID}_" || true)"
if [ -n "$LOCAL_WT" ]; then
    echo "note: a LOCAL worktree for #${ID} exists but nothing is pushed — a previous session here may have"
    echo "      abandoned it, or may be mid-work without having claimed properly:"
    echo "$LOCAL_WT" | sed 's/^/    /'
    echo ""
fi

if [ "$CHECK_ONLY" = true ]; then
    echo "UNCLAIMED — #${ID} has no remote branch."
    exit 0
fi

# ---------------------------------------------------------------------------------------------------------
# 2. Claim it.
# ---------------------------------------------------------------------------------------------------------
if [ -z "$SLUG" ]; then
    TITLE="$(gh issue view "$ID" --json title --jq .title 2>/dev/null || true)"
    # Strip the title's prefix, lowercase, non-alphanumerics to dashes, first 4 words. Two forms, deliberately
    # separate: `feat(scope): ` / `QA(task#267) - ` (scoped, either separator) and `docs: ` (unscoped, colon
    # only). Allowing a bare `-` separator without the parens would eat the first word of a hyphenated title --
    # "Multi-login: lift the …" would claim as `login-lift-the-one`.
    SLUG="$(printf '%s' "$TITLE" \
        | sed -E 's/^[a-zA-Z]+\([^)]*\)!? *[:-] *//; s/^[a-zA-Z]+!?: *//' \
        | tr '[:upper:]' '[:lower:]' \
        | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//' \
        | cut -d- -f1-4)"
    [ -n "$SLUG" ] || die "could not derive a slug from issue #${ID}; pass one explicitly"
fi

BRANCH="${TYPE}/${ID}_${SLUG}"
WORKTREE=".worktrees/${ID}_${SLUG}"

[ -e "$WORKTREE" ] && die "$WORKTREE already exists — remove it, or 'git worktree prune', before claiming"

echo "claiming #${ID} as ${BRANCH}"
git worktree add "$WORKTREE" -b "$BRANCH" origin/develop >/dev/null
git -C "$WORKTREE" push -u origin "$BRANCH" --quiet

# Submodules are NOT populated in a fresh worktree; the ProjectX-dependent projects fail to compile without this.
git -C "$WORKTREE" submodule update --init --recursive --quiet 2>/dev/null || true

echo ""
echo "claimed. the branch is pushed and empty, so every other session can see it now."
echo "  worktree: ${WORKTREE}"
echo "  branch:   ${BRANCH}"
echo ""
echo "push your commits as you go — the branch tip is the heartbeat the staleness rule reads."
echo "if you abandon this, delete the branch (git push origin --delete ${BRANCH}) so it stops blocking."
