#!/usr/bin/env bash
#
# verdict-state.sh — answer ONE question about a PR: what review verdict is in force right now, and does it
# bind to the commit under test? Prints a single line and exits; it never sleeps and never polls.
#
#   STATE <TAB> SHA <TAB> ROUNDS <TAB> REVIEW_ID <TAB> DETAIL
#
#   APPROVED           the verdict binds to the head under test (same commit, or the same contribution)
#   CHANGES-REQUESTED  the last verdict asked for changes; only a push can resolve it
#   STALE              an approval exists but the PR's own contribution has changed since it was given
#   NONE               no verdict has been recorded yet (SHA and REVIEW_ID are empty)
#
# ROUNDS is how many changes-requested verdicts this PR has collected in total — the round counter
# scripts/watch-verdict.sh escalates on. REVIEW_ID identifies the review that ruled, which is how that script
# prints the right body and the right inline comments. Both are emitted HERE rather than worked out there,
# because finding them needs the same lenient parsing as the ruling — and that parsing has exactly one home
# (see below). A caller that re-derives either would be the second parser this script exists to prevent.
#
# DETAIL is a human sentence explaining the ruling, so callers can print it rather than reconstruct it. It is
# LAST because a caller reading the fields with `read` collects the remainder into its final variable.
#
# WHY THIS IS A SCRIPT AND NOT A CI STEP (gh#815)
# ----------------------------------------------
# Two callers need this answer and must never disagree about it: the `review-verdict` job in ci.yml, which
# GATES the PR, and scripts/watch-verdict.sh, which is how the AUTHOR AGENT waits for its own PR. A second
# copy of the logic below would drift, and the drift would read as the gate and the author disagreeing about
# whether a PR is approved -- with the author taking the next card while the check still reds.
#
# Substance: documentation/agents/code-reviewer.md (the verdict line) · engineering §10 (the loop).
#
# Usage:
#   verdict-state.sh <pr> [--head <sha>] [--base <ref>] [--repo <owner/name>]
#
# `--head` defaults to the PR's current head and `--base` to its base branch, both read from the API. CI
# passes them EXPLICITLY from the event payload instead: a check reports on the commit that triggered it, and
# reading "current head" mid-run would judge a commit the run never tested.
#
# Exit status: 0 when it could answer (including NONE), 2 on a usage error. It is read-only -- no writes to
# the repository, the PR, or the working tree.

# Deliberately NOT `set -e`. The comparisons below probe git for things that legitimately are not there (a
# rebase can orphan the approved commit), and every such probe is already guarded with `||`; under `-e` the
# first miss would abort the script instead of falling through to the fallback that exists for it.
set -uo pipefail

die() { printf 'verdict-state: %s\n' "$*" >&2; exit 2; }

PR="${1:-}"
[ -n "$PR" ] || die "usage: verdict-state.sh <pr> [--head <sha>] [--base <ref>] [--repo <owner/name>]"
[[ "$PR" =~ ^[0-9]+$ ]] || die "the first argument must be the PR number, got '$PR'"
shift

HEAD_SHA="${HEAD_SHA:-}"
BASE_REF="${BASE_REF:-}"
REPO="${REPO:-}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --head) HEAD_SHA="${2:?--head needs a sha}"; shift 2 ;;
    --base) BASE_REF="${2:?--base needs a ref}"; shift 2 ;;
    --repo) REPO="${2:?--repo needs owner/name}"; shift 2 ;;
    *)      die "unknown argument '$1'" ;;
  esac
done

command -v gh >/dev/null || die "gh not found"

[ -n "$REPO" ] || REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null)
[ -n "$REPO" ] || die "could not determine the repository — pass --repo owner/name"

if [ -z "$HEAD_SHA" ] || [ -z "$BASE_REF" ]; then
  pr_json=$(gh api "repos/$REPO/pulls/$PR" --jq '[.head.sha, .base.ref] | @tsv' 2>/dev/null)
  [ -n "$pr_json" ] || die "could not read PR #$PR from $REPO"
  IFS=$'\t' read -r api_head api_base <<<"$pr_json"
  [ -n "$HEAD_SHA" ] || HEAD_SHA="$api_head"
  [ -n "$BASE_REF" ] || BASE_REF="$api_base"
fi

# The target branch moves under a long-lived PR; the merge bases below are meaningless without it.
git fetch --quiet --no-tags origin "+refs/heads/$BASE_REF:refs/remotes/origin/$BASE_REF" 2>/dev/null || true

# The PR's OWN contribution at $1: everything that commit adds on top of wherever it forked from
# the target branch, hashed with `patch-id --stable` so pure line-number drift is not a change.
# Prints nothing when the commit cannot be resolved -- a rebase can orphan the approved one -- and
# the caller then falls back rather than guessing.
pr_patch_id() {
  # `local` matters: the review-reading loop below also uses `sha`.
  local sha="$1" mb
  git cat-file -e "${sha}^{commit}" 2>/dev/null \
    || git fetch --quiet --no-tags origin "$sha" 2>/dev/null \
    || return 0
  mb=$(git merge-base "origin/$BASE_REF" "$sha" 2>/dev/null) || return 0
  git diff "$mb" "$sha" 2>/dev/null | git patch-id --stable 2>/dev/null | cut -d' ' -f1
}

# state | commit reviewed | first line of the body. Reviews come back oldest-first, so the last
# row carrying a verdict is the one in force.
verdict=""; vsha=""; vid=""; rounds=0
while IFS=$'\t' read -r state sha vrid first; do
  # Lenient by design: markdown emphasis, casing and a trailing period are all things a reviewer
  # writes without thinking. A gate that reds on "**Verdict:** Approve." teaches people to
  # distrust it, and a distrusted gate gets worked around rather than fixed.
  norm=$(printf '%s' "$first" | tr -d '\r*_`' | tr '[:upper:]' '[:lower:]' \
           | sed 's/[[:space:]]\{1,\}/ /g; s/^ //; s/ $//; s/\.$//')
  # Request-changes is tested FIRST: "verdict: request changes" must never be mistaken for an
  # approval by a looser pattern. Trailing text after the verdict word is allowed -- reviewers
  # write "Verdict: Approve -- nice catch on the lock" and mean it.
  case "$norm" in
    "verdict: request changes"*|"verdict: requests changes"*|"verdict: changes requested"*)
      verdict="CHANGES-REQUESTED"; vsha="$sha"; vid="$vrid"; rounds=$(( rounds + 1 )) ;;
    "verdict: approve"*)
      verdict="APPROVED";          vsha="$sha"; vid="$vrid" ;;
    *)
      # A native review state still counts where the identity allows one.
      [ "$state" = "APPROVED" ]          && { verdict="APPROVED";          vsha="$sha"; vid="$vrid"; }
      [ "$state" = "CHANGES_REQUESTED" ] && { verdict="CHANGES-REQUESTED"; vsha="$sha"; vid="$vrid"; rounds=$(( rounds + 1 )); }
      ;;
  esac
done < <(gh api "repos/$REPO/pulls/$PR/reviews" --paginate \
           --jq '.[] | [.state, .commit_id, .id, ((.body // "") | split("\n")[0])] | @tsv')

answer() { printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$rounds" "$vid" "$3"; exit 0; }

if [ "$verdict" = "CHANGES-REQUESTED" ]; then
  answer CHANGES-REQUESTED "$vsha" "changes were requested on ${vsha:0:7}; only a push can resolve it"
fi

if [ "$verdict" = "APPROVED" ]; then
  if [ "$vsha" = "$HEAD_SHA" ]; then
    answer APPROVED "$vsha" "approved at head ${HEAD_SHA:0:7}"
  fi

  # An approval binds to WHAT WAS APPROVED. But `protect-develop` is strict, so every merge into
  # $BASE_REF forces open PRs to update, and pinning to the SHA alone would invalidate every
  # approval on every unrelated merge.
  #
  # COMPARING TREES DOES NOT FIX THAT, which is what the first cut got wrong: a sync merge always
  # changes the tree, so the escape hatch never fired and gh#782, gh#787 and gh#791 each sat
  # through the full 30-minute wait on an approval that was still perfectly good.
  #
  # The question is not "did the tree change" but "did the PR's OWN CONTRIBUTION change". Diff each
  # commit against its own merge base and compare patch ids: a sync merge leaves that patch
  # identical, while anything the author actually decided changes it. That deliberately INCLUDES a
  # conflict RESOLUTION made while merging -- resolving a conflict is a human edit the reviewer
  # never saw, so it costs the approval exactly as a fresh commit would.
  approved_patch=$(pr_patch_id "$vsha")
  head_patch=$(pr_patch_id "$HEAD_SHA")

  if [ -n "$head_patch" ] && [ "$head_patch" = "$approved_patch" ]; then
    answer APPROVED "$vsha" \
      "approved at ${vsha:0:7}; head ${HEAD_SHA:0:7} carries the identical contribution (patch ${head_patch:0:12}) -- the only change since is a merge from $BASE_REF"
  fi

  # Fallback for the one case git cannot answer locally: a rebase can orphan the approved commit so
  # it is fetchable from no ref, while the API still knows its tree. An identical tree there is the
  # same proof of "history moved, content did not".
  if [ -z "$approved_patch" ]; then
    head_tree=$(gh api "repos/$REPO/commits/$HEAD_SHA" --jq .commit.tree.sha 2>/dev/null || echo "")
    rev_tree=$(gh api "repos/$REPO/commits/$vsha"     --jq .commit.tree.sha 2>/dev/null || echo "")
    if [ -n "$head_tree" ] && [ "$head_tree" = "$rev_tree" ]; then
      answer APPROVED "$vsha" "approved at ${vsha:0:7}; head ${HEAD_SHA:0:7} is a rebase with an identical tree"
    fi
  fi

  # A stale approval is not a rejection -- it is "not yet re-reviewed". Both callers keep waiting on
  # it rather than failing: gh#782 is the case where failing instantly gave the reviewer no chance
  # to re-rule after the branch was updated for `strict`.
  answer STALE "$vsha" "an approval on ${vsha:0:7} is stale (head ${HEAD_SHA:0:7}, the contribution changed)"
fi

answer NONE "" "no verdict yet"
