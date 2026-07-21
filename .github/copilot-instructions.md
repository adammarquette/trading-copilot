# Copilot review instructions — Trading Co-Pilot

This is a **self-hosted, single-operator futures day-trading co-pilot**. It places real orders against real
brokerage and prop-firm accounts, and its one autonomous action is a safety-critical **auto-flatten** before the
CME close. A defect here can cost someone an account.

Review accordingly: **a plausible-looking bug on a path that reaches a broker matters more than any number of
style observations.** The full agent contract is in [`AGENTS.md`](../AGENTS.md) and
[`src/AGENTS.md`](../src/AGENTS.md); this file is the review-relevant subset.

## What this codebase treats as a defect

### Fail-open is the defect that keeps recurring

Anything that decides whether an order reaches a broker must **admit only the known-good cases** and refuse
everything else. This exact pattern has been found three separate times in one PR series — an `enum` switch that
named the bad cases and let the rest through, so an unrecognized value, a cast, a deserialized number, or a
member added later became authorization by default.

Flag any switch or conditional on this path that is a blacklist rather than a whitelist. Enums are not closed:
`(SomeEnum)99` is reachable, public constructors and injectable interfaces make it reachable in practice, and
"we'd add a case when we add a value" is not enforcement.

Related: an enum whose **zero value is the permissive one** is a defect. Uninitialised state must fail closed.

### An authorization must describe what is actually sent

The risk gate authorizes a specific account, instrument, quantity and venue. Flag anything that lets the thing
evaluated differ from the thing transmitted — a risk snapshot that doesn't identify its account, an opaque venue
handle not bound to the instrument it was resolved for, a venue-qualified id not checked against the executor
that will receive it. Bare handles collide across brokers: `9001` is a real, different account at every one.

The size transmitted must be the size the gate **approved**, never the size requested.

### Enforcement belongs below the caller, and below the model

- Safety inputs must not be caller-supplied. A deployment environment passed per-call rather than injected at
  composition lets a caller name its own environment and walk a live account through the R-14 guard.
- Risk limits are enforced in deterministic code (`Domain/Risk/`), never held in prompt text. The LLM proposes;
  the gate decides. Flag any limit that depends on a model honouring an instruction.
- Broker integration goes through the venue abstraction (R-17). Venue-specific behaviour belongs in the adapter
  (`Integration.ProjectX`), never scattered through the core.

### Money and time

- Money, prices and quantities are `decimal`, tick-size aware. **Never `float`/`double`.**
- Hard risk limits are measured at the **safety stop**, not the working stop.
- Session logic is wall-clock Central and DST-aware; timestamps are `timestamptz` UTC.

## Repository conventions

- **.NET 10, C# latest.** Nullable on, warnings-as-errors, file-scoped namespaces, XML docs on public members,
  immutability by default, constructor DI, async-all-the-way with `CancellationToken`.
- **Queries in fluent/method syntax** — `.Where(x => …).Select(…)`. LINQ query-comprehension (`from … select …`)
  is not used anywhere, EF Core included.
- **Structured logging** via `ILogger` — no string interpolation into log messages.
- **No secrets in source.** Options pattern plus environment; broker credentials server-side only. Flag any
  credential, account identifier, or token appearing in code, tests, fixtures or committed config.
- Dependencies go through Central Package Management (`Directory.Packages.props`).

## Tests

Test-first is the definition of done. Every public product method needs unit coverage in
`MarqSpec.TradingCopilot.UnitTests` — xUnit + FakeItEasy + FluentAssertions, fully mocked, folders mirroring the
namespace, named `MethodUnderTest_Should{ExpectedBehavior}_When{condition}`.

Worth flagging specifically:

- A new guard or refusal path with no test asserting **the venue is never called**.
- A new branch in price or quantity selection with no test asserting the **exact** values transmitted.
- A fake that doesn't configure the identity a new guard now reads — it will pass or fail for the wrong reason.

## Documentation is part of the change

Any change whose behaviour, data model, API or UX a document describes must update that document **in the same
PR** — the PRD (`R-#`), `documentation/trading-platform-architecture.md`, the data dictionary **and its ERD**,
the wireframes, the ADRs. A PR that changes behaviour and touches no documentation is very likely incomplete;
say so.

Accepted ADRs are **immutable** — superseded by a later ADR, never edited.

Also flag documentation that has become false: a comment or doc describing a limitation that the same PR just
fixed, an XML doc still advertising an obsolete contract, or a doc claiming an enforcement that no longer exists
in the code. A stale safety claim is worse than no claim.

## Weighting

Prioritise, in order: **correctness on order/risk/flatten paths** → **fail-open and unchecked-input holes** →
**missing tests on safety paths** → **stale or overclaiming documentation** → everything else.

Do not report formatting; `dotnet format` is enforced in CI. Prefer a small number of well-evidenced findings
with a concrete failure scenario over broad observations.
