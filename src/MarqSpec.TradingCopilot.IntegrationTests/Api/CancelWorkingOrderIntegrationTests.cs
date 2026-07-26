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
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>cancelling a working order via the order API</b> (gh#282 for gh#250, R-11, ADR-0007),
/// against real PostgreSQL with the applied migrations so the DB-level guards (the R-20 default-deny filter, the
/// mode trigger, the CHECK constraints) are live.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim under test:</b> cancelling a working order takes down <i>exactly</i> that order and its protection —
/// the venue leg is cancelled, the stop plan is retired, the action is audited — and another operator can neither
/// see nor cancel it. Every assertion guards a named failure mode; a happy-path-only check is not a guard.
/// </para>
/// <para>
/// Written from R-11 / ADR-0007 and the issue's acceptance criteria, <b>not</b> from the implementation (QA
/// contract). The venue seam is the sole sanctioned double and is adversarial — it reports
/// <see cref="TradingMode.Live"/> for every account, records every cancel it is issued (so a skipped venue cancel
/// cannot pass), and can be told to reject a cancel as "already gone" so the benign-rejection path is exercised for
/// real rather than assumed.
/// </para>
/// </remarks>
public class CancelWorkingOrderIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public CancelWorkingOrderIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
        VenueFactory.ResetPositions();
    }

    // -------------------------------------------------------------------------------------------------------
    // The cancel takes down exactly the order and its protection.
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_ShouldCancelAtVenue_AndMarkCancelled_WhenOrderIsWorking()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Cancel-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);
        (Guid orderId, string venueOrderKey) = await PlaceWorkingOrderAsync(client, accountId);

        using HttpResponseMessage response = await CancelAsync(client, orderId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // The load-bearing assertion: the pull reaches the VENUE for this handle. An implementation that only marked
        // the row Cancelled would leave a live order resting at the broker -- exactly what the stub's recorder catches.
        VenueFactory.CancelOrderCalls.Should().Contain(
            call => call.VenueOrderKey == venueOrderKey, "the working order's leg is pulled from the venue");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_ShouldRetireTheStopPlan_WhenOrderIsCancelled()
    {
        // Protection comes down WITH the order: a cancelled never-filled entry has no position to protect, so its
        // hidden stop plan must be retired -- otherwise the promotion watcher could later place a native stop for an
        // entry that never filled (the gh#250 hazard).
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Retire-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);
        (Guid orderId, _) = await PlaceWorkingOrderAsync(client, accountId);
        (await PlanStagingAsync(orderId)).Should().Be(StopStaging.Hidden, "the placed entry rests with a hidden stop plan");

        await CancelAsync(client, orderId);

        (await PlanStagingAsync(orderId)).Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task Cancel_ShouldAudit_WhenOrderIsCancelled()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Audit-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);
        (Guid orderId, _) = await PlaceWorkingOrderAsync(client, accountId);
        Guid planId = await PlanIdAsync(orderId);

        await CancelAsync(client, orderId);

        await ExecuteDbContextAsync(async db =>
        {
            bool audited = await db.AuditRecords.IgnoreQueryFilters().AnyAsync(record =>
                record.Action == AuditAction.OrderCancelled && record.StopPlanId == planId);
            audited.Should().BeTrue("the cancellation leaves an immutable audit record for the retired plan");
        });
    }

    // -------------------------------------------------------------------------------------------------------
    // Refusals.
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_ShouldReturn409_WhenOrderIsNotWorking()
    {
        // A filled order is a live position, not a resting order to pull -- cancelling it would be a lie. Only a
        // working order can be cancelled here.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Filled-{Guid.NewGuid():N}");
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedOrderAsync(accountId, operatorId, OrderStatus.Filled, "PX-FILLED-1");

        using HttpResponseMessage response = await CancelAsync(client, orderId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        VenueFactory.CancelOrderCalls.Should().NotContain(
            call => call.VenueOrderKey == "PX-FILLED-1", "a filled order's leg is never pulled");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Filled, "the row is untouched by the refusal");
    }

    [Fact]
    public async Task Cancel_ShouldReturn404_ForAnotherOperator()
    {
        // R-20 default-deny at the data layer: an order the current operator does not own is invisible, so the
        // handler cannot even find it -- 404, never a cross-operator cancel. Seed a working order owned by ANOTHER
        // operator on a real account; the authenticated operator must not be able to reach it.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"Foreign-{Guid.NewGuid():N}");
        Guid anotherOperator = Guid.NewGuid();
        Guid foreignOrderId = await SeedOrderAsync(accountId, anotherOperator, OrderStatus.Working, "PX-FOREIGN-1");

        using HttpResponseMessage response = await CancelAsync(client, foreignOrderId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        VenueFactory.CancelOrderCalls.Should().NotContain(
            call => call.VenueOrderKey == "PX-FOREIGN-1", "a non-owner's cancel never reaches the venue");
        (await OrderStatusAsync(foreignOrderId)).Should().Be(OrderStatus.Working, "the other operator's order stays working");
    }

    // -------------------------------------------------------------------------------------------------------
    // A benign venue rejection leaves the record consistent, without a retry-storm.
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_ShouldNotCorruptRecord_WhenVenueRejectsCancelAsAlreadyGone()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, $"BenignReject-{Guid.NewGuid():N}");
        await DeclareRiskProfileAsync(client, accountId);
        (Guid orderId, string venueOrderKey) = await PlaceWorkingOrderAsync(client, accountId);

        // The venue reports the order already gone (it filled or was pulled) as the cancel is issued -- a benign
        // rejection the path must swallow without corrupting the record or hammering the venue (gh#250, the OCO-exit
        // precedent gh#184).
        VenueFactory.MakeCancelThrow(venueOrderKey);

        using HttpResponseMessage response = await CancelAsync(client, orderId);

        ((int)response.StatusCode).Should().BeLessThan(500, "a benign 'already gone' rejection is handled, not a server error");
        VenueFactory.CancelOrderCalls.Count(call => call.VenueOrderKey == venueOrderKey)
            .Should().Be(1, "one cancel attempt per order — never a retry-storm on a benign rejection");

        // The record is left CONSISTENT: the order status and its plan staging agree, whichever terminal choice the
        // handler made on the benign reject -- never a half-transition (a cancelled order with a still-hidden plan,
        // or vice versa).
        OrderStatus status = await OrderStatusAsync(orderId);
        StopStaging staging = await PlanStagingAsync(orderId);
        bool coherent = (status == OrderStatus.Cancelled && staging == StopStaging.Retired)
            || (status == OrderStatus.Working && staging == StopStaging.Hidden);
        coherent.Should().BeTrue($"the record must stay coherent after a benign reject (observed {status} / {staging})");
    }

    // -------------------------------------------------------------------------------------------------------
    // Helpers — mirroring the gh#182 send-as-is harness (one fixture, unique account per test).
    // -------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Firm → conventions → connection → discovered account. Capital-at-risk false ⇒ the account is Practice.</summary>
    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConv.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts.First(account => account.CanTrade).Id;
    }

    private Task DeclareRiskProfileAsync(HttpClient client, Guid accountId) =>
        client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 1_000m,
                AccountProfitTarget: 3_000m,
                StartingBalance: 50_000m,
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
                MaxBestDayFraction: 0.4m))
        .ContinueWith(t => t.Result.EnsureSuccessStatusCode(), TaskScheduler.Default);

    /// <summary>Places a small buy through the gate; returns the working order's id and its venue handle.</summary>
    private async Task<(Guid OrderId, string VenueOrderKey)> PlaceWorkingOrderAsync(HttpClient client, Guid accountId)
    {
        SendOrderRequest proposal = new(
            Symbol: "MES",
            TickSize: 0.25m,
            PointValue: 5m,
            Side: OrderSide.Buy,
            Quantity: 1,
            Entry: 5_000m,
            Stop: 4_990m,
            SafetyStop: 4_980m,
            ReferencePrice: 5_000m,
            Type: OrderType.Market);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/", proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's proposal must place cleanly");
        SendOrderResponse? sent = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(sent);
        sent.OrderId.Should().NotBeNull();
        sent.VenueOrderKey.Should().NotBeNull();
        return (sent.OrderId!.Value, sent.VenueOrderKey!);
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid orderId) =>
        client.DeleteAsync($"/orders/{orderId}");

    private Task<Guid> OperatorIdAsync() =>
        QueryDbContextAsync(async db => (await db.Users.SingleAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    /// <summary>Seeds an order row directly (a state the stub does not reach on its own — e.g. Filled, or foreign-owned).</summary>
    private async Task<Guid> SeedOrderAsync(Guid accountId, Guid ownerId, OrderStatus status, string venueOrderKey)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbContextAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = ownerId,
                AccountId = accountId,
                Instrument = "CON.F.US.MES.U26",
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = 1,
                Type = OrderType.Market,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = status,
                Mode = TradingMode.Practice,
                EntryMethod = OrderEntryMethod.Manual,
                VenueOrderKey = venueOrderKey,
                PlacedAt = new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero),
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private Task<OrderStatus> OrderStatusAsync(Guid orderId) =>
        QueryDbContextAsync(async db =>
            (await db.Orders.IgnoreQueryFilters().SingleAsync(order => order.Id == orderId)).Status);

    private Task<StopStaging> PlanStagingAsync(Guid orderId) =>
        QueryDbContextAsync(async db =>
            (await db.StopPlans.IgnoreQueryFilters().SingleAsync(plan => plan.OrderId == orderId)).Staging);

    private Task<Guid> PlanIdAsync(Guid orderId) =>
        QueryDbContextAsync(async db =>
            (await db.StopPlans.IgnoreQueryFilters().SingleAsync(plan => plan.OrderId == orderId)).Id);

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
