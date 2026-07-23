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

> **Status: `v0.1.0` (pre-release) — early / scaffolding.** The **`documentation/` folder is the current source of truth** — the product
> requirements and engineering practices live there. The `src/` foundation is building out (solution + CI, data layer + tenancy, auth) and **runs locally via `docker compose up`** (see below). Built
> with an AI-Engineering-first approach.

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

```
POST /auth/login              {email, password}          -> JWT
GET  /auth/me                 (Bearer)                   -> the operator
POST /firms                   {name, type}   (Bearer)    -> register a firm (gh#76)
GET  /firms                   (Bearer)                   -> the operator's firms + conventions
PUT  /firms/{id}/conventions  [{stage, capitalAtRisk}]   -> declare what each stage means (gh#60)
POST /connections             {firmId, platform, credentialKey} -> a firm login (one per firm x platform)
GET  /connections             (Bearer)                   -> the operator's connections
POST /connections/{id}/accounts/discover                 -> pull the venue's roster; each account's mode is
                                                            COMPUTED from its stage x the firm's declared
                                                            conventions, never taken from the venue (gh#60)
GET  /connections/{id}/accounts                          -> the persisted roster (no venue round-trip)
PUT  /accounts/{id}/stage     {stage}                    -> declare an account's stage, overriding the
                                                            conservative resolver (it never guesses; you do
                                                            the telling). Unknown is refused
DEL  /accounts/{id}/stage                                -> clear the override, fall back to the resolved stage
POST /accounts/{id}/orders    {the proposal}             -> send ONE order through the whole ladder (gh#11):
                                                            declared risk rules (refused when absent), the
                                                            R-14 mode x environment gate, the R-5/R-16 risk
                                                            gate -- every sized attempt leaves an auditable
                                                            GateDecision; a placed one journals the Order.
                                                            Requires a FLAT account (honest unrealized P&L=0)
POST /accounts/{id}/orders/arm {the proposal}             -> ARM: run the whole ladder but STAGE, not send --
                                                            an editable ticket, never at the venue (ADR-0007)
PUT  /orders/{id}             {the proposal}              -> EDIT a staged ticket -- re-gates on every change
POST /orders/{id}/take                                    -> TAKE: re-validate EVERYTHING fresh (R-12), then
                                                            transmit. Fails-and-stays-staged if the fresh gate
                                                            now blocks -- what passed at arm is not authority
DEL  /orders/{id}                                         -> cancel a staged ticket (before it is taken)
PUT  /accounts/{id}/risk      {the whole declaration}    -> declare the account's risk rules (R-5); validated
                                                            through the domain factories, refused whole on any
                                                            violation (gh#10)
GET  /accounts/{id}/risk                                 -> the declared rules; 404 until declared -- absence
                                                            is the gate's fail-closed input, never a default
```

The `/auth/invitations` and `/auth/accept-invite` endpoints also exist and work, but are **dormant** — not part
of the onboarding story, retained as the plumbing a future read-only / mentee login would reuse (ADR-0017 §4).
Issuing is **primary-operator-only** (gh#128): an accepted invitee cannot chain further invitations.

Config — the DB connection string, `Jwt:SigningKey`, and the bootstrap operator — comes from env / `.env` (see
[`.env.example`](.env.example)); real secrets are never committed (ADR-0012, engineering §8). `docker compose down -v`
tears it down.

---

## For AI agents & new readers — start here

This repo is built to be navigated by AI coding agents as much as by people. Read in this order:

| Doc | What's there |
|---|---|
| [`documentation/trading-platform-prd.md`](documentation/trading-platform-prd.md) | **Product requirements (PRD)** — problem, goals, the `R-1…R-21` requirements, success metrics, phasing, open questions |
| [`documentation/trading-platform-engineering.md`](documentation/trading-platform-engineering.md) | **Engineering guide** — architecture patterns, a lightweight engineering-practices scaffold (stack, testing, observability, deployment, safety-critical discipline), and the companion knowledge wiki |
| [`documentation/trading-platform-architecture.md`](documentation/trading-platform-architecture.md) | **System architecture** — the runtime view: services, event pipeline, data flow, and open design decisions |
| [`AGENTS.md`](AGENTS.md) | Instructions for AI coding agents (imported by `CLAUDE.md`) — settled conventions and where to find things |

The PRD is *what* the product does; the engineering guide is *how* we build it; the architecture doc is the
*runtime design*. None is a spec the running system reads — they inform design.

## Where to find things

| Path | What's there |
|---|---|
| [`documentation/`](documentation/) | All specs & design docs — the substance today (PRD + engineering guide; a `wiki/` companion knowledge base to come) |
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
| **Event log** (ADR-0001) | `IEventLog` | planned — Timescale today, swappable later |

Two properties fall out of the decomposition. A **data-only source implements just the market-data slice**, so
adding equities context alongside futures is an adapter rather than a second pipeline. And **venue capabilities
are explicit** — an adapter declares what it actually supports, so a missing capability fails loudly at the seam
instead of surfacing mid-execution.

The discipline that keeps the seams honest: **enforcement lives below the model.** The LLM proposes; a
deterministic risk gate decides. No limit is ever held in prompt text.

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
- **Docs move in the same PR** as the change they describe. A stale doc is a **build break in the top layer** —
  the source no longer produces the system — not a housekeeping lapse.
- **Issues and PRs are part of the source, not exhaust.** Issue-first; every PR cites its issue; decisions,
  evidence, and findings are recorded on them where the next session — human or agent — recompiles from.
- A session **starts by reading the top layer** (this README, then `documentation/`) and derives the rest —
  which is exactly the compile direction.

The engineering practice that follows from it:

- **Test-first**, with heightened rigor and change control on the execution / auto-flatten layer.
- **Enforcement below the model** — the risk/execution gate enforces limits; the LLM only proposes.
- **Conventional Commits**; add an `Assisted-by:` trailer for AI-authored changes.
- **Work tracked in GitHub issues & PRs** — issue-first, no orphaned PRs.
- **Contributing:** branch off `develop`, name branches **`<type>/<work-item-id>_<title>`**, curate commits into units of work and land by **rebase-merge** (squash retired) — see [`CONTRIBUTING.md`](CONTRIBUTING.md).

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
