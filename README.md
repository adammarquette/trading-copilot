# Trading Co-Pilot

A **multi-user futures day-trading co-pilot** — a decision-support *and* execution system with a
human in the loop. It ingests market data, order flow, news, and social signals; generates fully specified trade
suggestions (direction, entry, stop, targets, size) with cited reasoning; lets the trader execute *through* the
system so intent and outcome are captured natively; and journals every suggestion and trade to close the learning
loop. Its one autonomous action is risk-reducing: **auto-flattening open positions before the CME close.**

> **Status: `v0.1.0` (pre-release) — early / scaffolding.** The **`documentation/` folder is the current source of truth** — the product
> requirements and engineering practices live there. The `src/` foundation is building out (solution + CI, data layer + multi-user tenancy, auth) and **runs locally via `docker compose up`** (see below). Built
> with an AI-Engineering-first approach.

---

## Run it locally

Requires Docker. From the repo root:

```bash
docker compose up -d --build      # builds the API image, starts Postgres + the app
```

The API comes up on **http://localhost:8080**, applies the EF migrations, and seeds a first operator
(`operator@local` / `changeme-local` — local-dev defaults; override in `.env`). The invitation-only flow works end
to end:

```
POST /auth/login          {email, password}            -> JWT
POST /auth/invitations    {email}   (Bearer)           -> an invite token (shown once)
POST /auth/accept-invite  {token, password, displayName} -> a new account + JWT
GET  /auth/me             (Bearer)                     -> your user
```

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
| `src/` | .NET solution (`MarqSpec.TradingCopilot.slnx`, base namespace `MarqSpec.TradingCopilot.*`) — projects build out under `src/` per the roadmap (`Domain`, `Data`, the `Api` BFF, + test projects so far); naming per engineering guide §3 |
| `AGENTS.md` · `CLAUDE.md` | Orientation for AI coding agents (root); `CLAUDE.md` is a shim that imports `AGENTS.md` |
| [`documentation/AGENT-MEMORY.md`](documentation/AGENT-MEMORY.md) | Agents' catch-all — practices and cross-agent notes that don't fit any formal document |

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

Software here is built with AI agents as first-class engineering participants. The documentation is a
**cross-referenced knowledge base** (Andrej Karpathy's
[LLM Wiki](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f) pattern) that agents traverse — via
`R-#` requirement IDs and doc-section pointers — to reconstruct context instead of re-deriving it each session.

- **Test-first**, with heightened rigor and change control on the execution / auto-flatten layer.
- **Enforcement below the model** — the risk/execution gate enforces limits; the LLM only proposes.
- **Conventional Commits**; add an `Assisted-by:` trailer for AI-authored changes.
- **Work tracked in GitHub issues & PRs** — issue-first, no orphaned PRs.
- **Contributing:** branch off `develop`, name branches **`<type>/<work-item-id>_<title>`**, rebase/squash before merge — see [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Related projects

- **`MarqSpec.Client.ProjectX`** — the ProjectX / TopstepX client used for market data, account state, and order
  execution.

---

*Multi-user decision-support and execution tooling — for each user's own trading, not investment advice for
third parties.*
