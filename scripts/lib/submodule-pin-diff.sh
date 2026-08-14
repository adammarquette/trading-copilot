#!/usr/bin/env bash
#
# submodule-pin-diff.sh (gh#839) -- the ONE way check-submodule-pins.sh decides which external/ submodule pins a PR
# ITSELF moved, shared with its test (scripts/tests/submodule-pins.test.sh) so the guard and its proof cannot drift.
#
# WHY THREE DOTS, NOT TWO (gh#839)
# --------------------------------
# It diffs from the MERGE-BASE of base and head (git's `base...head`), not the two tips (`base..head`), so it
# attributes only the pin changes THIS branch introduced. The two-dot form was a false-positive engine: the moment
# a legitimate submodule bump lands on `develop`, every OTHER open PR that never touched external/ diffs
# `develop(new pin)` against its own `head(old pin)` and trips the guard's BACKWARD-move alarm -- a safety-critical
# "this reverted a merged fix in PR #690!" cried on entirely innocent PRs, and on ALL of them at once whenever a
# venue-client bump merges. It is only ever over-strict (`strict` mode forces an update-branch that clears it), but
# a safety alarm that regularly cries wolf is one people learn to wave through, which is its own hazard.
#
# `merge-base(base, head)` is the branch point, so `base...head` sees exactly what the branch did -- the guard's own
# stated intent ("we see only THIS PR's own pin change"). Crucially this does NOT weaken the guard: when the PR
# itself moves a pin -- forward OR backward from its own base -- the merge-base IS that base, so a real move is still
# fully caught (the test pins this). Only a bump that happened on the base branch, not in this PR, stops counting.
#
# The merge-base is resolved EXPLICITLY rather than via the bare `base...head` diff syntax, so that a missing merge
# base falls back to the stricter two-dot tip diff instead of a silent empty pass -- a guard must never fail OPEN.
# For a real PR this fallback is unreachable: a branch cut from `develop` always shares history with `develop`, so
# the merge-base resolves. CI checks out with `fetch-depth: 0`, so it is present there too (`submodules: false` is
# fine -- the SHAs come from the gitlink diff, no checkout needed).
#
# Usage:  mapfile -t rows < <(changed_submodule_gitlinks "$BASE_SHA" "$HEAD_SHA")
#
# This file only DEFINES a function; it runs nothing, so it is safe to source from a test.

# Prints the `git diff --raw` rows for changed gitlinks (mode 160000) under external/ that <head> introduces
# relative to its merge-base with <base>. Format per row: ":<omode> <nmode> <osha> <nsha> <status>\t<path>".
# Never fails (no change / bad ref -> empty output).
changed_submodule_gitlinks() {
    local base="$1" head="$2" mb
    # Diff from the merge-base (three-dot semantics) so only this branch's own pin moves count. If the merge-base
    # cannot be resolved -- unreachable for a PR branched off develop -- fall back to the base tip, which is
    # stricter (the pre-gh#839 behaviour), never a silent pass.
    mb="$(git merge-base "$base" "$head" 2>/dev/null || true)"
    git diff --raw --no-abbrev "${mb:-$base}" "$head" -- external/ 2>/dev/null \
        | grep -E $'^:[0-7]{6} 160000|^:160000 [0-7]{6}' || true
}
