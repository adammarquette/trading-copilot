#!/usr/bin/env bash
#
# reviewer-outcome.sh (gh#994) -- classify the advisory `reviewer` workflow's CLI outcome so an Anthropic
# API / provisioning failure the PR author CANNOT fix degrades to a clean SKIP with a loud notice, instead
# of reddening every PR. reviewer.yml already takes exactly this posture when ANTHROPIC_API_KEY is
# unprovisioned (gh#811); an API-layer refusal is the same "not the diff" class and is treated the same.
# A genuine reviewer / invocation bug still reddens, so a real regression stays visible.
#
# WHY (gh#994): once ANTHROPIC_API_KEY was provisioned but its account had no credit, `claude ...
# --output-format json` exited 1 on EVERY PR with an API-layer rejection --
#   {"terminal_reason":"api_error","api_error_status":400,"result":"Credit balance is too low", ...}
# -- a 400 that has nothing to do with the diff, at very different diff sizes. reviewer.yml's own rule
# (its header) is that a red check nobody can fix trains people to ignore reds; the missing-key path
# already skips clean, and an API error (no credit, 401, 429, 5xx, overloaded) is the same class.
#
#   classify_review_outcome <rc> <result-json-file>   ->  echoes exactly one of: ok | infra | fail
#     ok    : rc == 0 -- the reviewer ran and produced a review body to post.
#     infra : rc != 0 AND the CLI reported an Anthropic API error (terminal_reason == "api_error", or a
#             populated numeric api_error_status) -- a backend / provisioning refusal the author cannot
#             fix. The caller skips clean (exit 0) with a loud ::warning:: naming the cause.
#     fail  : rc != 0 with no such API-error signal -- a real reviewer / workflow / invocation bug, or a
#             crash that wrote no parseable result. The caller reddens (exit rc).
#
# Deliberately dependency-free -- it matches the CLI's own single-line result JSON with bash glob patterns
# rather than jq, so it runs in ANY bash (CI's ubuntu and a bare Git Bash / WSL local shell alike),
# exactly like the repo's other hermetic shell-lib tests. It only defines a function and sets no shell
# options, so it is safe to `source` from the workflow step and from the test alike. The api_error match
# keys on the top-level KEY:VALUE pair, not the bare word, so an "api_error" mention inside a review's
# text (only ever read on the rc==0 ok-path anyway) can never be mistaken for the terminal reason.

classify_review_outcome() {
    local rc="${1:?classify_review_outcome: missing <rc>}"
    local json="${2:?classify_review_outcome: missing <result-json-file>}"

    # rc 0 is the only success signal the CLI gives us: a review body exists to post. The body is not
    # inspected here, so a review that happens to quote "api_error" is never misread.
    if [ "$rc" -eq 0 ]; then
        printf 'ok\n'
        return 0
    fi

    # rc != 0. Read the CLI's own result JSON, if it left one. A crash that wrote no JSON (an empty or
    # unreadable file) leaves body empty and correctly falls through to `fail` -- a review the CLI could
    # not even structure is a genuine failure, not a clean API refusal.
    local body=""
    [ -s "$json" ] && body="$(cat "$json" 2>/dev/null || printf '')"

    # An Anthropic API error (no credit, bad key, rate limit, overloaded, 5xx) is not the diff and not
    # something this PR can fix -- degrade to a clean skip, exactly as the unprovisioned path does. Match
    # the top-level terminal_reason == "api_error", or a populated numeric api_error_status, tolerating
    # the compact (key:value) and spaced (key: value) JSON forms.
    case "$body" in
        *'"terminal_reason":"api_error"'* | *'"terminal_reason": "api_error"'*)
            printf 'infra\n'; return 0 ;;
    esac
    case "$body" in
        *'"api_error_status":'[0-9]* | *'"api_error_status": '[0-9]*)
            printf 'infra\n'; return 0 ;;
    esac

    printf 'fail\n'
    return 0
}
