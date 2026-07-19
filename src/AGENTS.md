# AGENTS.md — Coding Agent (`src/`)

The **Coding Agent** contract, governing all production code and its unit tests under `src/`. Takes precedence
over the root `AGENTS.md` for this subtree (root rules still apply unless overridden here). The **QA Agent** owns
the integration/smoke tests separately — see
[`TradingCopilot/TradingCopilot.IntegrationTests/AGENTS.md`](TradingCopilot/TradingCopilot.IntegrationTests/AGENTS.md).

## Role
Write **production code** and the **unit tests** that drive it. You do **not** write integration or smoke tests —
QA does that *independently*, so intent and implementation are verified separately.

## Test-first (mandatory)
- Write the **failing unit test before** the implementation (red → green → refactor). No new public method
  without a failing test written first; bug fixes are regression-first.
- Unit tests go in **`TradingCopilot.UnitTests`** — one folder per product project mirroring the namespace,
  **every public method covered**, fully mocked with FakeItEasy (no I/O / DB / network), whole suite runs in
  seconds. Name: `MethodUnderTest_Should{ExpectedBehavior}_When{condition}`. (Engineering guide §5.)

## Standards (engineering §2–§4)
File-scoped namespaces, nullable on, warnings-as-errors, immutability by default, **DI through the constructor**,
async-all-the-way with `CancellationToken`, structured logging via `ILogger`, exhaustive switches, domain
primitives at boundaries. **Money / prices `decimal`, tick-size-aware — never float.** Enforcement lives below
the model; integrate brokers only through the venue abstraction (R-17); data via EF Core + `dotnet ef` migrations.
**Define queries in fluent / method syntax — `.Where(x => …).Select(…)`, never LINQ query-comprehension
(`from … select …`) — everywhere, EF Core included** (wiki: [.NET coding conventions](../documentation/wiki/pages/dotnet-coding-conventions.md)).

## Definition of done
Failing-test-first now green · every public method covered · standards + `dotnet format --verify-no-changes`
clean · traces to the task's issue and a PRD requirement (`R-#`) · no secrets · safety-critical paths carry
their suites (§5, §9).
