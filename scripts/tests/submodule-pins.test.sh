#!/usr/bin/env bash
#
# submodule-pins.test.sh (gh#839) -- proves scripts/lib/submodule-pin-diff.sh attributes a submodule pin change to
# the PR that ACTUALLY made it, by diffing from the merge-base (three-dot) rather than the two branch tips.
#
# The bug it pins: once a legitimate `external/**` bump lands on develop, the old two-dot diff made
# check-submodule-pins.sh fail EVERY other open PR that never touched external/ -- it saw develop's new pin against
# the PR's old pin and cried "BACKWARD move -- reverted a merged fix!" (the gh#690 safety alarm) on innocent PRs, all
# at once. The fix diffs from merge-base(base, head), so only what THIS branch did counts.
#
# The fixture is built with git plumbing (commit-tree over synthetic 160000 gitlinks) -- no real submodule, no
# network, no gh, no checkout -- so it is deterministic and runs beside the other no-SDK gates in CI. It is the
# failing check written first (gh#839 acceptance): against a two-dot diff the stale-PR case below reports a spurious
# backward move; against changed_submodule_gitlinks it is silent, while a PR's OWN pin move is still caught.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../lib/submodule-pin-diff.sh
source "$DIR/../lib/submodule-pin-diff.sh"

# Fully isolate from host git config (signing, hooks, identity) so the fixture is hermetic and cannot hang on a
# global commit.gpgsign; identity then comes from the env below.
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
export GIT_AUTHOR_DATE='2026-01-01T00:00:00 +0000' GIT_COMMITTER_DATE='2026-01-01T00:00:00 +0000'

REPO="$(mktemp -d)"
trap 'rm -rf "$REPO"' EXIT
git -C "$REPO" init -q -b main

# Two arbitrary submodule pins; P2 is "newer" than P1 only by convention -- the guard reads the recorded SHAs from
# the diff, it does not resolve them, so any two distinct 40-hex values exercise the same code path.
P1='1111111111111111111111111111111111111111'
P2='2222222222222222222222222222222222222222'

# Build a tree from cacheinfo specs ("<mode>,<sha>,<path>") via a throwaway index, printing the tree sha.
build_tree() {
    local idx="$REPO/.git/build-index"; rm -f "$idx"
    local spec
    for spec in "$@"; do GIT_INDEX_FILE="$idx" git -C "$REPO" update-index --add --cacheinfo "$spec"; done
    GIT_INDEX_FILE="$idx" git -C "$REPO" write-tree; rm -f "$idx"
}
commit() { # <tree> [parent] -> commit sha
    if [ -n "${2:-}" ]; then git -C "$REPO" commit-tree "$1" -p "$2" -m x; else git -C "$REPO" commit-tree "$1" -m x; fi
}

# An unrelated non-submodule change, so the "stale" PR is a real PR with real work, just nothing under external/.
BLOB="$(printf 'unrelated work\n' | git -C "$REPO" hash-object -w --stdin)"

T_P1="$(build_tree "160000,$P1,external/fake")"
T_P2="$(build_tree "160000,$P2,external/fake")"
T_STALE="$(build_tree "160000,$P1,external/fake" "100644,$BLOB,work.txt")"

MB="$(commit "$T_P1")"          # the branch point: pin at P1
BUMP="$(commit "$T_P2" "$MB")"  # a legitimate forward bump P1 -> P2 lands on the base branch (develop)
STALE="$(commit "$T_STALE" "$MB")"  # an innocent PR: forked at MB, changed only work.txt, pin still P1
BACK="$(commit "$T_P1" "$BUMP")"    # a PR off the bumped base that itself moves the pin P2 -> P1 (the gh#690 shape)

cd "$REPO"
fail=0
assert_empty() { # <desc> <output>
    if [ -z "$2" ]; then printf 'ok    %s\n' "$1"
    else printf '::error::FAIL  %s\n      expected no changed gitlink, got:\n%s\n' "$1" "$2"; fail=1; fi
}
assert_nonempty() { # <desc> <output>
    if [ -n "$2" ]; then printf 'ok    %s\n' "$1"
    else printf '::error::FAIL  %s\n      expected a changed gitlink row, got none\n' "$1"; fail=1; fi
}

# 1. THE FIX: base bumped the pin, the PR did not touch external/ -> the guard must see nothing (gh#839).
assert_empty "stale PR (base bumped to P2, PR left external/ at P1) moves no pin" \
    "$(changed_submodule_gitlinks "$BUMP" "$STALE")"

# 2. CONTROL: the pre-fix two-dot tip diff DID false-flag exactly that stale PR as a backward move. Proves the
#    fixture genuinely reproduces the bug, so assertion 1 is meaningful and not vacuously empty.
assert_nonempty "control: the old two-dot tip diff falsely flags the stale PR" \
    "$(git diff --raw --no-abbrev "$BUMP" "$STALE" -- external/ | grep -E '160000' || true)"

# 3. NOT WEAKENED: a PR that itself bumps the pin forward is still caught (merge-base is its base).
assert_nonempty "a PR that itself bumps the pin forward (P1 -> P2) is still caught" \
    "$(changed_submodule_gitlinks "$MB" "$BUMP")"

# 4. NOT WEAKENED: a PR that itself moves the pin BACKWARD from its own base is still caught -- the gh#690 incident
#    shape, and the exact move the guard exists to stop. Three-dot must not hide this.
assert_nonempty "a PR that itself moves the pin backward (P2 -> P1) is still caught (gh#690 shape)" \
    "$(changed_submodule_gitlinks "$BUMP" "$BACK")"

if [ "$fail" -ne 0 ]; then
    printf '\nFAIL: submodule-pin-diff did not attribute pin changes as gh#839 requires.\n' >&2
    exit 1
fi
printf '\nOK: a base-branch bump is not misattributed to an innocent PR, and a PR'\''s own pin move is still caught.\n'
