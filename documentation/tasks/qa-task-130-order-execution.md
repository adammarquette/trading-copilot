# Task Specification — Issue #130

**Issue Number**: `#130`  
**Title**: `QA(task#11) - Staged send path & order execution integration test suite`  
**Parent Tasks**: `#11` (Staged Send Path — `arm` $\rightarrow$ `edit` $\rightarrow$ `take`), `#134` (Working Stop Persistence)  
**Priority**: **P0 (Critical)**  
**Target Test File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/OrderExecutionEndpointsIntegrationTests.cs`  
**Category**: Integration Testing (Containerized Postgres via Testcontainers)  

---

## 1. Goal & Context
The safety-critical order execution path (ADR-0007, R-11b, R-12) processes orders through a staged ladder before venue transmission. While extensive unit tests exist (`StagedOrderEndpointsTests.cs`), a real integration test running against PostgreSQL (`TradingCopilotDbContext`) is required to verify:
- End-to-end database persistence of `Order` rows in `OrderStatus.Staged` with exact parameters (`Symbol`, `WorkingStopPrice`, `SafetyStopPrice`, `ReferencePrice`, `TickSize`, `PointValue`).
- `POST /orders/arm`, `PUT /orders/{id}`, `POST /orders/{id}/take`, and `DELETE /orders/{id}` HTTP endpoint pipelines.
- Fresh venue state re-validation upon `take` and creation of `GateDecisionRecord` audit rows.
- R-14 database mode guard enforcement (`Order.mode == Account.mode`).
- `gh#134` Working Stop Persistence regression guard: proving a `Limit` order armed under `ActualStop` sizes against `WorkingStopPrice` and transmits.

---

## 2. Test Cases to Implement

### Test 1: `ArmStagedOrder_ShouldPersistStagedOrderInDatabase_WithoutVenuePlacement`
- **Setup**: Create primary operator, firm, connection, and active account in Postgres.
- **Action**: Invoke `POST /orders/arm` with a valid limit order proposal.
- **Assertions**:
  - HTTP Response: `200 OK` with `OrderStatus.Staged` and non-null `orderId`.
  - Database: `Order` row exists with `Status = Staged`, `VenueOrderKey = null`, and exact `WorkingStopPrice`.
  - Venue Mock: Zero place-order calls recorded.

### Test 2: `EditStagedOrder_ShouldUpdateProposalAndReEvaluateRiskGate`
- **Setup**: Arm a staged order with quantity = 1.
- **Action**: Invoke `PUT /orders/{id}` updating quantity = 2.
- **Assertions**:
  - HTTP Response: `200 OK`.
  - Database: `Order.Size` updated to 2, and new `GateDecisionRecord` appended.

### Test 3: `TakeStagedOrder_ShouldReValidateFreshStateAndTransmit`
- **Setup**: Arm a staged order.
- **Action**: Invoke `POST /orders/{id}/take`.
- **Assertions**:
  - HTTP Response: `200 OK` with `OrderStatus.Working` and populated `venueOrderId`.
  - Database: `Order.Status` transitions to `Working`, `VenueOrderKey` set, `PlacedAt` updated to venue timestamp.
  - Venue Mock: Exactly 1 place-order call received.

### Test 4: `TakeStagedOrder_ShouldFailWith422_WhenFreshVenueStateViolatesRiskRules`
- **Setup**: Arm a staged order when account has 0 positions. Change mock venue account state so unrealized loss exceeds drawdown limit.
- **Action**: Invoke `POST /orders/{id}/take`.
- **Assertions**:
  - HTTP Response: `422 RefusedByRisk`.
  - Database: Order remains `OrderStatus.Staged`, `VenueOrderKey` remains null. `GateDecisionRecord` persisted auditing the refusal.

### Test 5: `TakeStagedOrder_ShouldRebuildWorkingStop_NotSafetyStop_ForLimitOrder (gh#134)`
- **Setup**: Configure `RiskProfileRecord` with `SizingBasis = ActualStop` and tight `PerTradeRiskFraction`. Arm a `Limit` order with tight working stop (1pt) and wide safety stop (20pt).
- **Action**: Invoke `POST /orders/{id}/take`.
- **Assertions**:
  - HTTP Response: `200 OK` (proves `take` sized against 1pt working stop, not 20pt safety stop).
