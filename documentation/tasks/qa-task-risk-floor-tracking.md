# Task Specification — Risk Floor Tracking Suite

**Title**: `QA(task#10) - Risk profile dynamic trailing drawdown & floor tracking integration suite`  
**Parent Task**: `#10` (Risk Rules Persistence & Gate Composition)  
**Priority**: **P2 (Medium)**  
**Target Test File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/RiskProfileLifecycleIntegrationTests.cs`  
**Category**: Integration Testing (Containerized Postgres)  

---

## 1. Goal & Context
Validates dynamic risk profile declaration and updating (`POST /accounts/{id}/risk`) under live trading conditions, verifying trailing drawdown floor calculation against Testcontainers Postgres.

---

## 2. Test Cases to Implement

### Test 1: `DeclareRisk_ShouldRecalculateTrailingDrawdownFloor`
- **Action**: `POST /accounts/{id}/risk` with updated `PerTradeRiskFraction` and `StartingBalance`.
- **Assertions**: `200 OK`, `RiskProfileRecord` updated in database with positive `StartingBalance`.

### Test 2: `UpdateRiskProfile_ShouldImmediatelyBindStagedOrderTake`
- **Setup**: Arm a staged order. Update risk profile to a tighter risk fraction that blocks the staged order size.
- **Action**: `POST /orders/{id}/take`.
- **Assertions**: `422 RefusedByRisk` (verifies `take` evaluates against the updated risk profile limits).
