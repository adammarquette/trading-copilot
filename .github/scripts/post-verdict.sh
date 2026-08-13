#!/usr/bin/env bash
#
# post-verdict.sh — post a review verdict so THE GATE CAN READ IT, or fail loudly having claimed nothing
# (gh#815; PR #818 review).
#
#   post-verdict.sh preflight <pr> [--repo owner/name]
#       Can this session post a verdict at all? Answers before a reviewer spends a review finding out.
#       Creates nothing.
#   post-verdict.sh review <pr> <APPROVE|REQUEST_CHANGES> <body-file> [--repo owner/name]
#       Post the verdict, then PROVE the gate reads it by asking verdict-state.sh what it now sees.
#
# WHY THIS EXISTS
# ---------------
# A verdict only counts inside a review BODY: verdict-state.sh reads `/repos/{repo}/pulls/{n}/reviews`, and the
# `review-verdict` check believes it. Three things a reviewer can do instead look like success and are not:
#
#   * a PR COMMENT (`/issues/{n}/comments`) whose first line is the verdict — a different endpoint entirely, and
#     one this gate has never read;
#   * an INLINE review comment, which does create a review record — with an EMPTY body, so the review exists
#     while the verdict does not;
#   * returning the ruling to whoever spawned the reviewer, where it dies with the session.
#
# All three were observed, not imagined: gh#812, gh#813 and gh#814 each merged with `review-verdict` never
# satisfied, and `verdict-state.sh 813` still answers `NONE` — the reviews on those PRs are comments and empty-
# bodied inline threads. A reviewer that reports "posted" after one of those is worse than one that never ruled,
# because the author's watcher then waits out its deadline for a verdict that is already on the page.
#
# So the two halves of the ruling are not separable and this script refuses to separate them: it posts, and then
# it asks THE GATE'S OWN SCRIPT what it now sees. Success means the gate agrees. Nothing else exits 0.
#
# IDENTITY IS THE WHOLE DIFFICULTY
# --------------------------------
# Creating a review takes `pull_requests: write` (GitHub says so itself, in `X-Accepted-GitHub-Permissions` on
# the 403). Two identities have it here, in preference order:
#
#   1. the reviewer GitHub App (`REVIEWER_APP_ID` + `REVIEWER_APP_INSTALLATION_ID` + a key) — not the author, so
#      it can submit a FORMAL Approve / Request-changes that needs no marker at all (gh#141);
#   2. whatever `gh` is authenticated as, if it holds that permission — GitHub blocks a formal state on a PR the
#      authenticated user authored, so the review goes up as `COMMENT` and the first-line marker carries it.
#
# A Cursor cloud-agent session has NEITHER: its installation token is refused on `/pulls/{n}/reviews` with
# `HTTP 403: Resource not accessible by integration`. That is not a bug to route around — it is the reason this
# script exists to say so. Provisioning is gh#811 + the runbook's *Agent review identity*; the operator's
# options are the App secrets or a token with `pull_requests: write`.
#
# Exit status is the interface, as with scripts/watch-verdict.sh:
#
#   0  posted, and verdict-state.sh confirms the gate reads it (a STALE approval counts — see below)
#   3  NOTHING WAS POSTED: no identity here can create a review. Do not report a verdict. Hand the body to the
#      operator, who provisions an identity (gh#811) and posts it.
#   4  posted, but the gate does not read the verdict expected — the body's first line, most likely. The review
#      is on the PR; fix the body and post again rather than assuming it landed.
#   64 bad usage; nothing was attempted.
#
# Read-only on the working tree, and it never merges, closes, pushes or resolves anything: the one write it
# makes is the review itself.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERDICT_STATE="${SCRIPT_DIR}/verdict-state.sh"
REVIEWER_REVIEW="${SCRIPT_DIR}/reviewer-review.sh"

die()  { printf 'post-verdict: %s\n' "$*" >&2; exit 64; }
note() { printf '%s\n' "$*"; }
loud() { printf '%s\n' "$*" >&2; }

# gh api with MSYS path conversion disabled, and endpoints written WITHOUT a leading slash. Both, because
# either alone is enough and this script must not be the one that gets it wrong: under Git Bash a `/repos/...`
# argument is rewritten into a Windows filesystem path before gh ever sees it, so the request goes nowhere.
# The failure is quiet and points the wrong way -- `preflight` would report no identity, and the fallback
# would refuse to post, on a host holding perfectly good `pull_requests: write` (PR #818 review). The sibling
# reviewer-review.sh carries the same wrapper for the same reason; verdict-state.sh omits the slash. Scoped to
# gh rather than exported: other tools want conversion left on.
ghapi() { MSYS_NO_PATHCONV=1 gh api "$@"; }

CMD="${1:-}"
case "$CMD" in
  preflight|review) ;;
  ""|-h|--help|help)
    grep '^#' "$0" | sed 's/^# \{0,1\}//'
    exit 0 ;;
  *) die "unknown command '$CMD' -- try: preflight <pr> | review <pr> <APPROVE|REQUEST_CHANGES> <body-file>" ;;
esac

PR="${2:-}"
[ -n "$PR" ] || die "usage: post-verdict.sh $CMD <pr> ..."
[[ "$PR" =~ ^[0-9]+$ ]] || die "the PR must be a number, got '$PR'"
shift 2

EVENT=""
BODY_FILE=""
if [ "$CMD" = "review" ]; then
  EVENT="${1:-}"; BODY_FILE="${2:-}"
  case "$EVENT" in
    APPROVE|REQUEST_CHANGES) ;;
    *) die "the state must be APPROVE or REQUEST_CHANGES (got '${EVENT}') -- a reviewer that has no verdict has not finished reviewing" ;;
  esac
  [ -n "$BODY_FILE" ] || die "usage: post-verdict.sh review <pr> <APPROVE|REQUEST_CHANGES> <body-file>"
  shift 2
fi

REPO="${REPO:-}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --repo) REPO="${2:?--repo needs owner/name}"; shift 2 ;;
    *)      die "unknown option '$1'" ;;
  esac
done

command -v gh >/dev/null || die "gh not found"
[ -r "$VERDICT_STATE" ] || die "cannot find verdict-state.sh next to this script"

[ -n "$REPO" ] || REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null)
[ -n "$REPO" ] || die "could not determine the repository -- pass --repo owner/name"

PR_URL="https://github.com/${REPO}/pull/${PR}"

# The App is usable only with all of an id, an installation id, and a key.
have_app() {
  [ -n "${REVIEWER_APP_ID:-}" ] && [ -n "${REVIEWER_APP_INSTALLATION_ID:-}" ] \
    && { [ -n "${REVIEWER_APP_PRIVATE_KEY_FILE:-}" ] || [ -n "${REVIEWER_APP_PRIVATE_KEY:-}" ]; }
}

# Can `gh`'s own identity create a review? Asked with a payload GitHub cannot accept, so the answer costs nothing:
#
#   403  the permission is missing                    -> no
#   422  the permission is there, the payload is not   -> yes
#
# Authorization precedes validation, which is what makes the two distinguishable. If that ever inverted, this
# would read a permitted identity as refused -- the safe direction, and the real post below fails loudly anyway,
# so this probe is an early warning rather than the guarantee.
#
# THE INVALID PART IS THE `event` VALUE, not the body, and that is the whole care taken here (PR #818 review).
# The first cut sent `REQUEST_CHANGES` with an empty body, relying on "an empty body is invalid" -- an assumption
# about GitHub's validation that cannot be tested from a session the probe exists to detect. Had it ever been
# accepted, the probe would have posted an empty-bodied changes-requested review, which the gate reads as
# CHANGES-REQUESTED by native state: a preflight that REDS THE PR IT WAS ASKED ABOUT. No enum value makes
# `VERDICT_PREFLIGHT_PROBE` a review, so the worst case now is the API ignoring `event` and drafting a PENDING
# review -- which carries no verdict, so the gate passes over it. The body says what it is, for the same reason.
gh_can_review() {
  local status
  status=$(ghapi "repos/${REPO}/pulls/${PR}/reviews" -X POST \
             -f event=VERDICT_PREFLIGHT_PROBE \
             -f body="post-verdict.sh preflight probe -- deliberately invalid, expected to be rejected." \
             --include 2>&1 \
             | sed -n 's|^HTTP/[0-9.]* \([0-9]\{3\}\).*|\1|p' | head -1)
  [ "$status" = "422" ]
}

# ---------------------------------------------------------------------------------------------------------
# preflight
# ---------------------------------------------------------------------------------------------------------
if [ "$CMD" = "preflight" ]; then
  if have_app; then
    if [ -x "$REVIEWER_REVIEW" ] || [ -r "$REVIEWER_REVIEW" ]; then
      if out=$(bash "$REVIEWER_REVIEW" verify 2>&1); then
        note "The reviewer App is configured and its token mints: ${out}"
        note "A verdict will be posted as a FORMAL review state, which binds with or without the marker."
        exit 0
      fi
      loud "The App secrets are set but the token does not mint, so the App identity is NOT usable:"
      loud "  ${out}"
    fi
  fi

  if gh_can_review; then
    note "No reviewer App here, but this \`gh\` identity may create reviews on ${PR_URL}."
    note "A verdict will go up as a COMMENT review whose FIRST LINE is the marker (GitHub blocks a formal"
    note "state on a PR the authenticated user authored, gh#141)."
    exit 0
  fi

  loud "NO IDENTITY HERE CAN POST A VERDICT on ${PR_URL}."
  loud ""
  loud "Creating a review needs \`pull_requests: write\`. This session has neither the reviewer App"
  loud "(REVIEWER_APP_ID / REVIEWER_APP_INSTALLATION_ID / key) nor a \`gh\` token holding that permission."
  loud ""
  loud "DO NOT REVIEW AND THEN IMPROVISE. A PR comment, or an inline comment, creates no readable verdict:"
  loud "the gate reads review BODIES, so the author's watcher would sit out its full deadline while your"
  loud "ruling is visible on the page. Tell whoever spawned you that no identity is available, hand over the"
  loud "verdict and the body, and let the operator post it (provisioning: gh#811, and the deployment"
  loud "runbook's *Agent review identity*)."
  exit 3
fi

# ---------------------------------------------------------------------------------------------------------
# review
# ---------------------------------------------------------------------------------------------------------
[ -f "$BODY_FILE" ] || die "body file not found: $BODY_FILE"
[ -s "$BODY_FILE" ] || die "the body file is empty: $BODY_FILE"

# The reader is LENIENT on purpose -- verdict-state.sh forgives emphasis, casing, a trailing period -- because a
# gate that reds on "**Verdict:** Approve." teaches people to distrust it. A WRITER has no such excuse, and
# being stricter than the reader is safe by construction: everything accepted here the reader also accepts.
# Trailing text is allowed, because reviewers write "**Verdict: Approve** -- nice catch on the lock" and mean it.
first_line=$(head -1 "$BODY_FILE" | tr -d '\r')
case "$EVENT" in
  APPROVE)         want_marker='**Verdict: Approve**';         want_state="APPROVED" ;;
  REQUEST_CHANGES) want_marker='**Verdict: Request changes**'; want_state="CHANGES-REQUESTED" ;;
esac
if [ "${first_line#"$want_marker"}" = "$first_line" ]; then
  loud "The body's FIRST LINE must be exactly '${want_marker}' (trailing text after it is fine)."
  loud "It is currently: ${first_line:-(empty)}"
  loud "Nothing was posted. The marker IS the verdict for every identity but the App, so a body without it"
  loud "would leave the gate with nothing to read (documentation/agents/code-reviewer.md)."
  exit 64
fi

posted=""
if have_app; then
  if posted=$(bash "$REVIEWER_REVIEW" review "$PR" "$EVENT" "$BODY_FILE" 2>&1); then
    note "Posted as the reviewer App: ${posted}"
  else
    loud "The reviewer App is configured but posting failed:"
    loud "  ${posted}"
    loud "Falling back to this session's own \`gh\` identity."
    posted=""
  fi
fi

if [ -z "$posted" ]; then
  # COMMENT, not the event: GitHub refuses a formal Approve / Request-changes on a PR the authenticated user
  # authored (gh#141), and in this repo the author usually IS the `gh` identity. The state is cosmetic anyway --
  # what binds is the first line, which was checked above.
  body=$(cat "$BODY_FILE")
  if posted=$(ghapi "repos/${REPO}/pulls/${PR}/reviews" -X POST \
                -f event=COMMENT -f body="$body" \
                --jq '"review " + (.id|tostring) + " posted as " + .user.login + " -- state " + .state' 2>&1); then
    note "$posted"
  else
    loud "COULD NOT POST A REVIEW on ${PR_URL} -- NOTHING WAS POSTED and there is no verdict:"
    loud "  ${posted}"
    loud ""
    loud "This is the \`pull_requests: write\` requirement, not a transient failure: creating a review takes"
    loud "an identity that has it -- the reviewer App, or a \`gh\` token holding it. Report that you could NOT"
    loud "rule, hand over the verdict and the body you wrote, and let the operator post it (gh#811)."
    loud "Do not substitute a PR comment or an inline comment: the gate does not read either, so the author"
    loud "would wait out its whole deadline next to a verdict it cannot see."
    exit 3
  fi
fi

# ---------------------------------------------------------------------------------------------------------
# Prove it. Asking the GATE'S script -- not a second reading of our own -- is the point: "posted" and "the gate
# can read it" are different claims, and only the second one is the job.
# ---------------------------------------------------------------------------------------------------------
state=""; detail=""
if line=$(bash "$VERDICT_STATE" "$PR" --repo "$REPO"); then
  IFS='|' read -r state _ _ _ detail <<<"$line"
fi

if [ "$state" = "$want_state" ]; then
  note "The gate agrees: ${detail:-$state}."
  exit 0
fi

# An approval whose head moved while the review was being written is not a posting failure: the verdict is on
# the record and readable, it simply does not bind to what is there now. The author's watcher answers that with
# its own exit 3 (re-spawn), so this reports success -- the reviewer's job did not go wrong.
if [ "$want_state" = "APPROVED" ] && [ "$state" = "STALE" ]; then
  note "Posted and readable, but the head moved while you were reviewing: ${detail}."
  note "Nothing to redo -- the author is told to spawn a fresh review for the new head (engineering §10)."
  exit 0
fi

loud "POSTED, BUT THE GATE DOES NOT READ THE VERDICT: verdict-state.sh answers '${state:-unreadable}'"
loud "(${detail:-no detail}) where '${want_state}' was expected -- ${PR_URL}"
loud ""
loud "The review is on the PR, so do not report a ruling. The usual cause is a first line the gate cannot"
loud "parse; fix the body and post again. Do not leave it here: the author's watcher will wait out its full"
loud "deadline and then wake the operator (scripts/watch-verdict.sh, engineering §10)."
exit 4
