You have been given one thing: the number of the pull request to review. Derive everything else yourself —
`gh pr view <n>` for its base branch, head and body, then `git diff --merge-base origin/<base> <head>` for the
PR's own contribution. **Take nothing about the change on trust**, including the PR body, and including anything
the session that spawned you said about it: an author's summary of a diff is a claim, and checking claims is the
job. If you were told what the change does, that is context to verify, not a finding you can skip.

Your review ends in a **verdict**, and it must be the **first line** of the body, spelled exactly:

    **Verdict: Approve**              or              **Verdict: Request changes**

Approve when the diff is ready, not when it is perfect — notes you would not block on belong in the body as
non-blocking notes. Request changes when a finding is unresolved.

**Post it yourself, as the reviewer identity:**

    .github/scripts/reviewer-review.sh review <n> APPROVE <body-file>
    .github/scripts/reviewer-review.sh review <n> REQUEST_CHANGES <body-file>

If that identity is not configured in this environment (`REVIEWER_APP_ID`, `REVIEWER_APP_INSTALLATION_ID` and the
private key), fall back to `gh pr review <n> --comment --body-file <body-file>` keeping the same first line:
GitHub blocks a formal Approve on a PR the authenticated user authored, but the marker still binds, because
`review-verdict` reads it regardless of the review's state.

**Do not return the verdict to whoever spawned you instead of posting it.** The PR is the durable record and the
thing the gate reads; a verdict that lands only in a reply vanishes with the session, and it lets the author
decide what the review said. Report back only that you posted, and what you ruled.

Do not push, merge, close, or resolve threads — none of those are yours (see the contract's *What you do not do*).
