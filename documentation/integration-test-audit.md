# Integration Test Suite Audit & Ticket Backlog Report

**Author**: Primary QA / SDET  
**Date**: July 23, 2026 · **Realigned to the route map**: 2026-07-24 (gh#160)  
**Target Repository**: `trading-copilot` (`src/MarqSpec.TradingCopilot.IntegrationTests`)  
**Status**: Living inventory — suites land incrementally; see §4 for current per-issue status  

---

## 1. Executive Summary & Audit Context

As the primary QA/SDET for **Trading Copilot**, a comprehensive audit was conducted across the API endpoints, service layer, domain guards, multi-tenancy model (ADR-0017 / R-20), and database persistence layer (`TradingCopilotDbContext` & DB constraint triggers).

The objective of this audit is to identify high-value integration testing gaps, quantify risk coverage, and define actionable tickets to raise the quality ceiling of the platform.

### Key Audit Findings (status current as of the gh#160 realignment)
1. **Existing Integration Coverage**: Firm Onboarding, Risk Profile Declaration, User Invitation / Primary-Operator Auth Guard, the unauthenticated challenge, order execution (direct + staged ladder), the staged-stop plan, the event backbone, the synthetic conditional-entry firing engine, and the production smoke suite — see §2 for the full inventory.
2. **Gaps — delivered (✅) and remaining (⬜):**
   - ✅ **Staged Send Path & Order Execution (`arm` → `edit` → `take` → `cancel`)** — direct send (gh#130), the staged ladder (gh#157), and the stop-plan DB guards (gh#158) all landed against Testcontainers Postgres.
   - ✅ **R-14 Database Mode & Environment Enforcement** — the `Order.mode == Account.mode` trigger and mode × environment refusals are exercised in the order suites (gh#130, gh#157).
   - ✅ **Multi-Tenant / Multi-Operator Data Isolation (R-20 / ADR-0017)** — **gh#132**: collection-scoping, cross-tenant `404`s on accounts/orders, and gate-decision isolation proven at the `DbContext` (§2).
   - ✅ **Connection Lifecycle & Account Stage** — creation, discovery, and stage resolution/override covered (**gh#142**). Credential *rotation* and connection *soft-delete* remain **missing features**, not missing tests → **gh#210** for the Coding Agent.
   - ✅ **Production Read-Only Smoke Test Suite** — gh#131; the lazy-fixture fix so a deployed-target run starts no container is gh#152.
   - ✅ **Event Backbone Storage** — the append-only Timescale log at the storage tier (gh#161).
   - ✅ **Synthetic Conditional-Entry Firing Engine (ADR-0007 / R-11)** — arm (persist `Pending`, transmit nothing) → a quote crossing the trigger fires it to a **working native order** through the **authoritative fire-time re-gate** (R-5 / R-16 / R-12: a conditional valid at arm but violating the gate at fire is refused, not sent); cancel-if drift, expiry-outranks-fire, fired-then-replayed idempotency, a live-host `market.quote` backbone consume, and the `CK_ConditionalOrders_*` DB guards — **gh#180**, pairing the #198 watcher.

---

## 2. Current Integration Test Inventory

Below is the baseline inventory of current integration tests under [`src/MarqSpec.TradingCopilot.IntegrationTests`](../src/MarqSpec.TradingCopilot.IntegrationTests) (`Api/` for HTTP-driven suites, `Data/` for storage-tier ones):

| Integration Test Suite | Covered Endpoints & Features | Current Status |
| :--- | :--- | :--- |
| **`FirmOnboardingEndpointsIntegrationTests.cs`** | `POST /firms`, `GET /firms`, `PUT /firms/{id}/conventions`, connection creation & account discovery | **Active** (Passing) |
| **`RiskEndpointsIntegrationTests.cs`** | `PUT /accounts/{id}/risk`, `GET /accounts/{id}/risk` | **Active** (Passing) |
| **`UserInvitationEndpointsIntegrationTests.cs`** | `POST /auth/invitations`, `POST /auth/invitations/accept`, Primary Operator flag enforcement | **Active** (Passing) |
| **`UnauthenticatedEndpointsTests.cs`** | Global HTTP 401 unauthenticated challenge verification | **Active** (Passing) |
| **`OrderEndpointsIntegrationTests.cs`** | `POST /accounts/{id}/orders` — the **direct** send path: fail-closed risk absence, credential-key guard, gate refusal + audit trail, R-14 mode × environment (gh#130) | **Active** (Passing) |
| **`StagedOrderLadderIntegrationTests.cs`** | The **staged ladder** — `POST …/orders/arm`, `PUT /orders/{id}`, `POST /orders/{id}/take`, `DELETE /orders/{id}` — plus the gh#134 working-stop regression and the gh#96 DB mode guard (gh#157) | **Active** (Passing) |
| **`StopPlanPersistenceIntegrationTests.cs`** | Staged-stop plan persistence (`POST /accounts/{id}/orders` → `StopPlanRecord`); the four `CK_StopPlans_*` DB CHECK constraints proven by name on **both** safety-beyond-actual sides with a positive control; FK `ON DELETE CASCADE`; ATR not-yet-supported pin (gh#158) | **Active** (Passing) |
| **`SystemSmokeIntegrationTests.cs`** | `Category=Smoke` read-only probes against a deployed target (gh#131); the in-process host + its container are now **lazy**, so a deployed-target run starts no PostgreSQL container / needs no Docker (gh#152) | **Active** (Passing) |
| **`Data/EventBackboneIntegrationTests.cs`** | The append-only event log (ADR-0001) at the **storage tier** via `IEventLog` against the applied `AddEventBackbone` hypertable migration — envelope round-trip (`jsonb` payload, UTC instant), monotonic sequences under concurrent appends, the #156 replay two-rows-one-`Id` contract, id generation, `ReadAfter` ordering + paging, cursor upsert, blank type/source rejection; pins the retention-gap silent-skip (gh#162) and a non-UTC `OccurredAt` rejection (gh#201) (gh#161) | **Active** (Passing) |
| **`MultiTenantIsolationIntegrationTests.cs`** | R-20 default-deny workspace isolation between two operators (ADR-0017): collection-scoping (`GET /connections` excludes the other operator), `GET /connections/{id}/accounts` → 404 for a non-owner, staged-order `take`/`cancel` → 404 (order stays `Staged`), and gate-decision isolation proven at a user-scoped `DbContext`; second operator via the invitation flow (gh#132) | **Active** (Passing) |
| **`RiskProfileLifecycleIntegrationTests.cs`** | Per-account risk declaration (`PUT /accounts/{id}/risk`) — persistence + one-per-account replace-on-redeclaration; and the **R-12 re-gate**: a profile tightened *after* arming refuses the next `take` (422 `RefusedByRisk`, order stays `Staged`, the refusal journaled as a blocked gate decision) (gh#143) | **Active** (Passing) |
| **`ConnectionLifecycleIntegrationTests.cs`** | Connection creation, account discovery, and the per-account stage override — the roster persists and reads back with **mode resolved from the firm's declared conventions**, not the venue's flag (Practice, not the stub's Live; unrecognised names stay `Undeclared`, gh#60); one-login-per-firm×platform `409` (ADR-0016); stage override → convention mode / `Unknown` refused; clear reverts to the resolved stage (gh#142) | **Active** (Passing) |
| **`ConditionalFiringIntegrationTests.cs`** | The **synthetic conditional-entry firing engine** (ADR-0007, pairing the #198 watcher): `POST /accounts/{id}/orders/conditional` arms a pending conditional (persisted `Pending`, **transmits nothing**); the firing watcher (`ConditionalFiringService` / `ConditionalOrderHost`) fires it to a **working native order** when a quote crosses the trigger, **re-running R-5 / R-16 / R-12 at fire time** — a conditional valid at arm but violating the gate at fire is **refused, not sent** (proven by tightening risk between arm and fire); cancel-if drift; expiry outranks a crossing trigger; fired-then-replayed idempotency; a live-background-host `market.quote` backbone consume; and the `CK_ConditionalOrders_CancelDrift_StaleSide` / `_Direction_NotUnknown` DB checks proven by name (gh#180) | **Active** (Passing) |
| **`AutoFlattenSchedulerIntegrationTests.cs`** | The **primary auto-flatten scheduler** (R-13, ADR-0013) via `AutoFlattenService.RunPassAsync` at a controlled instant: closes an open position at its per-market deadline (`flatten.executed`) and **never opens** exposure; warns before the deadline, journals `missed` past the firing window, `disabled`, and `unconfigured`; retries to the attempt cap then `escalated` on a surviving position — every decision journalled to the real event log. Drives the shared `TestHost` position stub (open positions + configurable close) with the always-on hosts suppressed (gh#186) | **Active** (Passing) |
| **`AutoFlattenWatchdogIntegrationTests.cs`** | The **redundant auto-flatten watchdog** (R-13, ADR-0013) via `AutoFlattenWatchdogService.RunPassAsync`: the independent second tier steps in only on the primary's failure — a position open past deadline **+ grace** — closing once per pass (`flatten.watchdog.saved`), **defers to the primary inside the grace window**, journals `flatten.watchdog.rejected` when the close leaves exposure **or the venue rejects it** (one attempt, retried next pass — never a silent give-up), and `flatten.watchdog.critical` past the firing window; never opens exposure (gh#188) | **Active** (Passing) |
| **`KillSwitchIntegrationTests.cs`** | The **kill switch** (R-11, ADR-0007) end-to-end over HTTP (10 tests): engaging (**hold-to-confirm** — an unconfirmed request is refused `422` and does not engage) **disables outbound** so a subsequent direct send **and** a staged `take` are both refused (`RefusedByKillSwitch`, nothing transmitted, the ticket stays `Staged`, and the refusal **precedes sizing** — no `GateDecisionRecord`); **cancels working orders**; and per mode either **flattens all** open positions (close called, count reported) or **halts only** (leaves them on their native safety stops, closes nothing) — with an **omitted mode failing safe to FlattenAll**. The reducing path is **deliberately exempt**: auto-flatten still closes at the deadline while engaged (ADR-0007). Disengaging **re-enables** outbound; the engaged lock **rehydrates at host startup** from the durable row (a fresh host on the same DB comes up engaged, ADR-0013). Reuses the shared `TestHost` position stub + `FlattenTestPostgresFactory` (gh#190) | **Active** (Passing) |
| **`OrphanHandlingIntegrationTests.cs`** | **Connection-loss orphan handling** (R-11, R-19, ADR-0013) via `OrphanGuardService.OrphanAsync`/`RearmAsync` + `StopPromotionService.PromoteForQuoteAsync`: a drop moves **hidden** stops to `Orphaned` (high-severity `synthetic_risk` log) while **native** exchange-held stops and **pending conditionals** are left untouched; an orphaned stop is **never promotable** (Hidden-only, by construction) and the **native safety stop stays the floor** throughout; reconnect **re-arms** orphans to `Hidden` against venue truth and **auto-resumes nothing else** (native/retired/conditional); a restart **reconciles lingering orphans** on the first pass; ownership is **preserved** through the `IgnoreQueryFilters` sweep; and flaps are **idempotent**. Adversarial venue positions + always-on hosts suppressed via `OrphanTestPostgresFactory` (gh#192) | **Active** (Passing) |

---

## 3. High-Value Test Opportunities & Risk Analysis

```mermaid
flowchart TD
    subgraph Staged Send Path Integration
        A[POST /orders/arm] -->|Stage Proposal| B[(Database Order Row)]
        B -->|WorkingStopPrice Persisted| C[PUT /orders/{id}]
        C -->|Re-gate Edit| D[POST /orders/{id}/take]
        D -->|Fresh Venue & Risk Re-check| E[Transmitted / Working]
        D -->|Risk Violation| F[Staged / HTTP 422 Refused]
    end
    subgraph Multi-Tenant Isolation
        G[Operator A Context] -->|Query Operator B Order| H[HTTP 404 Not Found]
        I[Operator A Context] -->|Take Operator B Order| J[HTTP 404 Not Found]
    end
```

### High-Risk Execution Paths Requiring Integration Coverage:
1. **Working Stop Persistence & Re-validation (`gh#134`)**:
   - Limit/Market orders armed with tight working stops must not size against worst-case safety stops during `take`. An integration test running against Postgres verifies EF Core serialization and DB persistence of `WorkingStopPrice`.
2. **Database Trigger Guard Verification**:
   - `Order.mode` must match `Account.mode` at the database level. Direct writes or invalid service requests attempting cross-mode persistence must trigger real PostgreSQL exception handling.
3. **Audit Trail Integrity (`GateDecisionRecord`)**:
   - Proving that every sized order attempt (whether placed or blocked by risk rules) persists a corresponding `GateDecisionRecord` linked to `UserId`, `AccountId`, and optional `OrderId`.

---

## 4. Registered GitHub issues — status link table

Specs live in their GitHub issues (the tracker is the source of truth, gh#144); this section is a **link table
with status**, not a copy of the bodies. Statuses current as of the gh#160 realignment (2026-07-24).

| Issue | Suite / concern | Target file | Status |
| :--- | :--- | :--- | :--- |
| [#130](https://github.com/adammarquette/trading-copilot/issues/130) | Direct send path & order execution | `OrderEndpointsIntegrationTests.cs` | ✅ Delivered · closed |
| [#157](https://github.com/adammarquette/trading-copilot/issues/157) | Staged ladder (arm → edit → take → cancel) | `StagedOrderLadderIntegrationTests.cs` | ✅ Delivered (gh#197) · closed |
| [#158](https://github.com/adammarquette/trading-copilot/issues/158) | StopPlan persistence & safety-beyond-actual DB guard | `StopPlanPersistenceIntegrationTests.cs` | ✅ Delivered (gh#196) · closed |
| [#161](https://github.com/adammarquette/trading-copilot/issues/161) | Event backbone storage & replay-dedupe | `Data/EventBackboneIntegrationTests.cs` | ✅ Delivered (gh#204) · closed |
| [#131](https://github.com/adammarquette/trading-copilot/issues/131) | Production-safe read-only smoke suite | `SystemSmokeIntegrationTests.cs` | ✅ Delivered · closed |
| [#152](https://github.com/adammarquette/trading-copilot/issues/152) | Smoke suite starts no container on a deployed target | `SystemSmokeIntegrationTests.cs` (+ `TestHost/LazySmokeHostFixture.cs`) | ✅ Delivered (gh#205) · closed |
| [#180](https://github.com/adammarquette/trading-copilot/issues/180) | Synthetic conditional-entry firing engine (pairs the #198 watcher) | `ConditionalFiringIntegrationTests.cs` | ✅ Delivered (gh#216) |
| [#132](https://github.com/adammarquette/trading-copilot/issues/132) | Multi-tenant workspace & resource isolation | `MultiTenantIsolationIntegrationTests.cs` | ✅ Delivered (gh#213) — realigned to the route map (gh#160) |
| [#142](https://github.com/adammarquette/trading-copilot/issues/142) | Connection & account-stage suite | `ConnectionLifecycleIntegrationTests.cs` | ✅ Delivered (gh#217) — realigned (gh#160); missing endpoints → [#210](https://github.com/adammarquette/trading-copilot/issues/210) |
| [#143](https://github.com/adammarquette/trading-copilot/issues/143) | Risk profile trailing drawdown & floor tracking | `RiskProfileLifecycleIntegrationTests.cs` | ✅ Delivered (gh#215) — realigned per gh#160 (`PUT` verb, reuses the gh#157 harness) |
| [#186](https://github.com/adammarquette/trading-copilot/issues/186) | Auto-flatten scheduler host | `AutoFlattenSchedulerIntegrationTests.cs` (+ shared `TestHost` position stub & `FlattenTestPostgresFactory`) | ✅ Delivered (gh#235) |
| [#188](https://github.com/adammarquette/trading-copilot/issues/188) | Redundant watchdog + rejected-order | `AutoFlattenWatchdogIntegrationTests.cs` | ✅ Delivered (gh#238) |
| [#190](https://github.com/adammarquette/trading-copilot/issues/190) | Kill switch — outbound halt, cancel working, flatten/halt, startup rehydration | `KillSwitchIntegrationTests.cs` (reuses the gh#186 position stub) | ✅ Delivered (gh#239) |
| [#192](https://github.com/adammarquette/trading-copilot/issues/192) | Connection-loss orphan handling (drop → orphan, reconnect → re-arm, restart reconcile) | `OrphanHandlingIntegrationTests.cs` (+ `TestHost/OrphanTestPostgresFactory.cs`) | ✅ Delivered (this PR) |
| [#210](https://github.com/adammarquette/trading-copilot/issues/210) | *(work:code)* connection credential rotation + soft-delete | Coding Agent — `ConnectionEndpoints.cs` | 📝 Open · filed by gh#160 |

---

## 5. Route-map realignment log (gh#160)

The open QA specs were drafted from the audit's *intended* surface, not the *built* one; gh#160 corrected each
against the live route map on `develop` so every remaining test names an endpoint that exists:

- **#142** — `PUT /connections/{id}/credentials` and `DELETE /connections/{id}` do **not** exist; they are missing
  *features*, moved to [#210](https://github.com/adammarquette/trading-copilot/issues/210). The suite now covers
  connection creation, account discovery, and stage resolution/override — all routes that exist. Re-estimated 3 → 2.
- **#132** — `GET /connections/{id}` (by-id) and a gate-decision audit endpoint do **not** exist. Test 1 becomes
  collection-scoping (`GET /connections` must exclude the other operator's connection); Test 4 is parked at the
  `TradingCopilotDbContext` level until a gate-decision read surface exists. Stays **P0**.
- **#143** — declaration is `PUT /accounts/{id}/risk`, not `POST`; its `take` test now **reuses the gh#157
  staged-ladder harness** rather than standing up a second arm/take fixture.

Earlier drift this refresh also cleared: the never-created `OrderExecutionEndpointsIntegrationTests.cs` /
`ProductionSmokeTests.cs` target names (the shipped files are `OrderEndpointsIntegrationTests.cs` /
`SystemSmokeIntegrationTests.cs`); `#134` mislisted as a parent *task* (it is a **PR**); and a stale
"Ready for Development" header while several suites had already shipped and closed.
