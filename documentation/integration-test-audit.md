# Integration Test Suite Audit & Ticket Backlog Report

**Author**: Primary QA / SDET  
**Date**: July 23, 2026  
**Target Repository**: `trading-copilot` (`src/MarqSpec.TradingCopilot.IntegrationTests`)  
**Status**: Registered on GitHub & Ready for Development  

---

## 1. Executive Summary & Audit Context

As the primary QA/SDET for **Trading Copilot**, a comprehensive audit was conducted across the API endpoints, service layer, domain guards, multi-tenancy model (ADR-0017 / R-20), and database persistence layer (`TradingCopilotDbContext` & DB constraint triggers).

The objective of this audit is to identify high-value integration testing gaps, quantify risk coverage, and define actionable tickets to raise the quality ceiling of the platform.

### Key Audit Findings
1. **Existing Integration Coverage**: Solid coverage exists for **Firm Onboarding**, **Risk Profile Declaration**, and **User Invitation / Primary Operator Auth Guard**.
2. **Critical Gaps Identified**:
   - **Staged Send Path & Order Execution (`arm` $\rightarrow$ `edit` $\rightarrow$ `take` $\rightarrow$ `cancel`)**: Covered extensively in unit tests, but lacks an integration test suite running against real DB transactions and EF Core change trackers.
   - **R-14 Database Mode & Environment Enforcement**: Database constraint triggers (`Order.mode == Account.mode`) and R-14 mode x environment mismatch guards need end-to-end integration verification against Testcontainers Postgres.
   - **Multi-Tenant / Multi-Operator Data Isolation (R-20 / ADR-0017)**: Needs a dedicated integration suite proving that default-deny isolation enforces total invisibility (`404` / empty result sets) across separate user context tokens for all entities (`Order`, `Connection`, `Account`, `RiskProfile`, `GateDecision`).
   - **Connection Lifecycle & Credential Mutation**: Connection creation, credential key rotation, deactivation cascading, and account stage updates lack dedicated integration test scenarios.
   - **Production Read-Only Smoke Test Suite**: No tagged, production-safe smoke test suite exists for quick post-deployment health verification.

---

## 2. Current Integration Test Inventory

Below is the baseline inventory of current integration tests under [`src/MarqSpec.TradingCopilot.IntegrationTests`](../src/MarqSpec.TradingCopilot.IntegrationTests) (`Api/` for HTTP-driven suites, `Data/` for storage-tier ones):

| Integration Test Suite | Covered Endpoints & Features | Current Status |
| :--- | :--- | :--- |
| **`FirmOnboardingEndpointsIntegrationTests.cs`** | `GET /firms`, `GET /firms/{key}`, `POST /firms/{key}/stage-conventions`, connection creation & account resolution | **Active** (Passing) |
| **`RiskEndpointsIntegrationTests.cs`** | `PUT /accounts/{id}/risk`, `GET /accounts/{id}/risk` | **Active** (Passing) |
| **`UserInvitationEndpointsIntegrationTests.cs`** | `POST /auth/invitations`, `POST /auth/invitations/accept`, Primary Operator flag enforcement | **Active** (Passing) |
| **`UnauthenticatedEndpointsTests.cs`** | Global HTTP 401 unauthenticated challenge verification | **Active** (Passing) |
| **`OrderEndpointsIntegrationTests.cs`** | `POST /accounts/{id}/orders` — the **direct** send path: fail-closed risk absence, credential-key guard, gate refusal + audit trail, R-14 mode × environment (gh#130) | **Active** (Passing) |
| **`StagedOrderLadderIntegrationTests.cs`** | The **staged ladder** — `POST …/orders/arm`, `PUT /orders/{id}`, `POST /orders/{id}/take`, `DELETE /orders/{id}` — plus the gh#134 working-stop regression and the gh#96 DB mode guard (gh#157) | **Active** (Passing) |
| **`StopPlanPersistenceIntegrationTests.cs`** | Staged-stop plan persistence (`POST /accounts/{id}/orders` → `StopPlanRecord`); the four `CK_StopPlans_*` DB CHECK constraints proven by name on **both** safety-beyond-actual sides with a positive control; FK `ON DELETE CASCADE`; ATR not-yet-supported pin (gh#158) | **Active** (Passing) |
| **`SystemSmokeIntegrationTests.cs`** | `Category=Smoke` read-only probes against a deployed target (gh#131); the in-process host + its container are now **lazy**, so a deployed-target run starts no PostgreSQL container / needs no Docker (gh#152) | **Active** (Passing) |
| **`Data/EventBackboneIntegrationTests.cs`** | The append-only event log (ADR-0001) at the **storage tier** via `IEventLog` against the applied `AddEventBackbone` hypertable migration — envelope round-trip (`jsonb` payload, UTC instant), monotonic sequences under concurrent appends, the #156 replay two-rows-one-`Id` contract, id generation, `ReadAfter` ordering + paging, cursor upsert, blank type/source rejection; pins the retention-gap silent-skip (gh#162) and a non-UTC `OccurredAt` rejection (gh#201) (gh#161) | **Active** (Passing) |

> **Inventory drift (tracked by [gh#160](https://github.com/adammarquette/trading-copilot/issues/160)):** §4/§5
> below still carry stale target file names and closed-issue statuses, and duplicate the issue bodies wholesale
> rather than linking them. The full realignment is **gh#160's** deliverable, not this PR's.

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

## 4. Registered GitHub Issue Backlog & Specifications

The following GitHub issues are registered and tracked on `adammarquette/trading-copilot`:

### Ticket 1: `gh#130` — `QA(task#11) - Staged send path & order execution integration test suite`
* **GitHub Link**: [https://github.com/adammarquette/trading-copilot/issues/130](https://github.com/adammarquette/trading-copilot/issues/130)
* **Title Format**: `QA(task#11) - Staged send path & order execution integration test suite`
* **Specification**: [gh#130](https://github.com/adammarquette/trading-copilot/issues/130)
* **Priority**: **P0 (Critical)**
* **Category**: Integration Testing
* **Parent Tasks**: `#11` (Staged Send Path), `#134` (Working Stop persistence)
* **Target File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/OrderExecutionEndpointsIntegrationTests.cs`

---

### Ticket 2: `gh#132` — `QA(task#128) - Multi-tenant workspace & resource isolation integration suite`
* **GitHub Link**: [https://github.com/adammarquette/trading-copilot/issues/132](https://github.com/adammarquette/trading-copilot/issues/132)
* **Title Format**: `QA(task#128) - Multi-tenant workspace & resource isolation integration suite`
* **Specification**: [gh#132](https://github.com/adammarquette/trading-copilot/issues/132)
* **Priority**: **P0 (Critical)**
* **Category**: Security & Isolation Integration Testing
* **Parent Tasks**: `#128` (Primary Operator Issuance Restriction), `ADR-0017`, `R-20`
* **Target File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/MultiTenantIsolationIntegrationTests.cs`

---

### Ticket 3: `gh#131` — `QA(system) - Production-safe read-only smoke test suite`
* **GitHub Link**: [https://github.com/adammarquette/trading-copilot/issues/131](https://github.com/adammarquette/trading-copilot/issues/131)
* **Title Format**: `QA(system) - Production-safe read-only smoke test suite`
* **Specification**: [gh#131](https://github.com/adammarquette/trading-copilot/issues/131)
* **Priority**: **P1 (High)**
* **Category**: Deployment & Smoke Testing
* **Parent Task**: System Health & Visibility
* **Target File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/ProductionSmokeTests.cs`

---

### Ticket 4: `gh#142` — `QA(task#7) - Connection credential lifecycle & account stage resolution integration suite`
* **GitHub Link**: [https://github.com/adammarquette/trading-copilot/issues/142](https://github.com/adammarquette/trading-copilot/issues/142)
* **Title Format**: `QA(task#7) - Connection credential lifecycle & account stage resolution integration suite`
* **Specification**: [gh#142](https://github.com/adammarquette/trading-copilot/issues/142)
* **Priority**: **P2 (Medium)**
* **Category**: Integration Testing
* **Parent Task**: `#7` (Connection & Account Resolution)
* **Target File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/ConnectionLifecycleIntegrationTests.cs`

---

### Ticket 5: `gh#143` — `QA(task#10) - Risk profile dynamic trailing drawdown & floor tracking integration suite`
* **GitHub Link**: [https://github.com/adammarquette/trading-copilot/issues/143](https://github.com/adammarquette/trading-copilot/issues/143)
* **Title Format**: `QA(task#10) - Risk profile dynamic trailing drawdown & floor tracking integration suite`
* **Specification**: [gh#143](https://github.com/adammarquette/trading-copilot/issues/143)
* **Priority**: **P2 (Medium)**
* **Category**: Integration Testing
* **Parent Task**: `#10` (Risk Rules Persistence & Gate Composition)
* **Target File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/RiskProfileLifecycleIntegrationTests.cs`

---

## 5. Summary Matrix & GitHub Issue Tracking

| GitHub Issue # | Parent Task | Issue Title Format | Specification File | Target Test File |
| :--- | :--- | :--- | :--- | :--- |
| **`#130`** | `#11`, `#134` | `QA(task#11) - Staged send path & order execution integration test suite` | [gh#130](https://github.com/adammarquette/trading-copilot/issues/130) | `OrderExecutionEndpointsIntegrationTests.cs` |
| **`#132`** | `#128` | `QA(task#128) - Multi-tenant workspace & resource isolation integration suite` | [gh#132](https://github.com/adammarquette/trading-copilot/issues/132) | `MultiTenantIsolationIntegrationTests.cs` |
| **`#131`** | System Health | `QA(system) - Production-safe read-only smoke test suite` | [gh#131](https://github.com/adammarquette/trading-copilot/issues/131) | `ProductionSmokeTests.cs` |
| **`#142`** | `#7` | `QA(task#7) - Connection credential lifecycle & account stage resolution integration suite` | [gh#142](https://github.com/adammarquette/trading-copilot/issues/142) | `ConnectionLifecycleIntegrationTests.cs` |
| **`#143`** | `#10` | `QA(task#10) - Risk profile dynamic trailing drawdown & floor tracking integration suite` | [gh#143](https://github.com/adammarquette/trading-copilot/issues/143) | `RiskProfileLifecycleIntegrationTests.cs` |
| **`#158`** | `#153`, `#11` | `QA(task#153) - StopPlan persistence & the safety-beyond-actual DB guard integration suite` | [gh#158](https://github.com/adammarquette/trading-copilot/issues/158) | `StopPlanPersistenceIntegrationTests.cs` ✅ **delivered** |
| **`#161`** | `#13`, `#156` | `QA(task#13) - Event backbone storage & replay-dedupe integration suite` | [gh#161](https://github.com/adammarquette/trading-copilot/issues/161) | `Data/EventBackboneIntegrationTests.cs` ✅ **delivered** |
