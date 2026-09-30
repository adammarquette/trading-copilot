# Code map — from a task to the code, the spec and the tests

> **Draft for gh#1220.** Verified against `develop` @ `1f9654bf`: every path and test name below was opened on disk.
> `R-#` are PRD requirements ([`trading-platform-prd.md`](trading-platform-prd.md)); ADR numbers are in
> [`adr/`](adr/README.md). Rows name types and directories rather than line numbers, which drift.

The routing map goes *document → section*. This page goes the other way: **which project implements a
requirement, which record specifies it, and which tests pin it** — the edge an agent otherwise finds by grepping.

## The safety spine, one hop each

Where the four safety-critical paths (root [`AGENTS.md`](../AGENTS.md)) live. **Two of them are split across
projects on purpose**: the pure logic is in `Domain`, the hosted services and endpoints are in `Api`.

| Concern | Open this | Project | Unit tests |
|---|---|---|---|
| Risk gate (R-5, R-16) | `Risk/RiskGate.cs`, `SanityCaps`, `PositionSizer` | Domain | `Domain/Risk/RiskGateTests.cs` |
| Order execution (R-11, R-12) | `Execution/OrderExecutionService.cs` | Domain | `Domain/Execution/OrderExecutionServiceTests.cs`, `…ModifyTests.cs` |
| Mode policy (R-14) | `Venue/TradingModePolicy.cs` | Domain | `Domain/Venue/TradingModePolicyTests.cs` |
| Kill switch | Seam: `Execution/IKillSwitch.cs`, `KillSwitchMode.cs` (Domain). Implementation: `Kill/KillSwitch.cs`, `KillSwitchService.cs`, `KillSwitchEndpoints.cs` (Api). State: `Entities/KillSwitchState.cs` (Data) | Domain + Api + Data | `Api/Kill/KillSwitchServiceTests.cs`; integration `KillSwitchIntegrationTests.cs` |
| Auto-flatten (R-13) | Pure logic: `Domain/Flatten/FlattenSchedule.cs`, `FlattenVerification`, `FlattenCheckIn`, `MarketClock`. Hosts: `Api/Flatten/AutoFlattenHost.cs`, `AutoFlattenService.cs`, `AutoFlattenWatchdogHost.cs`, `AutoFlattenWatchdogService.cs`, `DeadMansSwitchHost.cs` | Domain + Api | `Api/Flatten/AutoFlatten*Tests.cs`, `Domain/Flatten/FlattenScheduleTests.cs`; integration `AutoFlattenSchedulerIntegrationTests.cs`, `AutoFlattenWatchdogIntegrationTests.cs`, `DeadMansSwitchCheckInIntegrationTests.cs` |
| Chat cannot reach an order ([ADR-0025](adr/0025-chat-tool-read-only-boundary.md)) | `Api/Chat/Tools/` (`IChatTool` and its six tools) | Api | `Api/Chat/Tools/ChatToolBoundaryTests.cs` |

What the model is told versus what enforces it: [`prompt-vs-enforcement.md`](prompt-vs-enforcement.md).

## Projects

Project names drop the `MarqSpec.TradingCopilot.` prefix. **Carries** is the set of `R-#` the project's code and
comments name — a signal of what it implements, not a proof.

| Project | What it is | Carries | Specified by | Pinned by | Look here first |
|---|---|---|---|---|---|
| **Domain** | Pure library, no I/O. The venue seam interfaces (`ITradingVenue`, `IOrderExecutor`, `INewsSource`, `IContextMarketDataSource`) plus risk, execution, flatten, trigger and recovery logic | R-4, R-5, R-11, R-12, R-13, R-14, R-16, R-17 | [ADR-0007](adr/0007-order-execution-model.md), [0013](adr/0013-failure-recovery-model.md), [0008](adr/0008-ai-invocation-cost-model.md), [0001](adr/0001-event-backbone.md); data dictionary `03`, `04`, `05` | `UnitTests/Domain/{Risk,Execution,Flatten,Venue}` | `Risk/`, `Execution/`, `Venue/`, `Flatten/` |
| **Api** | ASP.NET Core BFF and host: endpoints, hosted services and the composition root (`Program.cs`, `StartupTasks.cs`). Feature folders `Orders`, `Kill`, `Flatten`, `Chat`, `Risk`, `Recovery`, `Realtime`, `Auth`, `Venues` | R-1…R-20, R-22 (heaviest: R-20, R-13, R-4) | ADR-0007, 0013, [0003](adr/0003-authentication.md), [0015](adr/0015-distribution-licensing-governance.md), [0017](adr/0017-single-operator-data-isolation.md), [0019](adr/0019-alerting-channel-and-thresholds.md), [0020](adr/0020-spa-served-by-the-bff.md), [0021](adr/0021-realtime-hub-contract.md), ADR-0025; data dictionary `04`, `05`, `12` | `UnitTests/Api/{Flatten,Kill,Chat/Tools,Orders,Risk}`; `IntegrationTests/Api` | `Orders/`, `Flatten/`, `Kill/`, `Chat/Tools/`, `Program.cs` |
| **Data** | EF Core layer: `TradingCopilotDbContext`, `Entities/`, `Migrations/`, `Events/`, `Tenancy/`, `Journal/` | R-20, R-14, R-4, R-9, R-15 | ADR-0001, ADR-0017, ADR-0007; data dictionary `01`…`12` | `UnitTests/Data` (`DataLayerScopingTests`, `Tenancy/`, `ChatPersistenceTests`); `IntegrationTests/Data` | `TradingCopilotDbContext.cs`, `Entities/`, `Tenancy/`, `Migrations/` |
| **Integration.ProjectX** | Venue adapter: `ProjectXVenue` implements `ITradingVenue` (TopstepX). Wraps `external/MarqSpec.Client.ProjectX` | R-17, R-14, R-8, R-9 | [ADR-0023](adr/0023-venue-setup-contract.md), [0016](adr/0016-venue-configuration.md), ADR-0007; PRD R-17; data dictionary `03` | `UnitTests/Integration/ProjectX` (`ProjectXVenueTests`, `…ConnectionTests`, `…SetupContractTests`, `…MappingTests`) | `ProjectXVenue.cs`, `ProjectXSetupContract.cs`, `ProjectXMapping.cs` |
| **Integration.Tradovate** | Venue adapter: `TradovateVenue` implements `ITradingVenue`, with socket sync and quote subscriptions. Wraps `external/MarqSpec.Client.Tradovate` | R-17, R-5, R-4, R-9, R-13 | ADR-0023, ADR-0016; PRD R-17 | `UnitTests/Integration/Tradovate` (`TradovateVenueTests`, `…SetupContractTests`, `…TradingSocketSyncTests`, `…MappingTests`) | `TradovateVenue.cs`, `TradovateSetupContract.cs` |
| **Integration.Finnhub** | **Data only, not a venue.** `FinnhubMarketDataSource` implements `IContextMarketDataSource` (cross-asset context trades only); `FinnhubNewsSource` is a news source. Wraps `external/MarqSpec.Client.Finnhub` | R-2, R-17 (seam) | PRD R-2, R-17; data dictionary `02`, `09` | `UnitTests/Integration/Finnhub` (`FinnhubMarketDataSourceTests`, `FinnhubNewsSourceTests`) | `FinnhubMarketDataSource.cs`, `FinnhubNewsSource.cs` |
| **Integration.Tiingo** | **Data only, not a venue and not a price source.** `TiingoNewsSource` implements `INewsSource`. Wraps `external/MarqSpec.Client.Tiingo` | R-2, R-17 (seam) | PRD R-2, R-17; data dictionary `09` | `UnitTests/Integration/Tiingo` (`TiingoNewsSourceTests`, `TiingoIsNotAPriceSourceTests`) | `TiingoNewsSource.cs` |
| **Client** | React / Vite / TypeScript PWA under `src/Client/src`, served by the Api ([ADR-0020](adr/0020-spa-served-by-the-bff.md)) | R-19, R-11, R-13, R-14, R-22 | [ADR-0005](adr/0005-ui-design-language.md), [0004](adr/0004-charting.md), [0006](adr/0006-multi-screen-workspace.md), [0010](adr/0010-progressive-web-app.md); PRD R-10, R-19 | Colocated `*.test.tsx` (e.g. `safety/KillSwitchControl.test.tsx`, `App.test.tsx`) | `src/safety`, `src/chart`, `src/orders`, `src/api` |
| **UnitTests** | xUnit; references Domain, Data, Api and all four `Integration.*` projects. Folders `Api`, `Data`, `Domain`, `Integration` | R-1…R-22 except R-3 and R-21 | ADR-0025, 0007, 0013 | It *is* the suite; spine classes are listed above | `Domain/Risk`, `Domain/Execution`, `Api/Flatten`, `Api/Chat/Tools` |
| **IntegrationTests** | TestHost-based; references Api, Data, Domain, ProjectX and Finnhub only (**not** Tradovate or Tiingo, which unit tests cover) | R-1…R-20, R-22 | [`integration-test-audit.md`](integration-test-audit.md) | It *is* the suite (`KillSwitchIntegrationTests`, `AutoFlatten*IntegrationTests`, `MultiTenantIsolationIntegrationTests`, `AuthorizationSurfaceIntegrationTests`) | `Api/`, `TestHost/` |
| **`infra/`** | An AWS CDK app in C# (`Infra`: `EnvironmentStack`, `GitHubOidcStack`; `Infra.Tests`). **Plan withdrawn 2026-09-29 (gh#1215); the code stays until its removal is decided** | — | [ADR-0030](adr/0030-aws-deployment-topology.md); [runbook](deployment-runbook.md) | `Infra.Tests` (`AlarmTests`, `NetworkTests`, `EdgeTests`, `StatefulResourceTests`, `GitHubOidcStackTests`) | `EnvironmentStack.cs`, `Program.cs` |

**`external/`** holds four client submodules, each wrapped by exactly one `Integration.*` project:
`MarqSpec.Client.ProjectX`, `.Tradovate`, `.Finnhub`, `.Tiingo`. A fresh clone has them empty until
`git submodule update --init`.

## Things that are not where the name suggests

- **R-21 (strategy templates) has no code home**, and **R-3 (order-flow analytics) is essentially unbuilt** — it
  appears in code only in comments about kinds not yet built.
- **The data dictionary has no page `11`**; the files run `01`…`10`, then `12-chat-audit`.
- **Finnhub and Tiingo cite no ADR in code**; their spec is the PRD and the data dictionary.
- **The watchdog's specifying ADR was not pinned down**: its code cites [ADR-0013](adr/0013-failure-recovery-model.md)
  broadly, and which section specifies it was not verified.

## Keeping this current

A change that **adds, renames or removes a project under `src/`** updates its row in the same PR, as does one that
moves a safety-spine type between projects or renames a spine test class. Rows name types and directories, not line
numbers; verify a cell by opening what it names, not by copying an ADR's description of it.
