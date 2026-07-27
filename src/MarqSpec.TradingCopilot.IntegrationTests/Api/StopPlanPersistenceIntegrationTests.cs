using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the staged-stop plan (ADR-0007, gh#11 / gh#153) against real PostgreSQL with the
/// <b>applied</b> EF Core migrations — the four CHECK constraints from <c>d474d8a</c> live in the schema, not
/// merely in a model builder. Two tests drive the persistence through the send API; the rest write directly to
/// <see cref="TradingCopilotDbContext"/> because the whole point is to prove the <i>database</i> refuses what the
/// domain would never emit — bypassing the domain guard is the scenario (gh#158).
/// </summary>
/// <remarks>
/// The safety-beyond-actual invariant is side-dependent — safety below the working stop for a long, above it for
/// a short — so a CHECK that is correct for one side and inverted for the other passes every same-side test.
/// This suite exercises <b>both</b> sides (tests 3 and 4) with a positive control (test 5), and asserts every
/// constraint by <c>SqlState</c> + name so a row that fails on an incidental FK/NOT NULL never masquerades as a
/// passing guard. Traces R-5, R-11 · ADR-0007 · Engineering guide §5.
/// </remarks>
public class StopPlanPersistenceIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    /// <summary>PostgreSQL <c>check_violation</c> — the SqlState a CHECK constraint raises, distinct from an FK
    /// (23503) or NOT NULL (23502) failure. Asserting on it stops a row failing for the wrong reason.</summary>
    private const string CheckViolationSqlState = "23514";

    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public StopPlanPersistenceIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Persistence through the API — the endpoint that records a plan (OrderEndpoints.AddStopPlan).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TransmittedEntry_ShouldPersistStopPlan_LinkedToItsOrder()
    {
        HttpClient client = await CreateAuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, "Topstep-StopPlanPersist");
        await DeclareRiskProfileAsync(client, accountId);

        // A long with the working stop (4990) strictly inside the safety stop (4980): a stageable, hidden plan.
        SendOrderRequest request = ValidOrderRequest();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SendOrderResponse? sent = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(sent);
        sent.Outcome.Should().Be("Placed");
        sent.OrderId.Should().NotBeNull();
        Guid orderId = sent.OrderId!.Value;

        Guid operatorId = await GetOperatorIdAsync();

        await ExecuteDbContextAsync(async db =>
        {
            List<StopPlanRecord> plans = await db.StopPlans.IgnoreQueryFilters()
                .Where(p => p.OrderId == orderId)
                .ToListAsync();
            plans.Should().ContainSingle("a transmitted entry with distinct working and safety stops stages exactly one plan");

            StopPlanRecord plan = plans[0];
            plan.UserId.Should().Be(operatorId, "the plan is owned by the operator who placed the order (R-20)");
            plan.Side.Should().Be(OrderSide.Buy);
            plan.EntryPrice.Should().Be(5_000m, "the entry price round-trips as decimal, not float");
            plan.ActualStopPrice.Should().Be(4_990m, "the ACTUAL (working) stop is the hidden one, not the safety stop (gh#134)");
            plan.SafetyStopPrice.Should().Be(4_980m, "the safety stop rests natively beyond the working stop");
            plan.ProximityMetric.Should().Be(StopProximityMetric.Ticks, "the send path stages a tick band, never ATR");
            plan.ProximityValue.Should().Be(8m, "the ExecutionOptions.StopPromotionTicks default (8) rides the plan as its promotion band");
            plan.Staging.Should().Be(StopStaging.Hidden, "the working stop starts hidden; only the safety stop is native at entry");
        });
    }

    [Fact]
    public async Task CoincidentStops_ShouldSkipStopPlan_WhenTheSingleStopAlreadyRestsNatively()
    {
        HttpClient client = await CreateAuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, "Topstep-StopPlanSkip");
        await DeclareRiskProfileAsync(client, accountId);

        // Working stop == safety stop: the operator's stop already IS the safety stop and rests natively, so
        // there is nothing to stage. A hidden stop needs a safety stop strictly beyond it.
        SendOrderRequest request = ValidOrderRequest() with { Stop = 4_980m, SafetyStop = 4_980m };
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SendOrderResponse? sent = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(sent);
        sent.Outcome.Should().Be("Placed", "coincident stops are a valid two-leg bracket, not a refusal");
        Guid orderId = sent.OrderId!.Value;

        await ExecuteDbContextAsync(async db =>
        {
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.Id == orderId))
                .Should().BeTrue("the order itself is still journaled");
            (await db.StopPlans.IgnoreQueryFilters().AnyAsync(p => p.OrderId == orderId))
                .Should().BeFalse("no plan is staged when the working stop coincides with the safety stop");
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // The database CHECK constraints — proven against the applied migration, both sides, by name.
    // Each seeds a real parent Order first, so the only thing the insert can fail on is the CHECK under test.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Database_ShouldRejectSafetyStopInsideWorkingStop_ForBuy()
    {
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-SafetyInsideBuy");
        Guid orderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);

        // For a long the safety stop must sit BELOW the working stop. Here it rests ABOVE it (4995 > 4990),
        // nearer entry, so it would trigger first — the catastrophic floor would fire before the working stop
        // and the declared worst case would be neither deterministic nor the one declared. No domain code was
        // involved: the DB CHECK is the last line and must refuse it.
        StopPlanRecord inverted = PlanFor(operatorId, orderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_995m);
        await AssertInsertViolatesAsync(inverted, "CK_StopPlans_SafetyBeyondActual");
    }

    [Fact]
    public async Task Database_ShouldRejectSafetyStopInsideWorkingStop_ForSell()
    {
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-SafetyInsideSell");
        Guid orderId = await SeedParentOrderAsync(accountId, OrderSide.Sell);

        // The mirror of the Buy case — the one that catches an inverted side term. For a short the safety stop
        // must sit ABOVE the working stop (5010). Here it rests BELOW it (5005), inside the working stop.
        StopPlanRecord inverted = PlanFor(operatorId, orderId, OrderSide.Sell, entry: 5_000m, actual: 5_010m, safety: 5_005m);
        await AssertInsertViolatesAsync(inverted, "CK_StopPlans_SafetyBeyondActual");
    }

    [Fact]
    public async Task Database_ShouldAcceptSafetyBeyondWorkingStop_OnBothSides()
    {
        // The positive control for the two rejection tests above: without it, a CHECK that rejected EVERYTHING
        // would still pass tests 3 and 4. This proves the green is real on both sides.
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-BothSidesAccepted");
        Guid buyOrderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);
        Guid sellOrderId = await SeedParentOrderAsync(accountId, OrderSide.Sell);

        // Buy: safety (4980) < working (4990) < entry (5000). Sell: safety (5020) > working (5010) > entry (5000).
        await InsertStopPlanAsync(PlanFor(operatorId, buyOrderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_980m));
        await InsertStopPlanAsync(PlanFor(operatorId, sellOrderId, OrderSide.Sell, entry: 5_000m, actual: 5_010m, safety: 5_020m));

        await ExecuteDbContextAsync(async db =>
        {
            (await db.StopPlans.IgnoreQueryFilters().AnyAsync(p => p.OrderId == buyOrderId))
                .Should().BeTrue("a correctly-ordered long plan is accepted");
            (await db.StopPlans.IgnoreQueryFilters().AnyAsync(p => p.OrderId == sellOrderId))
                .Should().BeTrue("a correctly-ordered short plan is accepted");
        });
    }

    [Fact]
    public async Task Database_ShouldRejectNonPositiveProximityValue()
    {
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-NonPositiveProximity");

        // A zero band never promotes; a negative one is meaningless. Both are refused by the DB — each with its
        // own parent order so the unique OrderId index never confuses the assertion.
        foreach (decimal badValue in new[] { 0m, -8m })
        {
            Guid orderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);
            StopPlanRecord record = PlanFor(operatorId, orderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_980m, proximityValue: badValue);
            await AssertInsertViolatesAsync(record, "CK_StopPlans_ProximityValue_Positive");
        }
    }

    [Fact]
    public async Task Database_ShouldRejectUnknownProximityMetricAndStaging()
    {
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-UnknownEnums");

        // Unknown (0) is the fail-closed zero for both enums — an uninitialised metric or staging state must
        // never persist and read later as, say, "already native at the exchange".
        Guid metricOrderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);
        StopPlanRecord unknownMetric = PlanFor(operatorId, metricOrderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_980m, metric: StopProximityMetric.Unknown);
        await AssertInsertViolatesAsync(unknownMetric, "CK_StopPlans_ProximityMetric_NotUnknown");

        Guid stagingOrderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);
        StopPlanRecord unknownStaging = PlanFor(operatorId, stagingOrderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_980m, staging: StopStaging.Unknown);
        await AssertInsertViolatesAsync(unknownStaging, "CK_StopPlans_Staging_NotUnknown");
    }

    [Fact]
    public async Task DeletingOrder_ShouldCascadeDeleteItsStopPlan()
    {
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-CascadeDelete");
        Guid orderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);
        await InsertStopPlanAsync(PlanFor(operatorId, orderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_980m));

        // Delete the ORDER row in raw SQL — not through EF's change tracker — so the DB's own FK cascade, not
        // the model configuration, is what removes the plan. If the cascade lived only in EF, this would leave
        // an orphaned plan.
        await ExecuteDbContextAsync(async db =>
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Orders\" WHERE \"Id\" = {orderId}"));

        await ExecuteDbContextAsync(async db =>
            (await db.StopPlans.IgnoreQueryFilters().AnyAsync(p => p.OrderId == orderId))
                .Should().BeFalse("FK_StopPlans_Orders_OrderId ON DELETE CASCADE must remove the plan with its order"));
    }

    [Fact]
    public async Task AtrProximity_ShouldBeRefused_UntilBandResolutionMovesToTheCaller()
    {
        // NOT-YET-SUPPORTED PIN (contract §2) — not a guard, and not counted as coverage. #153 refuses ATR
        // proximity in the DOMAIN (StopPlan.Create throws NotSupportedException). The database deliberately
        // does NOT encode that refusal — an ATR metric (3) satisfies CK_StopPlans_ProximityMetric_NotUnknown —
        // so the domain rebuild is the only gate.
        //
        // The indicator pipeline (R-3) LANDED in #310, so ATR is now measurable — and the refusal still
        // stands, by design: #310 scoped the lift out because the band must resolve at the caller (the
        // promotion watcher), never inside this immutable value object reconstructed from the database.
        // #311 makes that change and deletes the throw. WHEN #311 LANDS, THIS TEST GOES RED: a deliberate
        // reminder that the persist-then-reconstruct ATR path is now live and needs real coverage.
        (Guid accountId, Guid operatorId) = await SeedTenantAsync("Topstep-AtrPin");
        Guid orderId = await SeedParentOrderAsync(accountId, OrderSide.Buy);

        StopPlanRecord atrPlan = PlanFor(
            operatorId, orderId, OrderSide.Buy, entry: 5_000m, actual: 4_990m, safety: 4_980m,
            metric: StopProximityMetric.AverageTrueRange, proximityValue: 2m);

        // The DB accepts the ATR row — persistence is not where ATR is refused.
        await InsertStopPlanAsync(atrPlan);

        (StopPlanRecord record, Order order) = await QueryDbContextAsync(async db =>
        {
            StopPlanRecord r = await db.StopPlans.IgnoreQueryFilters().SingleAsync(p => p.OrderId == orderId);
            Order o = await db.Orders.IgnoreQueryFilters().SingleAsync(x => x.Id == orderId);
            return (r, o);
        });

        // Reconstructing the domain plan for promotion (what StopPromotionService does) is where ATR is refused.
        Action reconstruct = () => record.ToStopPlan(order);
        reconstruct.Should().Throw<NotSupportedException>(
            "until #311 moves band resolution to the caller, the domain refuses to rebuild an ATR plan rather than mis-measure it as ticks");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Builds an unsaved plan for <paramref name="orderId"/> with valid defaults, so each test overrides
    /// only the field it is exercising.</summary>
    private static StopPlanRecord PlanFor(
        Guid userId,
        Guid orderId,
        OrderSide side,
        decimal entry,
        decimal actual,
        decimal safety,
        StopProximityMetric metric = StopProximityMetric.Ticks,
        decimal proximityValue = 8m,
        StopStaging staging = StopStaging.Hidden)
    {
        return new StopPlanRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderId = orderId,
            Side = side,
            EntryPrice = entry,
            ActualStopPrice = actual,
            SafetyStopPrice = safety,
            ProximityMetric = metric,
            ProximityValue = proximityValue,
            Staging = staging,
        };
    }

    /// <summary>Adds one plan through the real host pipeline, in its own scope, so a rejected insert cannot leave
    /// a following one in a poisoned change tracker.</summary>
    private async Task InsertStopPlanAsync(StopPlanRecord record)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        db.StopPlans.Add(record);
        await db.SaveChangesAsync();
    }

    /// <summary>Asserts an insert is rejected by the database with a CHECK violation (SqlState 23514) naming
    /// <paramref name="expectedConstraint"/> — never a bare "some exception", which would pass when an FK, NOT
    /// NULL, or unrelated constraint fails instead.</summary>
    private async Task AssertInsertViolatesAsync(StopPlanRecord record, string expectedConstraint)
    {
        Func<Task> insert = () => InsertStopPlanAsync(record);

        var thrown = await insert.Should().ThrowAsync<DbUpdateException>("the database, not the app layer, must reject the row");

        PostgresException? pg = thrown.Which.InnerException as PostgresException;
        pg.Should().NotBeNull("the rejection must come from PostgreSQL, not from EF validation");
        pg!.SqlState.Should().Be(CheckViolationSqlState, "a CHECK violation is 23514 — an FK/NOT NULL failure would mean the row failed for the wrong reason");
        pg.ConstraintName.Should().Be(expectedConstraint, "the row must fail on the constraint under test, not an incidental one");
    }

    /// <summary>Seeds a firm + connection (via the API) and a Practice account (direct), returning the account
    /// id and the operator's user id — the fixture the direct-write constraint tests build orders on.</summary>
    private async Task<(Guid AccountId, Guid OperatorId)> SeedTenantAsync(string label)
    {
        HttpClient client = await CreateAuthenticatedClientAsync();
        Guid connectionId = await CreateConnectionAsync(client, label);
        Guid operatorId = await GetOperatorIdAsync();

        Guid accountId = Guid.NewGuid();
        await ExecuteDbContextAsync(async db =>
        {
            db.Accounts.Add(new Account
            {
                Id = accountId,
                UserId = operatorId,
                ConnectionId = connectionId,
                VenueAccountKey = $"acct-{accountId:N}",
                Name = label,
                Stage = AccountStage.Evaluation,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
                Balance = 50_000m,
            });
            await db.SaveChangesAsync();
        });

        return (accountId, operatorId);
    }

    /// <summary>Seeds a valid parent <see cref="Order"/> (Practice mode, matching its account so the gh#96 mode
    /// trigger passes) and returns its id. The order's own stop values are journal fields — irrelevant to the
    /// StopPlan CHECKs — so they stay at benign defaults.</summary>
    private async Task<Guid> SeedParentOrderAsync(Guid accountId, OrderSide side)
    {
        Guid orderId = Guid.NewGuid();
        Guid operatorId = await GetOperatorIdAsync();

        await ExecuteDbContextAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = operatorId,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Symbol = "MES",
                Side = side,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        return orderId;
    }

    private async Task<Guid> CreateConnectionAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        createFirm.EnsureSuccessStatusCode();
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        createConn.EnsureSuccessStatusCode();
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        return connection.Id;
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        // Practice and Evaluation carry no capital at risk -> TradingMode.Practice, allowed in the Development host.
        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConv.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First().Id;
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest declareReq = new(
            DailyLossLimit: 1_000m,
            AccountProfitTarget: 3_000m,
            FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday,
            TrailingAmount: 2_000m,
            LocksAt: 50_100m,
            PerTradeRiskFraction: 0.15m,
            TargetRewardRatio: 1.5m,
            MaxDrawdownPerTrade: 300m,
            DailyDrawdownGovernor: 600m,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m,
            StartingBalance: 50_000m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private static SendOrderRequest ValidOrderRequest(int quantity = 1)
    {
        return new SendOrderRequest(
            Symbol: "MES",
            TickSize: 0.25m,
            PointValue: 5m,
            Side: OrderSide.Buy,
            Quantity: quantity,
            Entry: 5_000m,
            Stop: 4_990m,
            SafetyStop: 4_980m,
            ReferencePrice: 5_000m,
            Type: OrderType.Market);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private Task<Guid> GetOperatorIdAsync() => QueryDbContextAsync(async db =>
        (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> QueryDbContextAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}
