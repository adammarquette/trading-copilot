Review this pull request against the contract you were given, not against the author's account of it.

Read any file you need for context before judging a hunk — a diff alone does not show you the caller, the test,
or the invariant a hunk depends on.

Rank findings by blast radius, most severe first. For each one give the concrete failure it causes and the
`file:line` it happens at. If a category comes up clean, say so plainly in a line rather than padding it with
observations.

Verify the PR body's claims against the diff — a claim the diff does not support is itself a finding.

<!--
This file is the SUBSTANCE of the review and is shared by both reviewers, which is why it says nothing about
how the diff reaches you or what you do with the result. Those differ, and each has its own clause appended
after this one:

  reviewer-prompt-advisory.md   the CI reviewer in .github/workflows/reviewer.yml — diff on stdin, and
                                FORBIDDEN from writing a verdict line
  reviewer-prompt-verdict.md    the reviewer an author agent spawns — derives the diff itself, and its
                                verdict is binding (gh#815)

Split rather than copied: two prompts drifting apart is two different reviews claiming the same contract.
-->
