# AGENTS.md — Coding Agent (`src/`)

The **Coding Agent** contract, governing all production code and its unit tests under `src/`. Takes precedence
over the root `AGENTS.md` for this subtree (root rules still apply unless overridden here). The **QA Agent** owns
the integration/smoke tests separately — see
[`MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md`](MarqSpec.TradingCopilot.IntegrationTests/AGENTS.md).

## Role
Write **production code** and the **unit tests** that drive it. You do **not** write integration or smoke tests —
QA does that *independently*, so intent and implementation are verified separately.

## Test-first (mandatory)
- Write the **failing unit test before** the implementation (red → green → refactor). No new public method
  without a failing test written first; bug fixes are regression-first.
- Unit tests go in **`MarqSpec.TradingCopilot.UnitTests`** — one folder per product project mirroring the namespace,
  **every public method covered**, fully mocked with FakeItEasy (no I/O / DB / network), whole suite runs in
  seconds. Name: `MethodUnderTest_Should{ExpectedBehavior}_When{condition}`. (Engineering guide §5.)

## Standards (engineering §2–§4)
**.NET 10 (LTS), C# latest.**
File-scoped namespaces, nullable on, warnings-as-errors, immutability by default, **DI through the constructor**,
async-all-the-way with `CancellationToken`, structured logging via `ILogger`, exhaustive switches, domain
primitives at boundaries. **Money / prices `decimal`, tick-size-aware — never float.** Enforcement lives below
the model; integrate brokers only through the venue abstraction (R-17); data via EF Core + `dotnet ef` migrations.
**Define queries in fluent / method syntax — `.Where(x => …).Select(…)`, never LINQ query-comprehension
(`from … select …`) — everywhere, EF Core included** (authoritative:
[engineering §4](../documentation/trading-platform-engineering.md); background:
[wiki .NET coding conventions](../documentation/wiki/pages/dotnet-coding-conventions.md)).

## Stack & dependencies (engineering §2–§3)
- **Postgres over EF Core** with **TimescaleDB** (time-series — the bulk of the data) and **pgvector** (vectors:
  rulebook + AI-decision/retrieval data); **Cohere** for embeddings + rerank on decision-making / chat retrieval.
- **Data layer:** entities and storage types live in **`MarqSpec.TradingCopilot.Data`**; **EF Core is the
  default** — raw SQL only with a good reason (e.g. perf) — and schema changes go through `dotnet ef` migrations.
- **Dependencies via Central Package Management** (`Directory.Packages.props`); respect license caps (e.g.
  FluentAssertions `[6.12.0,8.0.0)`).

## Definition of done
**Your task ends when the PR you opened is approved and its required checks are green** — not when you push
(canonical: [engineering §10](../documentation/trading-platform-engineering.md), which owns the loop and every
exit status below). So, in the session that wrote the code: `scripts/watch-verdict.sh checks <pr>` → **spawn the
reviewer** → `scripts/watch-verdict.sh verdict <pr>`. Changes requested → the findings are printed for you;
address them here, push, and start again at `checks`. Stale approval → spawn the reviewer again. Approved and
green → take the next card from **Current ToDo**. Nothing in Current ToDo → **alert and pause**; do not invent
work.

What gets you *into* review: failing-test-first now green · every public method covered · standards +
`dotnet format --verify-no-changes` clean · traces to the task's issue and a PRD requirement (`R-#`) · no
secrets · safety-critical paths carry their suites (§5, §9).
