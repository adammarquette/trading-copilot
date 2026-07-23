# Task Specification — Issue #131

**Issue Number**: `#131`  
**Title**: `QA(system) - Production-safe read-only smoke test suite`  
**Parent Tasks**: System Health & Visibility  
**Priority**: **P1 (High)**  
**Target Test File**: `src/MarqSpec.TradingCopilot.IntegrationTests/Api/ProductionSmokeTests.cs`  
**Category**: Deployment & Smoke Testing (`[Trait("Category", "Smoke")]`)  

---

## 1. Goal & Context
Post-deployment verification requires a tagged, production-safe suite of read-only tests. These tests execute against staging or production deployments to verify API routing, database connectivity, and identity resolution without placing orders, modifying DB records, or mutating state.

---

## 2. Test Cases to Implement

### Test 1: `GetAuthMe_ShouldReturn200OK_WithAuthenticatedIdentity`
- **Action**: `GET /auth/me` with valid bearer token.
- **Assertions**: `200 OK` returning `UserId`, `IsPrimaryOperator`, and identity claims.

### Test 2: `GetFirms_ShouldReturn200OK_WithRegisteredFirmList`
- **Action**: `GET /firms`.
- **Assertions**: `200 OK` returning non-empty list of supported prop firms.

### Test 3: `GetConnectionsAndAccounts_ShouldReturn200OK_WithoutMutatingState`
- **Action**: `GET /connections` and `GET /connections/{id}/accounts`.
- **Assertions**: `200 OK` returning connection roster.

### Test 4: `GetRiskProfile_ShouldReturn200OK_WithDeclaredLimits`
- **Action**: `GET /accounts/{id}/risk`.
- **Assertions**: `200 OK` returning declared risk rules.
