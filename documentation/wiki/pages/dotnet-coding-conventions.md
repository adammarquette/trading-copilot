# .NET / C# coding conventions (baseline)

> **Trust tier:** authoritative (Microsoft Learn). **Verified:** WebFetch 2026-07-19.
> **Sources:** https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions
> (the canonical C# conventions, adopted from the dotnet/runtime + Roslyn styles) ·
> https://learn.microsoft.com/en-us/previous-versions/mixed-reality/world-locking-tools/documentation/howtos/codingconventions
> (a project style guide — **Unity-oriented**; we take only its *universal* C# parts).
> **Access:** Microsoft Learn pages fetched directly (no auth wall). The conventions doc's source repo
> (`dotnet/docs`) is **CC-BY-4.0** (LICENSE verified 2026-07-22) — this page is an attributed summary with
> canonical links, not a copy; the linked `dotnet/runtime` style guide is MIT.
> **Informs:** engineering §4 (Coding Standards — the authoritative home), root + `src/` `AGENTS.md`. This page is
> the fuller reference behind them.

We **adopt Microsoft's C# coding conventions as the baseline**, with one firm project deviation (below). Engineering
§4 states the load-bearing rules + our deltas and points here.

## The one deliberate deviation — queries use *fluent* syntax, not query-comprehension
- **Use LINQ _method / fluent_ syntax:** `orders.Where(o => o.IsOpen).OrderBy(o => o.PlacedAt).Select(o => o.Id)`.
- **Do NOT use LINQ _query-comprehension_ syntax:** *not* `from o in orders where o.IsOpen select o.Id`.
- This **overrides the Microsoft doc**, whose "LINQ queries" section *shows query-comprehension syntax* — we don't
  use it. Operator's call: comprehension syntax "pollutes" the code; fluent chaining reads and composes better.
- Applies **everywhere queries are defined** — **EF Core** (`_db.Orders.Where(...).Select(...)`), LINQ-to-Objects,
  and any `IQueryable` provider.

## Baseline (Microsoft) — the load-bearing rules
**Naming**
- `PascalCase` for types, methods, properties, events, constants, namespaces, and public / protected members.
- `camelCase` for locals + parameters; `_camelCase` for private instance fields (runtime / Roslyn style).
- `I`-prefix interfaces (`IRiskGate`); descriptive `T`-prefix type parameters (`TResult`).

**Types & language**
- Language keywords over runtime types (`string` / `int`, not `String` / `Int32`); prefer `int` over unsigned.
- `var` **only when the type is obvious** from the right-hand side (a `new`, a cast, a literal); otherwise name the
  type. Exception: LINQ range / result variables (anonymous or nested generics) use `var`.
- Immutability: `record` / `required` / `init`; prefer **`required` properties over constructors** to force init.
- `Func<>` / `Action<>` over custom delegate types.
- Target-typed `new()` when the variable type is named; **object / collection initializers**.
- **Collection expressions** `[ … ]` to initialize collections.
- **Raw string literals** `"""…"""` over escaped / verbatim; **string interpolation** `$"…"` over concatenation;
  `StringBuilder` in loops.
- `&&` / `||` (short-circuit) rather than `&` / `|` in comparisons.
- `using` **declarations** for disposables; `using` **directives outside** the namespace (fully-qualified, stable).
- Catch **specific** exception types — never bare `System.Exception` without a filter.

**Layout**
- **File-scoped namespaces** (`namespace X;`) — a build **error** if violated (our stricter rule).
- **Allman braces** (each on its own line), **always braces** even for a single statement; one statement / one
  declaration per line; four-space indent, spaces not tabs; a blank line between members; a space after
  `if` / `for` / `while`.
- **XML doc comments** on the public surface; `//` comments on their own line, sentence-case, ending in a period.

**From the Unity style guide — universal parts we also keep**
- **One public type per file** (a `class` / `struct` / `enum` in its own file; nested types may be `private`).
- **Encapsulation:** private field + public property, **co-located**; always declare an access modifier.
- **Enums:** put the default (`= 0`) **first** so indexes stay stable as values are added; `[Flags]` with `1 << n`
  for bitfields.
- **`DateTime.UtcNow`, never `DateTime.Now`** — perf + correctness (matches our UTC-internal rule).
- *(Excluded as Unity-specific / N/A to a .NET backend: `for` over `foreach`, `[SerializeField]`, material caching,
  `#if` platform compilation, license-header banners.)*

## Enforcement
Analyzers + the **checked-in root [`.editorconfig`](../../../.editorconfig)** (encodes these rules), **`dotnet format --verify-no-changes`**
in CI, and **warnings-as-errors** — style drift **fails the build, not review** (engineering §4, §10). The
fluent-over-LINQ rule is expressed as an analyzer / editorconfig rule where one exists, otherwise a review check.

## Relevant-link index
- Microsoft — C# Coding Conventions — https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions
- .NET Runtime — C# coding style (source of the above) — https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md
- World Locking Tools — coding guidelines (Unity-oriented) — https://learn.microsoft.com/en-us/previous-versions/mixed-reality/world-locking-tools/documentation/howtos/codingconventions
