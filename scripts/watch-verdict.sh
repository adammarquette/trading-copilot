#!/usr/bin/env bash
#
# watch-verdict.sh — how an author agent WAITS on the PR it opened, without ending its turn (gh#815).
#
#   scripts/watch-verdict.sh checks  <pr>    block until the PR's checks conclude
#   scripts/watch-verdict.sh verdict <pr>    block until a review verdict binds to the current head
#
# WHY THIS EXISTS
# ---------------
# A task is not done when the branch is pushed; it is done when the PR is approved and green (engineering §10).
# That rule had no mechanism: the contracts said "pause and monitor" and named no command, so in practice a
# session ended at the push and the review landed in an empty room. When the feedback was finally picked up it
# was by a DIFFERENT session, which had to rebuild from the diff everything the author already knew.
#
# So the wait belongs in the authoring session, and it has to be a blocking call rather than a habit. The exit
# STATUS is the whole interface -- each one maps to exactly one next action, so the loop can be followed
# without re-reading the PR:
#
#   0  approved and green      -> the card is Done; take the next one from Current ToDo
#   1  changes requested       -> the findings are printed above; fix them, push, and start again at `checks`
#   2  timed out               -> alert the operator and pause. What timed out depends on the phase: in
#                                `verdict` nobody ruled (was a reviewer spawned, and COULD it post one?); in
#                                `checks` the checks never concluded; in EITHER phase, check state itself may
#                                have been unreadable for --max-unreadable attempts running (GraphQL AND the
#                                REST fallback both failed, gh#1040) -- that is an API problem, not a stalled
#                                PR, and is reported as such well before the deadline. Each phase prints which
#                                of these it was.
#   3  approval is stale       -> the contribution changed since it was approved; SPAWN THE REVIEWER AGAIN
#   4  a check failed          -> fix CI first; nobody is asked to rule on a diff that does not build
#   5  too many rounds         -> --max-rounds reached; stop looping and escalate to the operator
#   64 bad usage               -> this script was called wrong; nothing was observed
#
# On `1` it prints the review body and the reviewer's inline comments. That printing is the point of running
# the wait here rather than in CI: the fix then happens in the session that still holds the context of why
# the code looks the way it does.
#
# WHAT IT DOES NOT DO
# -------------------
# It does not review, spawn the reviewer, push, or merge. Its ONE write is the `verdict:watching` label it
# raises for the life of its wait and drops on the way out (gh#1028); everything else it touches is a read.
# That write is not optional to provision: the identity this runs under needs `pull_requests: write`, and
# without it every wait is invisible to a coordinator -- a label failure is a warning on stderr, never a hard
# stop, so a read-only token degrades QUIETLY to the pre-gh#1028 behaviour. See *The watching signal* below.
# Who renders the verdict, and the independence rules that keep a reviewer spawned by the author honest, are
# the [reviewer contract](documentation/agents/code-reviewer.md)'s; the loop the exit statuses above serve is
# engineering §10's.
#
# `review-verdict` in CI waits for the same ruling from the same script (.github/scripts/verdict-state.sh) --
# deliberately, so the gate and the author can never disagree about whether a PR is approved.
#
# The reviewer's side of the same boundary is .github/scripts/post-verdict.sh: it posts the verdict and proves
# the gate reads it. Worth knowing here for one reason -- a verdict can be perfectly visible on the PR and still
# invisible to this wait, if it went up as a PR comment rather than in a review body. That is what `2` most
# often means (PR #818 review), which is why the timeout below says so before blaming the reviewer.

set -uo pipefail

# Resolved relative to THIS FILE, not the working directory: an agent runs this from wherever its session
# happens to sit -- a worktree, the main checkout, a subdirectory -- and the ruling must come from the copy of
# verdict-state.sh that ships with this copy of the script, never whichever one a relative path finds.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERDICT_STATE="${SCRIPT_DIR}/../.github/scripts/verdict-state.sh"

die() { printf 'watch-verdict: %s\n' "$*" >&2; exit 64; }

CMD="${1:-}"
PR="${2:-}"
case "$CMD" in
  checks|verdict) ;;
  ""|-h|--help|help)
    grep '^#' "$0" | sed 's/^# \{0,1\}//'
    exit 0 ;;
  *) die "unknown command '$CMD' -- try: checks <pr> | verdict <pr>" ;;
esac
[ -n "$PR" ] || die "usage: watch-verdict.sh $CMD <pr> [options]"
[[ "$PR" =~ ^[0-9]+$ ]] || die "the PR must be a number, got '$PR'"
shift 2

# Defaults sized to this repo: CI runs about 15 minutes (integration tests on a real-Postgres container plus
# the image build), and a spawned reviewer rules in a few minutes. The verdict deadline sits ABOVE the
# 30 minutes `review-verdict` itself waits, so the author does not give up before the gate does.
DEADLINE_MINUTES=45
POLL_SECONDS=60
MAX_ROUNDS=3
# How many CONSECUTIVE times read_checks() may come back unable to read check state -- GraphQL failed AND the
# REST fallback failed too -- before this gives up loudly instead of running out the full deadline blind
# (gh#1040). GitHub's secondary/burst limit clears in seconds, so a handful of polls is plenty of room for a
# transient blip to pass; past that it is worth alerting on rather than waiting out 45 minutes silently.
MAX_UNREADABLE=3
REPO="${REPO:-}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --deadline-min)   DEADLINE_MINUTES="${2:?--deadline-min needs a number}"; shift 2 ;;
    --poll-seconds)   POLL_SECONDS="${2:?--poll-seconds needs a number}";     shift 2 ;;
    --max-rounds)     MAX_ROUNDS="${2:?--max-rounds needs a number}";        shift 2 ;;
    --max-unreadable) MAX_UNREADABLE="${2:?--max-unreadable needs a number}"; shift 2 ;;
    --repo)           REPO="${2:?--repo needs owner/name}";                  shift 2 ;;
    *)                die "unknown option '$1'" ;;
  esac
done

command -v gh >/dev/null || die "gh not found"
[ -x "$VERDICT_STATE" ] || [ -r "$VERDICT_STATE" ] || die "cannot find .github/scripts/verdict-state.sh next to this script"

[ -n "$REPO" ] || REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null)
[ -n "$REPO" ] || die "could not determine the repository -- pass --repo owner/name"

PR_URL="https://github.com/${REPO}/pull/${PR}"
deadline=$(( $(date +%s) + DEADLINE_MINUTES * 60 ))
attempt=0

# ---------------------------------------------------------------------------------------------------------
# The watching signal
# ---------------------------------------------------------------------------------------------------------
# This wait is a LOCAL blocking call: nothing outside the authoring session can see that it is running. That
# invisibility used to leave the coordinator guessing, because its contract said to launch a reviewer only when
# "no author is running the watch-verdict loop" -- a condition with no observable form. Both guesses are wrong:
# assume an author is watching and a dead author's PR waits forever; assume none and you race the author's own
# reviewer on the same head (gh#1028).
#
# So the wait announces itself, and the coordinator reads the label instead of guessing. BOTH phases label,
# not just `verdict`: the coordinator launches a reviewer whenever the label is absent, so labelling only the
# `verdict` phase would leave the whole `checks` wait -- about 15 minutes -- reading as "nobody is watching".
# The reviewer it launched there would then race the one the author spawns the moment `checks` returns 0, which
# is the split verdict this signal exists to prevent. The label has to go up BEFORE the action it serializes.
#
# The trap covers EVERY exit, not only the clean one, because a stuck `verdict:watching` is worse than no label
# at all -- it suppresses reviewers indefinitely.
#
# The hazard here is NOT that an EXIT trap misses a signal. Bash runs it on a fatal signal (`termsig_handler`
# calls the exit trap before re-raising), verified on 5.3 under both Cygwin and Linux: a script whose only trap
# is EXIT, killed with TERM, still clears and still reports 143. The round-1 bug was the opposite shape -- INT
# and TERM were trapped to a handler that CLEARED THE LABEL AND RETURNED, and a returning handler hands control
# back to the poll loop. The label came off while the wait ran on, which is the "nobody is watching" state the
# label exists to deny, arrived at from the direction the design never considered.
#
# So the signal traps exit rather than clean up: cleanup belongs to EXIT alone, and every path reaches it once.
# They are defensive rather than load-bearing -- delete one and the label still clears, because EXIT still runs
# -- and the gate that matters is `scripts/tests/watch-verdict.test.sh`, which reds on the clear-and-return
# shape rather than on a missing trap. Keep them; do not mistake them for the thing being guarded.
#
# The label spans BOTH waits as one, and the seam between them is the whole reason. `checks` and `verdict` are
# separate processes, and the author spawns the reviewer BETWEEN them (engineering §10's ordering) -- so a
# `checks` that cleared the label on its way out would go dark for exactly the step the signal exists to
# serialize, and a coordinator polling there would launch the second reviewer. A green `checks` therefore hands
# the label ON rather than clearing it, and the `verdict` process inherits it. Every other way out of `checks`
# -- red, timeout, signal -- clears it, because those end the wait rather than continuing it.
#
# That hand-off is what makes a STUCK label reachable: nothing clears it if the author dies in the seam. It is
# bounded rather than prevented, and the bound is the coordinator's, not this script's -- `verdict:watching`
# older than the deadlines here is stale and may be taken over, which the coordinator contract states and
# gives the command for (gh#1028). A signal this script cannot clear from inside its own grave is why.
WATCH_LABEL="verdict:watching"
watching_applied=""
# Set only on the one exit that is a hand-off, never a finish: `checks` going green into the reviewer spawn.
hand_off=""
# Set when this process could not apply the label but an earlier phase may have handed one on (see below).
maybe_inherited=""

clear_watching() {
  # `maybe_inherited` is the hand-off's loose end: when `verdict`'s re-apply fails, this process never applied
  # the label but `checks` may well have left one up, and no-oping would strand it for the whole shelf life.
  # So it still tries -- quietly, since there may be nothing there and a warning would be the wrong story.
  if [ -z "$watching_applied" ]; then
    [ -n "$maybe_inherited" ] || return 0
    maybe_inherited=""
    gh pr edit "$PR" --repo "$REPO" --remove-label "$WATCH_LABEL" >/dev/null 2>&1 || true
    return 0
  fi
  [ -z "$hand_off" ] || return 0
  watching_applied=""
  # Warned about rather than swallowed. The apply path degrades loudly for the same reason this one must: a
  # label that fails to come OFF is the worse of the two failures, because it suppresses reviewers for as long
  # as it sits there, and nothing else will ever mention it. The warning cannot be a non-zero return -- this
  # runs from the EXIT trap, where a failing command would overwrite the exit status the caller reads.
  gh pr edit "$PR" --repo "$REPO" --remove-label "$WATCH_LABEL" >/dev/null 2>&1 || \
    printf 'watch-verdict: could not remove %s from %s -- clear it by hand (gh#1028)\n' \
      "$WATCH_LABEL" "$PR_URL" >&2
}

# Created on demand so a fresh clone needs no setup step; a no-op once it exists.
gh label create "$WATCH_LABEL" --repo "$REPO" --color BFD4F2 \
  --description "An author is blocked on watch-verdict.sh for this PR" >/dev/null 2>&1 || true
if gh pr edit "$PR" --repo "$REPO" --add-label "$WATCH_LABEL" >/dev/null 2>&1; then
  watching_applied=1
  trap clear_watching EXIT
  # 128 + the signal number, the status a shell killed by that signal reports.
  trap 'exit 130' INT
  trap 'exit 143' TERM
  trap 'exit 129' HUP
else
  # Degrade loudly rather than failing: the wait still works, it is just invisible to a coordinator, which
  # is the pre-gh#1028 behaviour and no worse than it.
  printf 'watch-verdict: could not apply %s -- this wait is invisible to a coordinator (gh#1028)\n' \
    "$WATCH_LABEL" >&2
  # ...except in `verdict`, where "invisible" may be the wrong story entirely: a green `checks` hands the label
  # ON, so a failure here can leave a label this process did not apply and would otherwise never remove. Say
  # which of the two it is, and take the traps anyway so the EXIT path gets its chance to clear it.
  if [ "$CMD" = "verdict" ]; then
    printf '  (if `checks` handed one on, it is still up and this process will try to clear it on exit)\n' >&2
    maybe_inherited=1
    trap clear_watching EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP
  fi
fi

# ---------------------------------------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------------------------------------
# `review-verdict` is EXCLUDED from every check reading below, and that exclusion is load-bearing: it is the
# job waiting for the very verdict this script's caller has not asked for yet. Counting it would deadlock the
# `checks` phase against the reviewer that the `checks` phase is supposed to run before.
#
# Buckets come from gh: pass | fail | pending | skipping | cancel. `skipping` counts as concluded -- a job
# that legitimately does not run on this PR is not a thing to wait for.
red_checks=""
pending_checks=0
passed_checks=0
# Whether the count above is trustworthy. Cleared, not just left at zero, the moment neither source could
# produce a readable answer -- an unreadable read must never be mistaken for a PR that genuinely has zero
# checks (gh#1040). A caller checks THIS before treating pending_checks==0 && passed_checks==0 as meaningful.
checks_readable=""
# Consecutive times in a row read_checks() has come back unreadable. Reset the moment either source reads
# cleanly; used to bound the wait so a standing API failure is reported within a handful of polls rather than
# the full deadline (see MAX_UNREADABLE above).
unreadable_attempts=0

read_checks() {
  red_checks=""; pending_checks=0; passed_checks=0; checks_readable=1
  local name bucket link out rc

  # Primary source: `gh pr checks`, which is GraphQL. The exit status is now READ rather than swallowed --
  # gh exits non-zero both when a check is genuinely pending/failing (normal, and it still prints the rows)
  # and when the call itself failed, e.g. rate-limited (not normal, and it prints nothing). Those two only
  # look alike when stdout is EMPTY, so that is the one case this falls through to the REST fallback below
  # rather than guessing from the exit code or the error text -- a PR that genuinely has no checks yet will
  # get the same empty answer back from REST, converging on the same "no checks reported yet" state either
  # way, while a rate limit will not (gh#1040).
  out=$(gh pr checks "$PR" --repo "$REPO" --json name,bucket,link \
          --jq '.[] | [.name, .bucket, (.link // "")] | @tsv' 2>/dev/null)
  rc=$?

  if [ -n "$out" ] || [ "$rc" -eq 0 ]; then
    while IFS=$'\t' read -r name bucket link; do
      [ -n "$name" ] || continue
      [ "$name" = "review-verdict" ] && continue
      case "$bucket" in
        fail|cancel) red_checks="${red_checks}    ${name}  (${bucket})  ${link}"$'\n' ;;
        pending)     pending_checks=$(( pending_checks + 1 )) ;;
        *)           passed_checks=$(( passed_checks + 1 )) ;;
      esac
    done <<<"$out"
    unreadable_attempts=0
    return
  fi

  if read_checks_rest; then
    unreadable_attempts=0
    return
  fi

  # Both sources failed to produce a readable answer. This is NOT "zero checks": callers must check
  # checks_readable before treating pending_checks==0 && passed_checks==0 as a legitimate empty state.
  checks_readable=""
  unreadable_attempts=$(( unreadable_attempts + 1 ))
}

# The fallback that stayed healthy through a full day of parallel sessions sharing one GraphQL pool (gh#1040):
# `gh api` hits REST directly, bypassing whatever pool `gh pr checks` draws from. Populates the same
# red_checks / pending_checks / passed_checks globals `read_checks` does; returns 1 (leave them alone) only
# when REST itself could not answer, so a caller can tell "REST says zero" from "REST could not say."
#
# Reads BOTH REST surfaces GraphQL's statusCheckRollup merges, not just the Checks API. The Checks API
# (check-runs) and the classic commit-status API (still used by third-party integrations that predate it) are
# two SEPARATE sets -- a fallback that read only check-runs would silently narrow to a partial one, and could
# then call a PR "green" while a still-pending or still-failing legacy status sat unread (review finding 1 on
# PR #1046, gh#1040). So either source failing to answer fails the WHOLE read: "I read a partial set" must
# never render as "there is nothing pending."
read_checks_rest() {
  local sha out name status conclusion link="${PR_URL}/checks"

  sha=$(gh api "repos/$REPO/pulls/$PR" --jq '.head.sha' 2>/dev/null)
  [ -n "$sha" ] || return 1

  out=$(gh api "repos/$REPO/commits/$sha/check-runs" --paginate \
          --jq '.check_runs[] | [.name, .status, (.conclusion // "")] | @tsv' 2>/dev/null) || return 1

  while IFS=$'\t' read -r name status conclusion; do
    [ -n "$name" ] || continue
    [ "$name" = "review-verdict" ] && continue
    if [ "$status" != "completed" ]; then
      pending_checks=$(( pending_checks + 1 ))
      continue
    fi
    case "$conclusion" in
      success|neutral|skipped)                        passed_checks=$(( passed_checks + 1 )) ;;
      failure|timed_out|action_required|cancelled)
        red_checks="${red_checks}    ${name}  (${conclusion})  ${link}"$'\n' ;;
      *)                                               pending_checks=$(( pending_checks + 1 )) ;;
    esac
  done <<<"$out"

  out=$(gh api "repos/$REPO/commits/$sha/status" \
          --jq '.statuses[] | [.context, .state] | @tsv' 2>/dev/null) || return 1

  while IFS=$'\t' read -r name status; do
    [ -n "$name" ] || continue
    [ "$name" = "review-verdict" ] && continue
    case "$status" in
      success)          passed_checks=$(( passed_checks + 1 )) ;;
      failure|error)
        red_checks="${red_checks}    ${name}  (${status})  ${link}"$'\n' ;;
      *)                pending_checks=$(( pending_checks + 1 )) ;;  # pending, or anything unrecognized
    esac
  done <<<"$out"
  return 0
}

# Prints the standing bail-out message and returns 0 once --max-unreadable consecutive reads have failed,
# so a caller can `if unreadable_bailout; then exit 2; fi` instead of running out its full deadline on an
# API problem that a bounded number of retries could not clear (gh#1040).
unreadable_bailout() {
  [ "$unreadable_attempts" -ge "$MAX_UNREADABLE" ] || return 1
  printf 'Could not read check state after %d attempt(s) -- GraphQL and the REST fallback both failed.\n' \
    "$unreadable_attempts" >&2
  printf 'This is an API problem, not a verdict on the PR -- look at %s/checks directly before waiting\n' \
    "$PR_URL" >&2
  printf 'any longer. `gh api rate_limit --jq .resources` does not itself consume quota and shows what,\n' >&2
  printf 'if anything, is actually exhausted (a secondary/burst limit clears in seconds, not at reset).\n' >&2
  return 0
}

# Prints the failures and returns 0 when there are any, so callers can `if report_red; then exit 4; fi`.
report_red() {
  [ -n "$red_checks" ] || return 1
  printf 'A check on %s failed:\n\n%s\n' "$PR_URL" "$red_checks"
  printf 'Fix it and push. Nobody is asked to rule on a diff that does not build, so the reviewer is not\n'
  printf 'spawned until this is green (engineering §10).\n'
  return 0
}

if [ "$CMD" = "checks" ]; then
  while :; do
    attempt=$(( attempt + 1 ))
    read_checks

    if [ -z "$checks_readable" ]; then
      if unreadable_bailout; then exit 2; fi
      state="could not read check state (attempt $unreadable_attempts of $MAX_UNREADABLE) -- retrying"
    else
      if report_red; then exit 4; fi

      if [ "$pending_checks" -eq 0 ] && [ "$passed_checks" -gt 0 ]; then
        printf '%d check(s) green on %s.\n' "$passed_checks" "$PR_URL"
        printf 'Spawn the reviewer now, then wait on it: scripts/watch-verdict.sh verdict %s\n' "$PR"
        # Handed on, not finished: the label stays up across the reviewer spawn, and `verdict` inherits it.
        # The operator who stops here is the one who has to take it down, so say it on the terminal rather
        # than only in this comment -- they will never read this line, and a claim nobody clears sits for
        # its shelf life.
        printf '%s stays on the PR across that spawn -- if you stop here instead, take it down:\n' "$WATCH_LABEL"
        printf '  gh pr edit %s --remove-label %s\n' "$PR" "$WATCH_LABEL"
        hand_off=1
        exit 0
      fi

      # No checks at all yet is the normal state for the first seconds after a push, so it waits rather than
      # concluding "green" on an empty set -- which would send the reviewer at an untested diff. Reaching
      # here means checks_readable is set, so this genuinely IS empty, not an unreadable state in disguise.
      if [ "$pending_checks" -eq 0 ]; then
        state="no checks reported yet"
      else
        state="$pending_checks pending, $passed_checks green"
      fi
    fi

    now=$(date +%s)
    if [ "$now" -ge "$deadline" ]; then
      printf 'Timed out after %sm waiting for checks to conclude -- %s.\n' "$DEADLINE_MINUTES" "$state" >&2
      printf 'Look at %s/checks before deciding anything.\n' "$PR_URL" >&2
      exit 2
    fi

    printf 'attempt %d: %s -- waiting %ss (%dm left)\n' \
      "$attempt" "$state" "$POLL_SECONDS" "$(( (deadline - now) / 60 ))"
    sleep "$POLL_SECONDS"
  done
fi

# ---------------------------------------------------------------------------------------------------------
# Verdict
# ---------------------------------------------------------------------------------------------------------
# Everything the FIX needs, printed where the author can act on it: the verdict body, then the reviewer's
# inline comments -- narrowed to the review that ruled, so round three does not reprint rounds one and two.
print_feedback() {
  local review_id="${1:-0}" body comments

  body=$(gh api "repos/$REPO/pulls/$PR/reviews" --paginate \
    --jq "map(select(.id == ${review_id})) | last | .body // empty" 2>/dev/null)

  printf '\n--- the verdict -------------------------------------------------------------\n'
  printf '%s\n' "${body:-(the review body could not be read; read it at $PR_URL)}"

  # Narrowed to the review that ruled, so a third round does not reprint the first two.
  comments=$(gh api "repos/$REPO/pulls/$PR/comments" --paginate \
    --jq "[.[] | select(.pull_request_review_id == ${review_id})]
          | .[] | \"\(.path):\(.line // .original_line // 0)  (\(.user.login))\n\(.body)\n\"" 2>/dev/null)

  if [ -n "$comments" ]; then
    printf '\n--- inline findings ---------------------------------------------------------\n'
    printf '%s\n' "$comments"
  fi

  printf -- '-----------------------------------------------------------------------------\n'
  printf 'Address these, push, and start again at: scripts/watch-verdict.sh checks %s\n' "$PR"
  printf 'Resolve the threads yourself once addressed -- the reviewer does not resolve its own (%s).\n' "$PR_URL"
}

while :; do
  attempt=$(( attempt + 1 ))

  # No --head: unlike CI, which must rule on the commit its run tested, the author wants the ruling on
  # whatever it last pushed, re-read every poll so a push mid-wait is picked up rather than ignored.
  verdict=""; vsha=""; rounds=0; vid=""
  detail="The verdict could not be read (transient API failure?)"
  if line=$(bash "$VERDICT_STATE" "$PR" --repo "$REPO"); then
    # `|`, not a tab: a tab is IFS whitespace, so a run of them collapses and the empty fields on the NONE
    # line would shift every later field left (verdict-state.sh's header has the full account).
    IFS='|' read -r verdict vsha rounds vid detail <<<"$line"
  fi
  # $detail arrives sentence-cased and is printed VERBATIM below. It used to be capitalized here with
  # `${detail^}`, which is Bash 4 and therefore a fatal bad substitution under the Bash 3.2 macOS still ships
  # as /bin/bash -- killing the STALE and APPROVED paths before their exit statuses, which are this script's
  # entire interface (PR #818 review). Sentence casing now lives where the sentence is written.

  case "$verdict" in
    CHANGES-REQUESTED)
      printf 'Changes requested on %s (round %s of %s).\n' "${vsha:0:7}" "$rounds" "$MAX_ROUNDS"
      print_feedback "${vid:-0}"
      if [ "$rounds" -ge "$MAX_ROUNDS" ]; then
        printf '\nThat is %s round(s) on one PR. Stop and tell the operator what the disagreement is instead\n' "$rounds"
        printf 'of pushing again -- past this point the review is usually about the approach, not the diff.\n'
        exit 5
      fi
      exit 1 ;;

    STALE)
      printf '%s.\n' "$detail"
      printf 'The approval does not cover what is at the head now, so it is not a rejection -- it is\n'
      printf 'not-yet-reviewed. SPAWN THE REVIEWER AGAIN for the current head; it will rule afresh.\n'
      exit 3 ;;

    APPROVED)
      # Approved is only half of done. The other half is green, and it is re-read here rather than trusted
      # from the earlier `checks` pass: the approval may have arrived minutes after a later push.
      read_checks
      if [ -z "$checks_readable" ]; then
        # This is the exact shape gh#1040 reported: an APPROVED PR whose checks could not be read must never
        # fall through to "waiting on 0 check(s)" -- that renders a read failure as a self-contradicting
        # description of an already-green PR. Say plainly that the read failed, and bail out well before the
        # deadline rather than making the operator wait 45 minutes on an API problem.
        if unreadable_bailout; then exit 2; fi
        state="$detail -- could not read check state (attempt $unreadable_attempts of $MAX_UNREADABLE), retrying"
      else
        if report_red; then exit 4; fi
        if [ "$pending_checks" -eq 0 ] && [ "$passed_checks" -gt 0 ]; then
          printf '%s, and %d check(s) green.\n' "$detail" "$passed_checks"
          printf 'Done: move the card to Done and take the next one from Current ToDo (engineering §10).\n'
          exit 0
        fi
        state="$detail -- waiting on $pending_checks check(s)"
      fi
      ;;

    *)
      state="$detail"
      ;;
  esac

  now=$(date +%s)
  if [ "$now" -ge "$deadline" ]; then
    printf 'Timed out after %sm. %s.\n' "$DEADLINE_MINUTES" "$state" >&2
    printf 'Nobody ruled. Check these two, in this order, before waking the operator:\n' >&2
    printf '  1. COULD the reviewer post a verdict? One only binds inside a review BODY, which takes an\n' >&2
    printf '     identity holding `pull_requests: write` -- the reviewer App, or a token that has it. A\n' >&2
    printf '     session without one can post PR comments, and this gate does not read them, so the review\n' >&2
    printf '     may exist while the verdict does not. `.github/scripts/post-verdict.sh preflight %s`\n' "$PR" >&2
    printf '     answers it in one call (gh#811).\n' >&2
    printf '  2. Was a reviewer spawned at all, and did it post to %s?\n' "$PR_URL" >&2
    printf 'Then alert the operator rather than taking another card (engineering §10).\n' >&2
    exit 2
  fi

  printf 'attempt %d: %s -- waiting %ss (%dm left)\n' \
    "$attempt" "$state" "$POLL_SECONDS" "$(( (deadline - now) / 60 ))"
  sleep "$POLL_SECONDS"
done
