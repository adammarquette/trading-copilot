# Engineering Guide: Personal Futures Trading Co-Pilot

**Companion to:** `trading-platform-prd.md`
**Author:** Adam
**Status:** Draft
**Date:** 2026-07-17

---

This document holds the **engineering practices and development-time resources** for the trading co-pilot — the "how we build it" that was leaking into the product PRD. It covers the architecture patterns the build inherits (§1), a lightweight scaffold of engineering practices — tech stack, testing, observability, deployment, safety-critical discipline, and workflow (§2–§10) — and the companion knowledge wiki the engineer and build agents use while developing (§11). None of it is a product requirement; the PRD remains the source of truth for what the product does.

---

## 1. Development Patterns & Architecture Alignment

This platform reuses the architectural patterns proven in **agent-forge-copilot** (`C:\Users\adamm\source\repos\GauntletAI\agent-forge-copilot`). That project is the reference for how the pieces fit together; the mapping below is intentionally close so the two share muscle memory. (Requirement IDs like R-5/R-12 refer to the PRD.)

| AgentForge pattern | Trading platform application |
|---|---|
| **.NET sidecar / BFF with an agent orchestrator** | Same backend shape: an always-on .NET service hosting the suggestion agent, tool routing, execution gate, and the API the local UI consumes. The UI is a thin client of the BFF. |
| **MCP tools (FHIR tools)** | MCP tools for ProjectX (`MarqSpec.Client.ProjectX`) and TradingView. The agent calls data/analytics/journal/rulebook as tools — same invocation pattern, different domain. |
| **Verification engine → verify-before-*act*** | Strongest parallel, now sharper: AgentForge verifies a clinical suggestion before *presenting* it; here the same discipline verifies a ticket before *sending* it. The R-5 risk gate + R-12 execution-time re-validation + R-16 caps are that verification engine applied to real orders — nothing reaches the broker unverified. |
| **SignalR hub for chat** | Reuse SignalR for multi-turn chat *and* for pushing real-time order/fill state, position updates, proactive alerts, and flatten warnings to the UI. |
| **Railway deployment** | The always-on cloud tier (ingestion, analytics, agent, execution, storage) deploys to Railway. |
| **Self-hosted Prometheus + Grafana (Epic 9)** | Same observability stack — plus execution-specific signals: order-gate pass/block counts, auto-flatten success, time-to-flat, order-ack latency. Auto-flatten reliability must be a monitored, alertable metric. |
| **Secrets server-side; delegated OAuth intact** | ProjectX credentials and API keys live only in the cloud tier's secret storage; the local UI never holds them. |

**Net-new subsystem with no AgentForge analog:** AgentForge does not *take* clinical actions, so the **execution + auto-flatten layer is a from-scratch, safety-critical build** — including the kill switch, the redundant flatten guarantee, and the practice/live mode surface. Design it with the same verify-before-act rigor as AgentForge's verification engine, but expect no existing code to lift directly.

**Intentional divergence:** AgentForge's top-level-launch / same-origin-proxy decisions (#21/#22) solved OpenEMR's SameSite-cookie constraint; this platform's UI is a standalone local client against a remote BFF, so that line doesn't carry over — the local client simply authenticates to the cloud API.

> The table reflects AgentForge's *architecture decisions* as understood, not a read of the current source. Before Phase 1, confirm specifics against the actual repo (project/solution layout, DI and orchestrator wiring, the verification engine's interfaces, MCP tool registration, SignalR hub contracts, Railway/observability config) so the trading platform inherits the real conventions.

---

## Engineering practices — a lightweight scaffold

The sections below (§2–§10) are a **deliberately minimal scaffold**, not a finished standard. Their job is to make the space of engineering decisions *visible* — so nothing important gets silently skipped as we move into architecture — and to record the **defaults we inherit from AgentForge** (§1) so we only reason about the deltas. Content stays terse on purpose; each section deepens as the plan firms up. Conventions used here:

- A plain bullet states a **current default** — mostly inherited from AgentForge, grounded in its actual repo.
- **Decide:** marks an **open choice** we haven't made yet, with a pointer to *when* it needs resolving.
- `R-#` / `Q-#` link to the PRD's requirements and open questions, keeping the two docs cross-referenced.

**Contents:** §2 Tech stack · §3 Solution structure · §4 Coding standards · §5 Testing & verification · §6 Agentic-AI practices · §7 Observability · §8 Deployment & secrets · §9 Safety-critical discipline · §10 Git workflow & Definition of Done · §11 Companion knowledge wiki · §12 Technical spikes.

---

## 2. Tech Stack & Platform

Default posture: **inherit AgentForge's stack** and diverge only with a reason.

- **Runtime/language:** .NET 10 (LTS), C# latest, `Nullable` on, warnings-as-errors — solution-wide via `Directory.Build.props`.
- **Host / API:** ASP.NET Core BFF (the always-on cloud tier); the local UI is a thin client of it.
- **Real-time:** SignalR — reused for chat *and* for pushing order/fill/position state, proactive alerts, and flatten warnings (R-6, R-10, R-11, R-13). Note: AgentForge only pushes in reply to a caller; **server-initiated push** (via `IHubContext`, per-user targeting) is a net-new extension on top of its outbox/resume durability pattern.
- **External HTTP:** Refit typed clients + `Microsoft.Extensions.Http.Resilience` (Polly) handlers — for `MarqSpec.Client.ProjectX`, TradingView, news/social feeds.
- **LLM access:** a hand-built `ILlmProvider` seam (as in AgentForge — Anthropic behind the seam in v1; no off-the-shelf LLM/MCP SDK). **Decide:** confirm provider/model tier.
- **Persistence:** **Postgres** is the datastore — relational (journal, rulebook, account/config) and **vector via pgvector**, over EF Core, reusing AgentForge's data stack. Time-series bars/ticks (R-1) also land in Postgres; **Decide (implementation):** plain Postgres partitioning vs. the TimescaleDB extension, driven by tick volume and the R-9 replay/retention needs (§8, Q-10).
- **Config & secrets:** Options pattern with validate-on-start; secrets from environment/CI only, server-side (§8).
- **Cloud tier:** Railway (R-1 always-on). **Decide:** monthly cost ceiling (Q-10).

## 3. Solution & Repository Structure

- **Default:** mirror AgentForge's conventions — `<Base>.<Area>` projects under `/src`, `/tests`, `/tools`; `.slnx` solution; folder == project == root namespace; `I`-prefixed interfaces; source-generated logging in sibling `*Log.cs`.
- **Base namespace:** **Decide** — the stub is `TradingCopilot`; likely `MarqSpec.TradingCopilot`, for consistency with `MarqSpec.Client.ProjectX`.
- **Dependency management:** Central Package Management (`Directory.Packages.props`) — versions declared once, `.csproj` carries none. Respect license caps (AgentForge pins FluentAssertions `[6.12.0,8.0.0)`).
- **Project sketch (starting point — Decide/refine in architecture):** `.Api` (BFF + SignalR) · `.Agent` (suggestion orchestrator) · `.Tools`/`.Mcp` (ProjectX, TradingView, journal, rulebook as agent tools — an *in-process, contract-validated tool seam*, as in AgentForge; not the MCP wire protocol) · **`.Execution`** (net-new: order gate, execution-time re-validation, sanity caps, kill switch, auto-flatten) · **`.Risk`** (layered risk model) · `.MarketData` (ingestion + time-series) · `.OrderFlow` (tape/footprint/volume-profile analytics) · `.SoftSignals` · `.Journal` · `.Rulebook` · `.Data` · `.Llm` · `.Observability`. The **`.Execution` + auto-flatten layer has no AgentForge analog** (§1, §9).
- **Broker/venue abstraction (architectural goal — PRD R-17).** Market data, account state, and execution sit behind **venue-neutral interfaces**, with `MarqSpec.Client.ProjectX` as the v1 **adapter** — the same "one provider in v1, abstraction preserved" discipline AgentForge uses for `ILlmProvider`. Broker-specific code lives only in a per-venue adapter project (e.g. `.Integration.ProjectX`; later `.Integration.Tradovate`), paralleling AgentForge's `.Integration.OpenEmr`; the engine, risk model, execution gate, journal, and UI depend on the abstractions. Instruments / accounts / orders / fills are **venue-tagged** end-to-end so risk, P&L, and the journal stay correct if accounts span venues, and venue capability gaps are explicit (AgentForge's optional-capability / honest-empty pattern).

## 4. Coding Standards & Conventions

- **Default:** inherit AgentForge wholesale — file-scoped namespaces (enforced as a build *error*), immutability by default (`record`/`required`/`init`, `sealed` by default), **DI through the constructor only** (no static locators, no ambient state), async-all-the-way honoring `CancellationToken`, exhaustive `switch` on enums, `System.Text.Json` source-gen, **structured logging via `ILogger`** (message templates, never interpolation), XML docs on the public surface, terse comments with grep-able `reference:` pointers.
- **Domain primitives at the boundary (parse, don't validate):** `InstrumentId`, `AccountId`, and money/size types — errors here move real contracts.
- **Decide (trading-specific, early):**
  - **Money & prices:** `decimal`, tick-size-aware — **never** binary floating point for prices, sizes, or P&L.
  - **Time:** one canonical representation (UTC internally) with explicit CME/CST session boundaries and the 3:00 PM CST flatten deadline as first-class domain concepts.

## 5. Testing & Verification Strategy

Safety-critical system → testing is a first-class practice, not an afterthought.

- **Tiers (inherit AgentForge):** **Unit** (fully mocked with FakeItEasy, no I/O, fast) · **Integration** (nothing mocked, against real deps in a practice/QA environment) · **Deterministic evals** for agent behavior (§6).
- **Frameworks:** xUnit, FluentAssertions (honor the `[6.12.0,8.0.0)` license cap), FakeItEasy, coverlet.
- **Test-first is the Definition of Done** (AgentForge rule): no new public method without a failing test written first; **bug fixes are regression-first**.
- **Every test guards a *named* failure mode** (boundary / invariant / regression) — happy-path-only suites don't pass review.
- **Safety-critical additions (net-new, highest rigor):**
  - **Deterministic replay:** drive suggestion generation, risk sizing, and execution-time re-validation against *recorded* market data so outcomes are reproducible (also underpins R-9 untaken-suggestion simulation, Q-5).
  - **Per-layer risk tests** (mirroring AgentForge's per-rule verification tests): binding-constraint selection, resize-vs-block, and "no trade" emission for R-5/R-16.
  - **Auto-flatten (R-13):** simulate the close under partial fills, rejects, and connectivity loss at 2:59; assert positions end flat and the **"only reduces exposure"** invariant holds.
  - **Kill switch (R-11):** asserts every outbound order path is disabled and working orders cancelled.
  - **Phase-2 → live gate as tests:** the PRD §9 exit criteria become an executable checklist that must be green before live mode is enabled.

## 6. Agentic-AI Development Practices

- **Golden-set eval gate (adopt AgentForge's standout pattern):** pinned model responses → **deterministic, repo-only, boolean** rubrics → a **PR-blocking gate** with a baseline and a max-regression bound. Trading rubrics (starting set): suggestion is fully specified (direction/entry/stop/target/size), rationale cites *real* signals present in context, computed size is within risk limits, session-clock/flatten deadline respected, no fabricated data.
- **Enforcement lives *below the model*.** AgentForge's load-bearing rule is "authorization is enforced below the model, never by prompt text." Our analog: the **risk gate, re-validation, and sanity caps enforce limits — the LLM only proposes** (R-5/R-12/R-16). One sharpening: AgentForge's verifier *flags but still ships* the answer (a flag is information); our order gate must **hard-block** — a fired risk/validity/flatten rule stops transmission. The `IDomainConstraintRule` seam supports blocking; the clinical use simply never exercises it.
- **The three learning-goal seams stay inspectable and testable:** durable memory = the rulebook (R-7); verify-before-act = the execution gate (R-11/R-12); outcome feedback = the journal → engine loop (R-9).
- **Decide:** LLM-judge rubrics for subjective suggestion quality (AgentForge defers these too — deterministic rubrics first).

## 7. Observability, Monitoring & Alerting

- **Default (inherit):** OpenTelemetry → self-hosted **Prometheus + Grafana + Loki**; a correlation ID on every request and downstream call; child spans per tool/agent step so a full trace reconstructs from one ID.
- **Execution-specific SLIs (net-new):** order-gate pass/block counts, **auto-flatten success and time-to-flat**, order-ack latency, ingestion uptime + gap/backfill events, suggestion and scan-alert latency (the §7 targets in the PRD).
- **Auto-flatten reliability is a monitored, *alertable* metric** — it must page. **Decide:** the alert channel for an on-call-of-one (push/mobile, PRD P1) and thresholds.

## 8. Deployment, Environments & Secrets

- **Default (inherit):** Railway via a multi-stage `Dockerfile` (sdk → aspnet, bind `$PORT`); **config/secrets re-asserted from CI (GitHub Actions) secrets on every deploy** (AgentForge's self-healing "one source of truth" pattern — worth copying, it fixed a class of drift bugs).
- **Environments track branches — dev / staging / prod:** the `develop` → `staging` → `main` promotion flow (§10) maps to three deployments. **Staging is a realistic test/review environment**, so features are exercised in production-like conditions before reaching production — nothing unreviewed touches prod.
- **Secrets server-side only:** ProjectX credentials and API keys live only in the cloud tier; the local UI never holds them (PRD §3). *Keep from AgentForge on its own merits:* BFF server-side credential custody, and persisted DataProtection keys if encrypted cookies must survive restarts.
- **Non-prod data comes from a production snapshot.** Staging/dev **seed and refresh their data stores from a snapshot of production ('live') data** for realistic history/context. The snapshot is *data only* — broker credentials stay per-environment secrets, so a snapshot can never hand a lower environment a live connection. **Decide:** snapshot refresh cadence + mechanism (ties to retention below).
- **Deployment environment vs. trading mode (R-14) — distinct axes, coupled by one safety policy:** **dev/staging are pinned to ProjectX practice accounts** (the real execution / auto-flatten path, practice money — never a live account); **production is the only environment permitted to run live**, where R-14's practice/live switch still governs the account. Staging therefore tests the *same* execution code that runs live (R-14 parity) with **zero real-money risk**. **Decide (before Phase 2):** practice/live → ProjectX credential/endpoint mapping within production (Q-4), and preventing an accidental live connection.
- **Retention:** **Decide** — tick/bar retention and roll-up policy (unbounded growth vs. R-9 replay needs vs. Q-10 cost).
- **Divergence from AgentForge:** the UI is a standalone local client against the remote BFF, so AgentForge's SMART-launch / cross-origin iframe / same-origin-proxy cookie machinery (decisions #21/#22) **does not carry over** — a single-origin client has no cross-site cookie problem.

## 9. Safety-Critical Engineering Discipline

The execution + auto-flatten layer moves real money and has **no AgentForge analog** — hold it to a higher bar than the rest of the system.

- **Fail-safe default:** when anything is uncertain, the safe state is **flat / no new orders**. Degrade deterministically; **never fabricate, never fail silently** (AgentForge rule, sharpened here).
- **Live is gated behind proven practice-account guardrails** (PRD §9): auto-flatten fires reliably, no unconfirmed or un-gated orders, native fills captured — *then* live is enabled with the same code, flipped mode.
- **Change control:** **Decide** a concrete policy for changes to the risk / execution / flatten / kill-switch paths — heightened review + mandatory safety suites (§5) before merge.
- **Auditability:** an immutable log of every order action and every guardrail decision (pairs with the proposed PRD security/audit requirement).

## 10. Git Workflow, CI/CD & Definition of Done

- **Host: GitHub** (`adammarquette/trading-copilot`) — issues and pull requests. The one deliberate divergence from AgentForge, which used an internal GitLab server *required for that project*; the workflow practices below carry over unchanged, only the host differs.
- **Branching — `develop` → `staging` → `main`:** **`develop` is the default branch**; contributors branch off `develop` and PR back into it. Changes promote `develop` → `staging` (realistic test/review) → `main` (production), and each long-lived branch deploys to its environment (§8). Never branch off or PR directly into `main`.
- **Issue-first — no orphaned PRs** (AgentForge's "no orphaned MRs", translated): every PR references a tracking issue that states the problem/requirement, opened *before* the PR (`Closes #N` / `Related to #N`); if none exists yet, open one first. Issues and PRs are part of the knowledge base — cite them like doc sections (`gh#N`). Deployment work is tracked as GitHub issues.
- **Planning & progress: a GitHub [Project board](https://github.com/adammarquette/trading-copilot/projects)** guides planning and tracks progress; issues and PRs flow onto it. It is set up to span **multiple related repos**, so any companion repos added later (e.g. a client library) link to the same board and stay synchronized.
- **Commits:** Conventional Commits with an `Assisted-by:` trailer for AI-authored changes; commit *type* drives SemVer, `vMAJOR.MINOR.PATCH` tags on `main`.
- **CI/CD via GitHub Actions:** stages **lint → build → test → deploy → verify**; deploy is `railway up` to the branch's environment (§8). Before a PR: `dotnet format --verify-no-changes` + unit tests green. Adopt AgentForge's hard-won guard rails — CI verifies tests *actually ran* (not a silent zero), and a license scan blocks restrictive-license bumps.
- **Merge gate:** GitHub **branch protection** on the long-lived branches (`develop`, `staging`, `main`) — require status checks (build / test / eval) to pass before merge (the analog of AgentForge's GitLab "pipelines must succeed" policy).
- **Definition of Done (starting point):** failing-test-first now green · standards/format clean · traces to a PRD requirement (`R-#`) and its tracking issue · tool/contract schemas updated on any interface change · no secrets in source · **safety-critical paths carry their required suites (§5, §9)** · the eval gate (§6) passes.

---

## 11. Companion Knowledge Wiki (development resource)

**This is not part of the trading platform.** It is a knowledge base that Adam (as the engineer) and the AI agents building the platform use *while developing it* — so the system can be built with real trading knowledge in mind rather than from assumptions. It sits alongside the project and informs design decisions; the running product does not read from it.

It can be stood up now, independent of the product build and its safety phasing — the earlier it exists, the more the design decisions are grounded as they're made. If a runtime knowledge base ever becomes a product feature, that is a separate decision — it would reuse this pattern but be specified as its own product requirement, with the trust-tier discipline below as a hard prerequisite.

### What it is

A compounding knowledge base following Karpathy's LLM Wiki pattern: immutable **raw sources**, an **LLM-maintained wiki** of interlinked markdown (summaries, concept and entity pages, an evolving synthesis), and a **schema file** that makes the agent a disciplined maintainer rather than a generic chatbot. Three operations — **ingest, query, lint** — and two navigation files: a content-oriented `index.md` and a chronological, append-only `log.md`. Knowledge is compiled once and kept current, not re-derived on every question.

It lives under the project's `documentation/` folder (`documentation/wiki/`); the exact internal arrangement is left to implementation.

### What it holds

Domain knowledge that shapes *how the platform should behave*: trading methodologies, order-flow theory, instrument/product specifics, company financials, and design-relevant references. In practice this is what tells the engineer and the build agents what a good suggestion looks like, how order flow is read, how the risk layers should behave — the reasoning behind the product requirements, kept in one maintained place.

### Invariants

- **Dual-audience:** LLMs are first-class readers *and* writers (agents create and maintain pages), while the content stays fully **human-readable** — plain markdown, browsable in Obsidian or any editor. No format that trades legibility for machine convenience.
- **`index.md` + `log.md`** exist and follow the gist's guidelines: `index.md` is a one-line-per-page catalog updated every ingest and read first at query time; `log.md` is append-only with a consistent grep-able prefix (e.g., `## [YYYY-MM-DD] ingest | Title`).
- **Immutable raw sources:** the agent reads from the raw archive but never edits it.
- **A schema file** (e.g., `SCHEMA.md`) defines page types, citation conventions, the new-page-vs-edit heuristic, and the ingest/query/lint workflows; it co-evolves with use.
- **Folder structure is flexible** — the invariants above are what matter.

### Operations

- **Ingest.** A source dropped into an `ingest/` drop-zone is read by an agent that discusses key takeaways with Adam, writes a summary page, updates the index and relevant entity/concept pages, appends a log entry, and archives the raw source. One source may touch 10–15 pages. Default flow is one source at a time with Adam involved; batch ingest with lighter supervision is possible.
- **Query.** Ask questions against the wiki; the agent reads `index.md` first, drills into relevant pages, and answers with citations and trust tiers. Valuable answers can be filed back as new pages so explorations compound.
- **Lint.** A periodic health check flags contradictions, stale claims, orphan pages, missing cross-references, and concepts lacking a page. Drift is the pattern's top failure mode, so lint runs on a schedule, not only on demand.

### Trust tiers (worth the discipline)

Even as a development aid, the knowledge isn't uniform, and mixing tiers quietly corrupts judgment. Every page carries a verification status and a source-trust tier that travels with any citation: **authoritative** (filings, exchange specs), **curated** (vetted methodologies, Adam's own tested notes), and **unverified** (social/influencer, un-checked intake). Unverified material is quarantined — readable, but not citable as *grounds* for another page until checked against a primary source. This keeps a hype-video "methodology" from silently hardening into a design assumption, and it's the prerequisite that would make any future runtime use safe.

### Relationship to the product

It informs the PRD; it is not read by the built system. The three product subsystems it might be confused with stay separate: **soft-signal ingestion** (fast, ephemeral events), **the rulebook** (Adam's own trading rules, learned at runtime), and **the journal** (suggestion/trade outcomes) are all runtime product features — the wiki is none of these. It is design-time scaffolding.

---

## 12. Technical Spikes / Open Engineering Questions

A landing zone for the engineering unknowns that need a spike or a decision before they block a phase. Most of the PRD's Open Questions are tagged *(Engineering)* — **Decide:** whether to migrate Q-1…Q-10 here (keeping the product/legal ones in the PRD) so the PRD stays product-focused. Until then, this section just points at them:

- **Blocking (before/at build start):** ProjectX order API capabilities (Q-1, gates R-11/R-13) · order-flow granularity for footprint (Q-2, R-3) · **auto-flatten guarantee & failure mode (Q-3, safety-critical — see §9)** · practice vs. live account handling (Q-4, §8) · untaken-suggestion simulation rules (Q-5, §5) · X/Twitter access method (Q-6).
- **Non-blocking (during implementation):** execution-time validity tolerance (Q-7, R-12) · YouTube transcript pipeline (Q-8) · local UI packaging (Q-9) · cloud host & cost ceiling (Q-10, §2/§8).

Each becomes its own page/entry as it's picked up.
