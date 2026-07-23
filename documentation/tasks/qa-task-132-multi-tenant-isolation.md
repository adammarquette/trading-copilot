# Task Specification — Issue #132

**Issue Number**: `#132`  
**Title**: `QA(task#128) - Multi-tenant workspace & resource isolation integration suite`  
**Parent Tasks**: `#128` (Primary Operator Invitation Restriction), `ADR-0017` (Single-Operator Data Isolation), `R-20`  
**Priority**: **P0 (Critical)**  
**Target Test File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/MultiTenantIsolationIntegrationTests.cs`  
**Category**: Security & Data Isolation Integration Testing  

---

## 1. Goal & Context
`ADR-0017` and requirement `R-20` mandate strict default-deny workspace isolation in multi-tenant environments. A non-primary operator (User B) must never be able to access, modify, arm, edit, take, cancel, or enumerate resources (connections, accounts, risk profiles, staged orders, gate decisions) belonging to User A (Primary Operator).

To prevent resource enumeration attacks, cross-tenant requests must return `HTTP 404 Not Found` (never `403 Forbidden` or sensitive details).

---

## 2. Test Cases to Implement

### Test 1: `GetResource_ShouldReturn404_WhenUserBQueriesUserAConnection`
- **Setup**: Seed User A with Connection A. Authenticate request as User B.
- **Action**: Invoke `GET /connections/{connectionAId}`.
- **Assertions**: HTTP Response: `404 Not Found`.

### Test 2: `GetAccounts_ShouldReturnEmpty_WhenUserBListsAccountsOnUserAConnection`
- **Setup**: Seed User A with Connection A and Account A. Authenticate request as User B.
- **Action**: Invoke `GET /connections/{connectionAId}/accounts`.
- **Assertions**: HTTP Response: `404 Not Found` or empty `[]` array.

### Test 3: `StagedOrderActions_ShouldReturn404_WhenUserBAttemptsToTakeOrCancelUserAOrder`
- **Setup**: Seed User A with Staged Order A. Authenticate as User B.
- **Action**: Invoke `POST /orders/{orderAId}/take` and `DELETE /orders/{orderAId}`.
- **Assertions**: HTTP Response: `404 Not Found`. Order A remains `Staged` in database.

### Test 4: `GateDecisionAudit_ShouldExcludeUserADecisions_WhenUserBQueriesAuditLog`
- **Setup**: Seed GateDecision records for User A and User B. Authenticate as User B.
- **Action**: Query audit endpoint for User B.
- **Assertions**: Returned decision list contains zero records belonging to User A.
