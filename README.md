# Trading Co-Pilot

An **open-source futures day-trading co-pilot** — a decision-support *and* execution system with a
human in the loop. It ingests market data, order flow, news, and social signals; generates fully specified trade
suggestions (direction, entry, stop, targets, size) with cited reasoning; lets the trader execute *through* the
system so intent and outcome are captured natively; and journals every suggestion and trade to close the learning
loop. Its one autonomous action is risk-reducing: **auto-flattening open positions before the CME close.**

**You run your own instance.** Fork it, deploy it, point it at your own broker credentials — broker API keys are
tied to an individual login, so the system is built to be operated by the person whose account it trades. It is
authenticated because it is web-exposed, with data isolation enforced at the data layer — but it is **one operator per deployment**, not a service with a user base.

> ### ⚠️ No warranty. Not financial advice.
> This software places **real orders against a real broker account**. It is provided **as-is, without warranty of
> any kind**. **You alone are responsible** for your trading decisions and their outcomes, for how you configure
> and operate your deployment, and for complying with your broker's and prop firm's terms. Nothing here is
> investment advice or a recommendation to trade. **Prove it on a practice account first** — a defect in a system
> like this can cost you an account.

> **Status: `v0.1.0` (pre-release) — the safety-critical spine is building out.** The **`documentation/` folder
> remains the source of truth** — requirements, architecture, and engineering practices live there, and the code
> is written against them. Landed in `src/` so far: the solution + CI/CD, the data layer + R-20 scoping, auth,
> the **venue seam** (ProjectX adapter), the **enforcing risk gate**, the **gated send path** with the
> arm → edit → take ladder, **staged stops** + their promotion watcher, **conditional entries** + their firing
> watcher, the **append-only event backbone** and its two consumers, **auto-flatten** (primary scheduler +
> redundant watchdog), the **kill switch**, and the recovery layer (orphan handling, settlement reconcile,
> decision-state rehydration). **Not yet built:** the suggestion engine, the LLM/agent layer, soft signals, the
> journal & analytics, and the React SPA / PWA — the whole client. **Nothing has traded live.** Runs locally via
> `docker compose up` (see below). Built with an AI-Engineering-first approach.

---

## Run it locally

Requires Docker. `docker compose up` **pulls** the CI-built image from GHCR (the same artifact Railway runs —
ADR-0018), so a plain clone is enough to *run* it:

```bash
git clone …                        # a recursive clone is only needed to BUILD (see below)
docker compose up -d               # pulls ghcr.io/adammarquette/trading-copilot:develop, starts Postgres + the app
```

> **If the pull fails with `unauthorized` or `not found`:** the GHCR package isn't published-and-public yet (it's
> created on the first merge to `develop`, and is **private until made public** — see the runbook's operator
> setup). Until then, either `docker login ghcr.io` with a token that has `read:packages`, or just **build from
> source** with the dev override below — it needs no registry access at all.

**To run your own changes** (or before the image is public), build from source with the dev override (needs a
**recursive** clone — the ProjectX client is a submodule under `external/` the image builds against):

```bash
git submodule update --init        # if you didn't clone --recursive
docker compose down                # stop the pulled container
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
```

The API comes up on **http://localhost:8080**, applies the EF migrations, and **seeds the operator**
(`operator@local` / `changeme-local` — local-dev defaults; override in `.env`). Onboarding is just sign-in —
**one operator per deployment** (ADR-0017):

**The endpoints document themselves.** The running API generates an **OpenAPI 3 spec** at `/openapi/v1.json` and
a browsable **Scalar reference UI** at `/scalar/v1` (the UI outside production only), straight from the routes —
a reference that **cannot silently drift** from the code (gh#604). Read the surface there, not here.

What the surface is *for*, so you know what to look for:

- **Onboarding** — sign in, register a firm, declare what each funding **stage** means, then connect a venue
  login and discover its accounts. An account's **trading mode is computed** from its stage × the firm's declared
  conventions, never taken from the venue (gh#60).
- **Two ways to send an order.** *Direct* runs the whole ladder and transmits. *Arm → take* stages an editable
  ticket that never reaches the venue, then **re-validates everything fresh** at take — what passed at arm is not
  authority (R-12, ADR-0007). A **conditional** order is the second send mode: held locally, transmitting
  nothing, until a watcher fires it through an authoritative fire-time re-gate (gh#176, gh#198).
- **Risk is declared, and its absence is fail-closed.** Rules are validated whole through the domain factories;
  until declared, the read 404s and the gate refuses — absence is never a default (R-5, gh#10). Every sized
  attempt leaves an auditable `GateDecision`.
- **Positions come from venue truth**, each tagged with its mark basis — live, settlement re-mark, or
  declared-unknown when the venue cannot be reached — never a stale live view (gh#193, R-13).
- **The kill switch** disables every outbound order at the send choke point, cancels working orders, then
  flattens-all or halts-only — and **survives a restart**, so nothing silently re-enables trading (gh#189, R-11).

The `/auth/invitations` and `/auth/accept-invite` endpoints also exist and work, but are **dormant** — not part
of the onboarding story, retained as the plumbing a future read-only / mentee login would reuse (ADR-0017 §4).
Issuing is **primary-operator-only** (gh#128): an accepted invitee cannot chain further invitations.

Config — the DB connection string, `Jwt:SigningKey`, and the bootstrap operator — comes from env / `.env` (see
[`.env.example`](.env.example)); real secrets are never committed (ADR-0012, engineering §8). `docker compose down -v`
tears it down.

---

## For AI agents & new readers — start here

This repo is built to be navigated by AI coding agents as much as by people. Two entry points, in this order:

1. **[`AGENTS.md`](AGENTS.md)** — the rules every agent follows (imported by `CLAUDE.md`, so it loads itself).
   It routes you to your **role contract** — Coding, QA, Code Reviewer, Platform or Coordinator — which holds
   the rules the root file deliberately does not repeat.
2. **[`documentation/README.md`](documentation/README.md)** — the map of the documentation layer: what each
   document is, when to open it, and what it costs to read. **Go through the map; do not sweep the folder.**

Orientation for the three you will reach for most: the **PRD** is *what* the product does (`R-1…R-22`), the
**engineering guide** is *how* we build it, and the **architecture** doc is the *runtime design*. None is a spec
the running system reads — they inform design.

## Where to find things

| Path | What's there |
|---|---|
| [`documentation/`](documentation/) | All specs & design docs — PRD, engineering guide, architecture, [data dictionary](documentation/data-dictionary.md), [ADRs](documentation/adr/), the [deployment runbook](documentation/deployment-runbook.md), [agent contracts](documentation/agents/), [wireframes](documentation/design/), and the [`wiki/`](documentation/wiki/) companion knowledge base (design-time domain knowledge — venue APIs, prop-firm rules, market sessions; not read by the product) |
| `src/` | .NET solution (`MarqSpec.TradingCopilot.slnx`, base namespace `MarqSpec.TradingCopilot.*`) — projects build out under `src/` per the roadmap (`Domain`, `Data`, the `Api` BFF, the `Integration.ProjectX` venue adapter, + test projects so far); naming per engineering guide §3 |
| `external/` | Vendored submodules, pinned per venue client: [`MarqSpec.Client.ProjectX`](https://github.com/adammarquette/MarqSpec.Client.ProjectX) (the v1 adapter builds against it) and [`MarqSpec.Client.Tradovate`](https://github.com/adammarquette/MarqSpec.Client.Tradovate) (requirements only so far — carried for reference, nothing builds against it yet). Outside `src/` so this solution's build settings aren't imposed on them |
| `AGENTS.md` · `CLAUDE.md` | Orientation for AI coding agents (root); `CLAUDE.md` is a shim that imports `AGENTS.md` |
| [`documentation/AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) | Agents' catch-all — practices and cross-agent notes that don't fit any formal document |

## Built to be swapped — the component seams

The architecture is deliberately component-driven: every external dependency sits behind an interface you can
implement yourself. Fork it, write an adapter, and the rest of the system doesn't change.

| Seam | Contract | State |
|---|---|---|
| **Trading venue** (R-17) | `ITradingVenue` — composed of `IMarketDataSource` · `IAccountSource` · `IOrderExecutor` | **built**; ProjectX/TopstepX adapter shipped, Tradovate is the next adapter |
| **Data-only provider** | `IMarketDataSource` alone — no accounts, no execution | **built**; the slice a quotes/news source implements |
| **Risk enforcement** (R-5) | `IRiskGate` → `GateDecision` | **built**; layered limits, most-restrictive wins |
| **LLM provider** | `ILlmProvider` | planned — one provider behind a seam, never prompt-enforced limits |
| **Event log** (ADR-0001) | `IEventLog` | **built**; append-only Timescale hypertable behind the seam, two cursor-tracked consumers on it (stop promotion gh#153, conditional firing gh#198) — a future bus is an adapter change |
| **Venue connection liveness** (R-17) | `IVenueConnection` | **built**; what the orphan guard watches to know the venue dropped (gh#209) |

Two properties fall out of the decomposition. A **data-only source implements just the market-data slice**, so
adding equities context alongside futures is an adapter rather than a second pipeline. And **venue capabilities
are explicit** — an adapter declares what it actually supports, so a missing capability fails loudly at the seam
instead of surfacing mid-execution.

The discipline that keeps the seams honest: **enforcement lives below the model.** The LLM proposes; a
deterministic risk gate decides. No limit is ever held in prompt text
([engineering §6](documentation/trading-platform-engineering.md), [ADR-0007](documentation/adr/0007-order-execution-model.md)).

## Stack (planned)

- **C# / .NET 10 (LTS)** — nullable on, warnings-as-errors, file-scoped namespaces, Central Package Management.
- **ASP.NET Core BFF + SignalR** — real-time chat, plus pushing order/fill/position state and flatten warnings.
- **React SPA** frontend — consumes REST + SignalR (websockets); **JWT**-authenticated API, **per-user authorization** (RBAC-capable), installable as a PWA.
- **Postgres** over EF Core — **TimescaleDB** (time-series), **pgvector** (vectors), relational; **Cohere** embeddings + rerank for decision-making/chat retrieval.
- **Refit** typed clients (`MarqSpec.Client.ProjectX`, TradingView, feeds) + **Polly** resilience.
- **OpenTelemetry → Prometheus / Loki / Tempo / Grafana** (metrics / logs / traces) for observability.
- One LLM provider behind an `ILlmProvider` seam.

See the engineering guide for the full, evolving picture.

## How we work — AI-Engineering first

Software here is built with AI agents as first-class engineering participants.

**The documentation is a layer of the software, not commentary on it.** Machine code abstracts the hardware;
assembler abstracts machine code; C# abstracts both — and this repository treats its documentation — the
`documentation/` markdown **and the GitHub issues and PRs** — as the next layer up: **the highest-level source
code of the system**. Like any source layer, it is written to be *compiled downward*: from the PRD's
requirements (`R-#`), the ADRs, the data dictionary, and the issue/PR trail, the C# below is
**reconstructable** — by an agent or a human — the way a compiler reconstructs machine code from source. The
repo's documentation rules are consequences of that principle, not housekeeping:

- The docs form a **cross-referenced knowledge base** (Andrej Karpathy's
  [LLM Wiki](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f) pattern), traversed via stable
  IDs — `R-#`, ADR numbers, `gh#N` — which are the layer's **symbol table**. Agents follow them to reconstruct
  context each session instead of re-deriving it.
- **Docs move in the same PR** as the change they describe — **the affected section of the affected document**.
  A stale doc is a **build break in the top layer** — the source no longer produces the system — not a
  housekeeping lapse.
- **Issues and PRs are part of the source, not exhaust.** Issue-first; every PR cites its issue; decisions,
  evidence, and findings are recorded on them where the next session — human or agent — recompiles from.
- A session **resolves the top layer on demand**: this README, then the map at
  [`documentation/README.md`](documentation/README.md), then the one section it actually needs.
  **Reconstructable does not mean read-it-all** — a compiler resolves the symbols it needs rather than reading
  every source file, and `R-#` / ADR numbers / `gh#N` are precisely that symbol table. `documentation/` is
  ~240K tokens; sweeping it is not the compile direction, it is loading the whole object graph to link one
  symbol. **Route, don't sweep.**

The engineering practice that follows from it:

- **Test-first**, with heightened rigor and change control on the execution / auto-flatten layer; bug fixes are
  **regression-first**.
- **Conventional Commits**; AI-authored changes carry **both** an `Assisted-by:` and a `Co-Authored-By:` trailer.
- **Contributing:** branch off `develop`, name branches **`<type>/<work-item-id>_<title>`**, curate commits into units of work and land by **rebase-merge** (squash retired) — see [`CONTRIBUTING.md`](CONTRIBUTING.md).

(*Enforcement below the model* and *issue-first* are stated once each above — the seams section and the
documentation-layer bullets respectively — rather than repeated here.)

## Contributing & governance

Contributions are welcome — issues, adapters for other venues, fixes. See [`CONTRIBUTING.md`](CONTRIBUTING.md)
for the branching model, commit conventions, and the test-first Definition of Done.

**Direction is maintainer-led.** Adam Marquette is the final authority on scope, architecture, and what merges.
Contributions are reviewed on their merits and on fit with the direction here; there is no obligation to accept
any change, and a declined PR is not a judgement on its quality. If you want the project to go somewhere it
isn't going, **fork it** — the licence exists so you can, and a fork is a legitimate outcome rather than a
failure. Safety-critical areas (the risk gate, execution, auto-flatten, the kill switch) carry a higher bar:
expect design discussion before implementation.

## Related projects

- **`MarqSpec.Client.ProjectX`** — the ProjectX / TopstepX client used for market data, account state, and order
  execution.

## License

Copyright © 2026 Adam Marquette. Licensed under the **Apache License 2.0** — see [`LICENSE`](LICENSE) and
[`NOTICE`](NOTICE).

### An AI-first engineering project

**The great majority of this codebase was written by AI coding agents** — source, tests, and documentation —
working under human direction, review, and acceptance. Comparatively little was hand-written.

That is stated plainly because it is the honest description of the work, and because *how* it was built is part
of what it demonstrates: the requirements traceability (`R-#`), the ADR trail, the test-first discipline, and the
cross-referenced knowledge base exist so that agents can reconstruct context and build against a specification
rather than a vibe. Judge it on the architecture, the test suites, and whether the safety-critical paths hold.

One practical consequence: copyright in AI-generated work is unsettled, so the strength of any claim here — and
with it the enforceability of any licence — is uncertain. A permissive licence asks little, which makes the
question largely academic. It would not be under a restrictive one.

---

*Self-hosted decision-support and execution tooling — for the operator’s own trading, not investment advice for
third parties.*
