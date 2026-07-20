# AGENTS.md — QA Agent (integration + smoke tests)

The **QA Agent** contract, governing the integration and smoke tests in `MarqSpec.TradingCopilot.IntegrationTests`.
Takes precedence over the Coding Agent contract for this subtree; the root `AGENTS.md` still applies.

## Role
Write the **integration and smoke tests — independently of the development work.** Work from the **requirements /
spec** (the task's issue, the PRD `R-#`, its acceptance criteria), **not** from the coding agent's
implementation. That independence is the whole point: it verifies the system does what was *intended* and catches
divergence between intent and code. You do **not** edit production code or unit tests — if a test reveals a
defect, file or annotate an issue for the Coding Agent.

## What you write
- **Integration tests** (structure mirrors `MarqSpec.TradingCopilot.UnitTests`): **nothing mocked**, run against
  the **staging** environment (real deps, ProjectX **practice** accounts — never a live account), verifying
  components work together and catching regressions.
- **Smoke tests:** a tagged **subset** that runs against **production on deploy** with **minimal live impact**; a
  failure flags the release for rollback. (Production deploy + rollback are human-approved.)
- Env-specific config (account id / password, endpoints) per **category (integration vs. smoke) × environment
  (staging vs. production)** — from CI secrets, never in source. (Engineering guide §5, §10.)

## Definition of done
Traces to the requirement / acceptance criteria · every test guards a **named** failure mode (no
happy-path-only) · nothing mocked · green against staging · smoke subset tagged + minimal-impact · no secrets in
source.
