# Task Specification — Connection Lifecycle Suite

**Title**: `QA(task#7) - Connection credential lifecycle & account stage resolution integration suite`  
**Parent Task**: `#7` (Connections, Accounts, and Stage Conventions)  
**Priority**: **P2 (Medium)**  
**Target Test File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/ConnectionLifecycleIntegrationTests.cs`  
**Category**: Integration Testing (Containerized Postgres)  

---

## 1. Goal & Context
Validates connection management endpoints (`POST /connections`, `GET /connections/{id}`, `PUT /connections/{id}/credentials`, `DELETE /connections/{id}`) and account stage resolution (`PUT /accounts/{id}/stage`).

---

## 2. Test Cases to Implement

### Test 1: `UpdateCredentials_ShouldInvalidateOldTokensAndUpdateConnection`
- **Action**: `PUT /connections/{id}/credentials` with new API key.
- **Assertions**: `200 OK`, connection credentials updated in database, venue session re-initialized.

### Test 2: `DeleteConnection_ShouldSoftDeleteConnectionAndDeactivateChildAccounts`
- **Action**: `DELETE /connections/{id}`.
- **Assertions**: `200 OK`, connection `IsActive` set to false, child accounts marked inactive.

### Test 3: `UpdateAccountStage_ShouldEnforceFirmStageConventions`
- **Action**: `PUT /accounts/{id}/stage` setting stage key.
- **Assertions**: `200 OK` if stage is allowed by parent firm conventions; `400 Bad Request` if invalid.
