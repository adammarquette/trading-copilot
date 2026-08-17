using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Kill;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>decision-state rehydration across a restart</b> (gh#223, gh#221, R-20, R-12, R-13,
/// ADR-0013): the decision surface — staged orders, pending conditionals, hidden/orphaned stops, suggestions, the
/// kill-switch lock — comes back <b>inert</b> after a process restart. Nothing silently resumes; per-operator
/// isolation (R-20) holds through the restart; and an <i>inconsistent</i> surface trips the fail-safe (kill switch
/// engaged, HaltOnly) rather than being quietly repaired.
/// </summary>
/// <remarks>
/// The load-bearing harness: the <see cref="PostgresApiFactory"/> Postgres container outlives the host, so a second
/// host built via <c>CreateHost</c> re-runs <c>StartupTasks</c> (migrate → kill-switch rehydrate → decision-state
/// rehydrate) against the <b>same database</b> — that IS the restart. Because the guard runs deployment-wide over a
/// shared container, each test resets the surface (rows + the singleton kill-switch row) before seeding its own.
/// Traces R-20 · R-12 · R-13 · ADR-0013 · ADR-0017.
/// </remarks>
public class StateRehydrationIntegrationTests : IClassFixture<RehydrationTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const decimal Entry = 5_000m;
    private const decimal ActualStop = 4_990m;
    private const decimal SafetyStop = 4_980m;

    private readonly RehydrationTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public StateRehydrationIntegrationTests(RehydrationTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Rehydration_ShouldLeaveStagedOrderStaged_WhenHostRestarts()
    {
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid orderId = await ArmStagedOrderAsync(accountId);

        await WithRestartAsync(async rebooted =>
        {
            (await GetKillSwitchAsync(rebooted)).Engaged.Should().BeFalse("a clean decision surface rehydrates inert — no fail-safe");
        });

        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Staged, "the staged order is neither auto-sent nor cancelled by a restart");
        (await VenueKeyAsync(orderId)).Should().BeNull("nothing was transmitted — the order still requires an explicit take");
    }

    [Fact]
    public async Task Rehydration_ShouldNotResurrectExpiredSuggestionAsTakeable_WhenValidityWindowPassed()
    {
        // A suggestion→order path now EXISTS (gh#548: POST /suggestions/{id}/take), so the old "inert data with no
        // endpoint" reading of this guard is retired. What still holds — and is what this pins — is the property
        // that matters on restart: no path SILENTLY transmits a suggestion. A take is an explicit operator action
        // that merely ARMS an inert `Staged` ticket, itself requiring a separate, fully gated send, and rehydration
        // invokes neither. So a Stale / ExpiredVoid suggestion still comes back as data and nothing else: the
        // order count is unmoved and the lifecycle state is untouched. The take path's own refusals (a non-Active
        // suggestion can never be armed at all) are covered independently by SuggestionTakeIntegrationTests
        // (gh#614); this case guards the restart edge those cannot reach.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid suggestionId = await SeedSuggestionAsync(accountId, operatorId, SuggestionState.ExpiredVoid);
        long ordersBefore = await OrderCountAsync();

        await WithRestartAsync(async rebooted =>
        {
            (await GetKillSwitchAsync(rebooted)).Engaged.Should().BeFalse("an expired suggestion is not an inconsistency — it must not trip the fail-safe");
        });

        (await SuggestionStateAsync(suggestionId)).Should().Be(SuggestionState.ExpiredVoid, "the expired suggestion is untouched by rehydration");
        (await OrderCountAsync()).Should().Be(ordersBefore, "rehydration resurrects no suggestion into a takeable/transmitted order");
    }

    [Fact]
    public async Task Rehydration_ShouldKeepConditionalPending_WhenHostRestarts()
    {
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid conditionalId = await SeedPendingConditionalAsync(accountId, operatorId);

        await WithRestartAsync(async rebooted =>
        {
            (await GetKillSwitchAsync(rebooted)).Engaged.Should().BeFalse("a pending conditional is a consistent surface — no fail-safe");
        });

        (await ConditionalStatusAsync(conditionalId)).Should().Be(
            ConditionalStatus.Pending, "a pending conditional survives the restart as Pending — it fires only on a genuine trigger, re-gated");
    }

    [Fact]
    public async Task Rehydration_ShouldPreserveKillSwitchLock_WhenHostRestarts()
    {
        // Regression guard on gh#189: a crash or redeploy never silently re-enables trading.
        await FreshAccountAsync();
        await SeedKillSwitchAsync(engaged: true, KillSwitchMode.FlattenAll, "engaged before the restart");

        await WithRestartAsync(async rebooted =>
        {
            KillSwitchStateResponse state = await GetKillSwitchAsync(rebooted);
            state.Engaged.Should().BeTrue("the operator's lock survives the restart — rehydrated from the durable row (ADR-0013)");
            state.Mode.Should().Be(nameof(KillSwitchMode.FlattenAll));
        });
    }

    [Fact]
    public async Task Rehydration_ShouldKeepOtherOperatorsStateInvisible_WhenHostRestarts()
    {
        // R-20 default-deny survives the restart: another operator cannot reach the owner's staged order.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid ownersOrder = await ArmStagedOrderAsync(accountId);
        (string intruderEmail, string intruderPassword) = await CreateSecondOperatorAsync();

        await using WebApplicationFactory<Program> restarted = _factory.CreateHost(DeploymentEnvironment.Staging);
        HttpClient intruder = restarted.CreateClient();
        intruder.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(intruder, intruderEmail, intruderPassword));

        using HttpResponseMessage take = await intruder.PostAsync($"/orders/{ownersOrder}/take", content: null);
        take.StatusCode.Should().Be(HttpStatusCode.NotFound, "R-20 holds through the restart — another operator cannot even see the owner's order");
        (await OrderStatusAsync(ownersOrder)).Should().Be(OrderStatus.Staged, "and the owner's order is untouched");
    }

    [Fact]
    public async Task Rehydration_ShouldNotPromoteOrphanedStopSilently_WhenHostRestartsWhileDisconnected()
    {
        // On restart with a dead venue connection, an orphaned stop must not be silently promoted. Promotion is
        // Hidden-only by construction; a hidden control on the same promoting quote proves the quote WOULD promote.
        (Guid accountId, string venueKey) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid hiddenPlan = await SeedStopPlanAsync(accountId, operatorId, StopStaging.Hidden);
        Guid orphanedPlan = await SeedStopPlanAsync(accountId, operatorId, StopStaging.Orphaned);

        await WithRestartAsync(async rebooted =>
        {
            await Task.CompletedTask; // the restart itself must not promote anything
        });
        (await StagingAsync(orphanedPlan)).Should().Be(StopStaging.Orphaned, "the restart pass never promotes an orphaned stop");

        // The hidden control promotes only for a position the venue still reports open (position-aware promotion,
        // gh#263) — a long, so a +1 net. Without it the control fails closed to not-promoting and cannot witness
        // that the quote promotes. The orphaned stop stays orphaned regardless (Hidden-only promotion).
        VenueFactory.SeedPosition(venueKey, Contract, netQuantity: 1);

        int promoted = await PromoteAsync(bid: 4_991m, ask: 4_991.25m);

        promoted.Should().Be(1, "only the hidden control promotes on the promoting quote");
        (await StagingAsync(hiddenPlan)).Should().Be(StopStaging.Native, "the hidden control proves the quote promotes");
        (await StagingAsync(orphanedPlan)).Should().Be(StopStaging.Orphaned, "an orphaned stop is never promotable on a dead connection");
    }

    [Fact]
    public async Task Rehydration_ShouldFailSafe_WhenStateIsInconsistent()
    {
        // An impossible combination — a Staged order that nonetheless carries a venue key — must end no-new-orders
        // and LOUD (kill switch engaged, HaltOnly), never silently repaired.
        (Guid accountId, string _) = await FreshAccountAsync();
        Guid operatorId = await OperatorIdAsync();
        await SeedStagedOrderWithVenueKeyAsync(accountId, operatorId);

        await WithRestartAsync(async rebooted =>
        {
            KillSwitchStateResponse state = await GetKillSwitchAsync(rebooted);
            state.Engaged.Should().BeTrue("an inconsistent decision surface fails safe — the kill switch is engaged at startup");
            state.Mode.Should().Be(nameof(KillSwitchMode.HaltOnly), "rehydration halts new outbound and leaves positions on their native stops");
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<(Guid AccountId, string VenueKey)> FreshAccountAsync()
    {
        await ResetStateAsync();
        HttpClient client = await AuthenticatedClientAsync();
        return await SetupAccountAsync(client);
    }

    private async Task ResetStateAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.ConditionalOrders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.KillSwitchStates.ExecuteDeleteAsync(); // clear a prior fail-safe lock so the next restart starts clean
        });
        VenueFactory.ResetPositions();
    }

    private async Task WithRestartAsync(Func<HttpClient, Task> body)
    {
        await using WebApplicationFactory<Program> restarted = _factory.CreateHost(DeploymentEnvironment.Staging);
        HttpClient rebooted = restarted.CreateClient(); // building the host re-runs StartupTasks — the "restart"
        rebooted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await LoginAsync(rebooted, PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        await body(rebooted);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await LoginAsync(client, PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        return client;
    }

    private async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private async Task<(string Email, string Password)> CreateSecondOperatorAsync()
    {
        string email = $"intruder-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        HttpClient owner = await AuthenticatedClientAsync();

        using HttpResponseMessage invite = await owner.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        invite.EnsureSuccessStatusCode();
        IssueInvitationResponse? issued = await invite.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(issued);

        using HttpResponseMessage accept = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(issued.Token, password, "Intruder"));
        accept.EnsureSuccessStatusCode();
        return (email, password);
    }

    private async Task<(Guid AccountId, string VenueKey)> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Rehydrate-{Guid.NewGuid():N}", FirmType.PropFirm));
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

    private async Task<Guid> ArmStagedOrderAsync(Guid accountId)
    {
        HttpClient client = await AuthenticatedClientAsync();
        await DeclareRiskProfileAsync(client, accountId);

        using HttpResponseMessage arm = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", ValidProposal());
        arm.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's order must arm cleanly");
        StagedOrderResponse? staged = await arm.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest request = new(
            DailyLossLimit: 1_000m, AccountProfitTarget: 3_000m, StartingBalance: 50_000m, FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday, TrailingAmount: 2_000m, LocksAt: 50_100m, PerTradeRiskFraction: 0.15m,
            TargetRewardRatio: 1.5m, MaxDrawdownPerTrade: 300m, DailyDrawdownGovernor: 600m, DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true, SizingBasis: SizingBasis.SafetyStop, MaxContractsPerOrder: 5, MaxBestDayFraction: 0.4m);
        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", request);
        response.EnsureSuccessStatusCode();
    }

    private static SendOrderRequest ValidProposal() => new(
        Symbol: "MES", TickSize: 0.25m, PointValue: 5m, Side: OrderSide.Buy, Quantity: 1,
        Entry: Entry, Stop: ActualStop, SafetyStop: SafetyStop, ReferencePrice: Entry, Type: OrderType.Market);

    private async Task SeedStagedOrderWithVenueKeyAsync(Guid accountId, Guid operatorId) =>
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(NewOrder(accountId, operatorId, OrderStatus.Staged, venueOrderKey: "STUB-INCONSISTENT-KEY"));
            await db.SaveChangesAsync();
        });

    private static Order NewOrder(Guid accountId, Guid operatorId, OrderStatus status, string? venueOrderKey = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = operatorId,
        AccountId = accountId,
        Instrument = Contract,
        Symbol = "ES",
        Side = OrderSide.Buy,
        Size = 1,
        Type = OrderType.Market,
        EntryPrice = Entry,
        WorkingStopPrice = ActualStop,
        SafetyStopPrice = SafetyStop,
        ReferencePrice = Entry,
        TickSize = 0.25m,
        PointValue = 5m,
        Status = status,
        Mode = TradingMode.Practice,
        VenueOrderKey = venueOrderKey,
        PlacedAt = DateTimeOffset.UtcNow,
    };

    private async Task<Guid> SeedStopPlanAsync(Guid accountId, Guid operatorId, StopStaging staging)
    {
        Guid orderId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            Order order = NewOrder(accountId, operatorId, OrderStatus.Working);
            order.Id = orderId;
            db.Orders.Add(order);
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = planId,
                UserId = operatorId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = Entry,
                ActualStopPrice = ActualStop,
                SafetyStopPrice = SafetyStop,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8m,
                Staging = staging,
            });
            await db.SaveChangesAsync();
        });
        return planId;
    }

    private async Task<Guid> SeedPendingConditionalAsync(Guid accountId, Guid operatorId)
    {
        Guid conditionalId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.ConditionalOrders.Add(new ConditionalOrderRecord
            {
                Id = conditionalId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = Contract,
                Symbol = "ES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = Entry,
                WorkingStopPrice = ActualStop,
                SafetyStopPrice = SafetyStop,
                ReferencePrice = Entry,
                TickSize = 0.25m,
                PointValue = 5m,
                TriggerPrice = 5_010m,
                TriggerDirection = ConditionalCrossDirection.RisesTo,
                Status = ConditionalStatus.Pending,
                Mode = TradingMode.Practice,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return conditionalId;
    }

    private async Task<Guid> SeedSuggestionAsync(Guid accountId, Guid operatorId, SuggestionState state)
    {
        Guid suggestionId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Suggestions.Add(new Suggestion
            {
                Id = suggestionId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = Contract,
                Side = OrderSide.Buy,
                Size = 1,
                EntryPrice = Entry,
                StopPrice = ActualStop,
                TargetPrice = 5_020m,
                Mode = TradingMode.Practice,
                State = state,
                CreatedAt = DateTimeOffset.UtcNow,

                // Required since gh#542/#543/#544. Mechanical fill-in only — this suite asserts rehydration and
                // suggestion inertness, neither of which these fields affect. ExpiresAt must clear the
                // CK_Suggestions_ExpiresAfterCreated CHECK, so it sits strictly after CreatedAt.
                Rationale = "seeded",
                CitedFactors =
                [
                    new CitedFactor
                    {
                        Id = Guid.NewGuid(),
                        UserId = operatorId,
                        Kind = CitedFactorKind.Indicator,
                        IsPrimary = true,
                        TimeframeMinutes = 1,
                        Indicator = "rsi",
                        Period = 14,
                    },
                ],
                Confidence = 50,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        });
        return suggestionId;
    }

    private async Task SeedKillSwitchAsync(bool engaged, KillSwitchMode mode, string reason) =>
        await ExecuteDbAsync(async db =>
        {
            db.KillSwitchStates.Add(new KillSwitchState
            {
                Id = KillSwitchState.SingletonId,
                Engaged = engaged,
                Mode = mode,
                EngagedAt = engaged ? new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero) : null,
                Reason = engaged ? reason : null,
                UpdatedAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            });
            await db.SaveChangesAsync();
        });

    private async Task<int> PromoteAsync(decimal bid, decimal ask)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        StopPromotionService promotion = scope.ServiceProvider.GetRequiredService<StopPromotionService>();
        AdversarialTestProjectXVenueFactory venueFactory = scope.ServiceProvider.GetRequiredService<AdversarialTestProjectXVenueFactory>();
        ITradingVenue venue = venueFactory.Create(FirmConventions.None);
        return await promotion.PromoteForQuoteAsync(VenueId.Parse("projectx"), Contract, bid, ask, venue, CancellationToken.None);
    }

    private async Task<KillSwitchStateResponse> GetKillSwitchAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/kill-switch");
        response.EnsureSuccessStatusCode();
        KillSwitchStateResponse? state = await response.Content.ReadFromJsonAsync<KillSwitchStateResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(state);
        return state;
    }

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<OrderStatus> OrderStatusAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync());

    private Task<string?> VenueKeyAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.VenueOrderKey).SingleAsync());

    private Task<long> OrderCountAsync() => QueryDbAsync(db => db.Orders.IgnoreQueryFilters().LongCountAsync());

    private Task<ConditionalStatus> ConditionalStatusAsync(Guid id) => QueryDbAsync(db =>
        db.ConditionalOrders.IgnoreQueryFilters().Where(c => c.Id == id).Select(c => c.Status).SingleAsync());

    private Task<SuggestionState> SuggestionStateAsync(Guid id) => QueryDbAsync(db =>
        db.Suggestions.IgnoreQueryFilters().Where(s => s.Id == id).Select(s => s.State).SingleAsync());

    private Task<StopStaging> StagingAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(p => p.Id == planId).Select(p => p.Staging).SingleAsync());

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
