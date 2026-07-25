using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>kill switch</b> (gh#190, gh#189, R-11, ADR-0007) against real PostgreSQL: the
/// operator's process-wide panic control. Driven over HTTP (<c>POST /kill-switch</c> hold-to-confirm,
/// <c>/disengage</c>, <c>GET</c>) — it disables outbound at <c>OrderExecutionService</c>, cancels working orders,
/// flattens-all or halts-only, journals <c>killswitch.*</c>, and <b>rehydrates the engaged state at host startup</b>
/// so the operator's lock survives a restart (ADR-0013). Reuses the shared venue-position stub (gh#186) for the
/// flatten path; the always-on hosts are suppressed by <see cref="FlattenTestPostgresFactory"/>.
/// </summary>
/// <remarks>
/// The switch is a process-wide singleton over a single deployment-wide row, so these tests are not account-isolated
/// like the others: each engaging test disengages in a <c>finally</c> to avoid leaking the lock to its siblings.
/// Traces R-11 · ADR-0007 · ADR-0013.
/// </remarks>
public class KillSwitchIntegrationTests : IClassFixture<FlattenTestPostgresFactory>
{
    private readonly FlattenTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public KillSwitchIntegrationTests(FlattenTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Engage_ShouldDisableOutbound_SoASubsequentSendIsRefused()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);

        try
        {
            using HttpResponseMessage engage = await client.PostAsJsonAsync(
                "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.FlattenAll, Confirmed: true, Reason: "panic"));
            engage.StatusCode.Should().Be(HttpStatusCode.OK);
            (await GetStateAsync(client)).Engaged.Should().BeTrue();
            (await EventCountForAsync(KillSwitchService.EngagedEventType)).Should().BeGreaterThan(0);

            long ordersBefore = await QueryDbAsync(db => db.Orders.IgnoreQueryFilters().LongCountAsync());
            long decisionsBefore = await QueryDbAsync(db => db.GateDecisions.IgnoreQueryFilters().LongCountAsync());
            using HttpResponseMessage send = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", ValidProposal());
            send.StatusCode.Should().Be(HttpStatusCode.Conflict, "outbound is disabled while the kill switch is engaged");
            SendOrderResponse? result = await send.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
            result!.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByKillSwitch));
            (await QueryDbAsync(db => db.Orders.IgnoreQueryFilters().LongCountAsync()))
                .Should().Be(ordersBefore, "a refused-by-kill-switch send transmits nothing and journals no order");
            (await QueryDbAsync(db => db.GateDecisions.IgnoreQueryFilters().LongCountAsync()))
                .Should().Be(decisionsBefore, "the refusal precedes sizing — no gate decision is ever recorded for it (#190 'refused before sizing')");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Engage_ShouldRequireHoldToConfirm_AndNotEngage_WhenNotConfirmed()
    {
        HttpClient client = await AuthenticatedClientAsync();
        await EnsureDisengagedAsync(client);

        using HttpResponseMessage engage = await client.PostAsJsonAsync(
            "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.FlattenAll, Confirmed: false, Reason: "oops"));

        engage.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a panic action must not fire from an unconfirmed request (R-11)");
        (await GetStateAsync(client)).Engaged.Should().BeFalse("an unconfirmed request must not engage");
    }

    [Fact]
    public async Task Engage_ShouldCancelWorkingOrders()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await ArmAndTakeAsync(client, accountId); // a resting Working order at the venue

        try
        {
            using HttpResponseMessage engage = await client.PostAsJsonAsync(
                "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: null));
            EngageKillSwitchResponse? report = await engage.Content.ReadFromJsonAsync<EngageKillSwitchResponse>(_jsonOptions);
            report!.CancelledOrders.Should().BeGreaterThan(0, "engaging cancels resting working orders");

            OrderStatus status = await QueryDbAsync(db =>
                db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync());
            status.Should().Be(OrderStatus.Cancelled, "the working order is cancelled by the kill switch");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Engage_FlattenAll_ShouldCloseOpenPositions()
    {
        HttpClient client = await AuthenticatedClientAsync();
        string venueKey = await SetupTradeableAccountAsyncReturningKey(client);
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);

        try
        {
            using HttpResponseMessage engage = await client.PostAsJsonAsync(
                "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.FlattenAll, Confirmed: true, Reason: null));
            EngageKillSwitchResponse? report = await engage.Content.ReadFromJsonAsync<EngageKillSwitchResponse>(_jsonOptions);
            report!.FlattenedPositions.Should().BeGreaterThan(0, "flatten-all closes every open position");
            VenueFactory.ClosePositionCalls.Should().Contain(call => call.ContractKey == "ESM25");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Engage_HaltOnly_ShouldLeaveOpenPositionsOnTheirStops()
    {
        HttpClient client = await AuthenticatedClientAsync();
        string venueKey = await SetupTradeableAccountAsyncReturningKey(client);
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);

        try
        {
            using HttpResponseMessage engage = await client.PostAsJsonAsync(
                "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: null));
            EngageKillSwitchResponse? report = await engage.Content.ReadFromJsonAsync<EngageKillSwitchResponse>(_jsonOptions);
            report!.FlattenedPositions.Should().Be(0, "halt-only leaves open positions on their native safety stops");
            VenueFactory.ClosePositionCalls.Should().BeEmpty("halt-only closes nothing");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Engage_ShouldRefuseAStagedTake_WhenEngaged()
    {
        // The enforcement is one choke point in the send path — so the take route must be refused too, not just
        // the direct send. Arm first (a Staged row is untouched by the sweep), then engage, then take.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await ArmAsync(client, accountId);

        try
        {
            using HttpResponseMessage engage = await client.PostAsJsonAsync(
                "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: null));
            engage.EnsureSuccessStatusCode();

            using HttpResponseMessage take = await client.PostAsync($"/orders/{orderId}/take", content: null);
            take.IsSuccessStatusCode.Should().BeFalse("taking a staged order is an outbound path — it is refused while engaged");
            SendOrderResponse? result = await take.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
            result!.Outcome.Should().Be(nameof(ExecutionOutcome.RefusedByKillSwitch));

            string status = await QueryDbAsync(db =>
                db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.Status.ToString()).SingleAsync());
            status.Should().Be(nameof(OrderStatus.Staged), "a refused take transmits nothing — the ticket stays staged");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Engage_ShouldDefaultToFlattenAll_WhenModeUnspecified()
    {
        // FlattenAll is the enum's zero value on purpose: an engage request that omits the mode must fail SAFE —
        // close everything, not halt-and-leave. Sent as raw JSON with no "mode" field.
        HttpClient client = await AuthenticatedClientAsync();
        string venueKey = await SetupTradeableAccountAsyncReturningKey(client);
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);

        try
        {
            using StringContent body = new("{\"confirmed\":true}", System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage engage = await client.PostAsync("/kill-switch", body);
            engage.EnsureSuccessStatusCode();
            EngageKillSwitchResponse? report = await engage.Content.ReadFromJsonAsync<EngageKillSwitchResponse>(_jsonOptions);
            report!.Mode.Should().Be(nameof(KillSwitchMode.FlattenAll), "an unspecified mode defaults to the fail-safe FlattenAll");
            report.FlattenedPositions.Should().BeGreaterThan(0, "the fail-safe default closes open positions");
            VenueFactory.ClosePositionCalls.Should().Contain(call => call.ContractKey == "ESM25");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Engage_ShouldNotBlockAutoFlatten_WhenEngaged()
    {
        // The deliberate exemption (ADR-0007): reducing / protective paths do NOT go through the outbound choke
        // point, so a killed system can still flatten at the deadline (R-13 outranks the halt). Engage halt-only
        // (so the engage itself closes nothing), then drive the auto-flatten pass past the ES deadline.
        HttpClient client = await AuthenticatedClientAsync();
        string venueKey = await SetupTradeableAccountAsyncReturningKey(client);
        VenueFactory.SeedPosition(venueKey, "ESM25", netQuantity: 2);

        try
        {
            using HttpResponseMessage engage = await client.PostAsJsonAsync(
                "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.HaltOnly, Confirmed: true, Reason: null));
            engage.EnsureSuccessStatusCode();
            VenueFactory.ClosePositionCalls.Should().BeEmpty("halt-only engage closes nothing itself");

            int executedBefore = await EventCountForAsync(AutoFlattenService.ExecutedEventType);
            await RunAutoFlattenPassAsync(Utc(19, 45)); // 14:45 CT — past the 14:30 ES deadline

            VenueFactory.ClosePositionCalls.Should().Contain(
                call => call.ContractKey == "ESM25", "auto-flatten still closes the position despite the kill switch");
            (await EventCountForAsync(AutoFlattenService.ExecutedEventType) - executedBefore)
                .Should().Be(1, "the reducing path is exempt — a killed system must still flatten");
        }
        finally
        {
            await DisengageAsync(client);
        }
    }

    [Fact]
    public async Task Disengage_ShouldReEnableOutbound()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client);
        await DeclareRiskProfileAsync(client, accountId);

        using (HttpResponseMessage engage = await client.PostAsJsonAsync(
            "/kill-switch", new EngageKillSwitchRequest(KillSwitchMode.FlattenAll, Confirmed: true, Reason: null)))
        {
            engage.EnsureSuccessStatusCode();
        }

        int disengagedBefore = await EventCountForAsync(KillSwitchService.DisengagedEventType);
        await DisengageAsync(client);

        (await GetStateAsync(client)).Engaged.Should().BeFalse();
        (await EventCountForAsync(KillSwitchService.DisengagedEventType) - disengagedBefore).Should().Be(1, "disengaging is journalled");

        // Outbound is allowed again — a send now reaches the gate rather than the kill-switch refusal.
        using HttpResponseMessage send = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", ValidProposal());
        SendOrderResponse? result = await send.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        result!.Outcome.Should().NotBe(nameof(ExecutionOutcome.RefusedByKillSwitch), "outbound is re-enabled after disengage");
    }

    [Fact]
    public async Task Startup_ShouldRehydrateTheEngagedState_FromTheDurableRow()
    {
        HttpClient primary = await AuthenticatedClientAsync();
        await EnsureDisengagedAsync(primary);

        // Seed the durable lock directly, as if a prior process had engaged it before a restart.
        await ExecuteDbAsync(async db =>
        {
            KillSwitchState? row = await db.KillSwitchStates.FirstOrDefaultAsync();
            if (row is null)
            {
                row = new KillSwitchState { Id = KillSwitchState.SingletonId };
                db.KillSwitchStates.Add(row);
            }

            row.Engaged = true;
            row.Mode = KillSwitchMode.FlattenAll;
            row.EngagedAt = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
            row.Reason = "engaged before the restart";
            row.UpdatedAt = row.EngagedAt.Value;
            await db.SaveChangesAsync();
        });

        try
        {
            // Boot a FRESH host on the same container: its StartupTasks must rehydrate the engaged lock.
            await using WebApplicationFactory<Program> restarted = _factory.CreateHost(DeploymentEnvironment.Staging);
            HttpClient rebooted = restarted.CreateClient();
            string token = await LoginAsync(rebooted);
            rebooted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            KillSwitchStateResponse state = await GetStateAsync(rebooted);
            state.Engaged.Should().BeTrue("the operator's lock survives a restart — rehydrated from the durable row (ADR-0013)");
            state.Mode.Should().Be(nameof(KillSwitchMode.FlattenAll));
        }
        finally
        {
            // Reset the durable row itself (not just the runtime flag) so the seeded lock never leaks.
            await DisengageAsync(primary);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));
        return client;
    }

    private async Task<string> LoginAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private async Task<string> SetupTradeableAccountAsyncReturningKey(HttpClient client)
    {
        (Guid _, string key) = await SetupAccountAsync(client);
        return key;
    }

    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client)
    {
        (Guid id, string _) = await SetupAccountAsync(client);
        return id;
    }

    private async Task<(Guid AccountId, string VenueKey)> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Kill-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse tradeable = accounts.First(a => a.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest request = new(
            DailyLossLimit: 1_000m, AccountProfitTarget: 3_000m, FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday, TrailingAmount: 2_000m, LocksAt: 50_100m,
            PerTradeRiskFraction: 0.15m, TargetRewardRatio: 1.5m, MaxDrawdownPerTrade: 300m,
            DailyDrawdownGovernor: 600m, DailyProfitTarget: 1_500m, StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop, MaxContractsPerOrder: 5, MaxBestDayFraction: 0.4m, StartingBalance: 50_000m);
        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId)
    {
        using HttpResponseMessage arm = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", ValidProposal());
        arm.EnsureSuccessStatusCode();
        StagedOrderResponse? staged = await arm.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    private async Task<Guid> ArmAndTakeAsync(HttpClient client, Guid accountId)
    {
        Guid orderId = await ArmAsync(client, accountId);
        using HttpResponseMessage take = await client.PostAsync($"/orders/{orderId}/take", content: null);
        take.StatusCode.Should().Be(HttpStatusCode.OK, "the loose profile takes the 1-lot to a working order");
        return orderId;
    }

    // Summer date → CDT (UTC−5): ES closes 14:30 CT = 19:30 UTC. Mirrors the gh#186 flatten suite.
    private static DateTimeOffset Utc(int hour, int minute) => new(2026, 7, 15, hour, minute, 0, TimeSpan.Zero);

    private async Task RunAutoFlattenPassAsync(DateTimeOffset now)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AutoFlattenService service = scope.ServiceProvider.GetRequiredService<AutoFlattenService>();
        await service.RunPassAsync(now, CancellationToken.None);
    }

    private static SendOrderRequest ValidProposal() => new(
        Symbol: "MES", TickSize: 0.25m, PointValue: 5m, Side: OrderSide.Buy, Quantity: 1,
        Entry: 5_000m, Stop: 4_990m, SafetyStop: 4_980m, ReferencePrice: 5_000m, Type: OrderType.Market);

    private async Task<KillSwitchStateResponse> GetStateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/kill-switch");
        response.EnsureSuccessStatusCode();
        KillSwitchStateResponse? state = await response.Content.ReadFromJsonAsync<KillSwitchStateResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(state);
        return state;
    }

    private async Task DisengageAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/kill-switch/disengage", content: null);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureDisengagedAsync(HttpClient client)
    {
        if ((await GetStateAsync(client)).Engaged)
        {
            await DisengageAsync(client);
        }
    }

    private Task<int> EventCountForAsync(string type) =>
        QueryDbAsync(db => db.Events.CountAsync(candidate => candidate.Type == type));

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
