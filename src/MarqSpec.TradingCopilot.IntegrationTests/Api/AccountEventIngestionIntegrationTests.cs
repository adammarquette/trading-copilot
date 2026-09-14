using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>account-event ingestion</b> (gh#281, gh#219, R-17, R-11, ADR-0001): the venue's
/// realtime truth becoming journal state via <see cref="AccountEventIngestionService.ProcessAsync"/>. A fill writes
/// a <see cref="Fill"/> row and advances its order to <c>PartiallyFilled</c> / <c>Filled</c> — exactly as far as
/// the venue says, never double-counted on replay; an order-state event moves a working order to
/// <c>Cancelled</c> / <c>Rejected</c> (retiring a never-filled entry's protection). Driven directly at a chosen
/// <see cref="AccountEvent"/> — the stub cannot push events — with the always-on hosts suppressed by
/// <see cref="OcoExitTestPostgresFactory"/>.
/// </summary>
/// <remarks>
/// The service is background plumbing: it resolves the owning account across owners (<c>IgnoreQueryFilters</c> +
/// the process credential key, ADR-0015) then writes in a per-owner context, so every <see cref="Fill"/> carries
/// its owner and a foreign / unknown event never crosses the R-20 boundary. Traces R-17 · R-11 · ADR-0001.
/// </remarks>
public class AccountEventIngestionIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const string VenueKey = "PRAC-50K-101";

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public AccountEventIngestionIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Fills → journal.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fill_ShouldRecordFill_AndMarkPartiallyFilled_WhenFillIsPartial()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);

        bool changed = await ProcessAsync(Fill("ENTRY-1", "FILL-1", quantity: 1));

        changed.Should().BeTrue();
        (await FillCountAsync(orderId)).Should().Be(1, "the fill is recorded as a Fill row");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.PartiallyFilled, "a fill of 1 of 3 leaves the order partially filled");
    }

    [Fact]
    public async Task Fill_ShouldMarkFilled_WhenCumulativeFillsReachOrderSize()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 2);

        await ProcessAsync(Fill("ENTRY-1", "FILL-1", quantity: 1));
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.PartiallyFilled, "the first of two contracts");

        await ProcessAsync(Fill("ENTRY-1", "FILL-2", quantity: 1));

        (await FillCountAsync(orderId)).Should().Be(2);
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Filled, "cumulative fills reaching the order size complete it");
    }

    [Fact]
    public async Task Fill_ShouldIgnoreVoidedFill_WhenExchangeBusts()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);

        bool changed = await ProcessAsync(Fill("ENTRY-1", "FILL-1", quantity: 1, voided: true));

        changed.Should().BeFalse("a busted trade is not an execution");
        (await FillCountAsync(orderId)).Should().Be(0, "a voided fill writes no Fill row");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Working, "and does not advance the order");
    }

    [Fact]
    public async Task Fill_ShouldBeIdempotent_WhenTheSameVenueFillKeyReplays()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);

        (await ProcessAsync(Fill("ENTRY-1", "FILL-1", quantity: 1))).Should().BeTrue("the first fill is recorded");
        (await ProcessAsync(Fill("ENTRY-1", "FILL-1", quantity: 1))).Should().BeFalse("a replay of the same venue fill key is a no-op");

        (await FillCountAsync(orderId)).Should().Be(1, "the { OrderId, VenueFillKey } unique index dedupes the replay");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.PartiallyFilled, "the replay never double-counts or re-advances");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Order-state → journal.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrderState_ShouldMarkCancelled_AndRetireProtection_WhenVenueReportsATerminalCancel()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);
        Guid planId = await SeedStopPlanAsync(fixture.OperatorId, orderId, StopStaging.Hidden);

        bool changed = await ProcessAsync(OrderState("ENTRY-1", VenueOrderState.Cancelled));

        changed.Should().BeTrue();
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Cancelled, "a venue cancel of a working order marks it Cancelled");
        (await StagingAsync(planId)).Should().Be(StopStaging.Retired, "a never-filled entry that leaves the book has no position — its plan is retired");
        (await AuditActionForPlanAsync(planId)).Should().Be(AuditAction.OrderCancelled, "the retirement is audited");
    }

    [Fact]
    public async Task OrderState_ShouldMarkRejected_WhenVenueRejectsTheOrder()
    {
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);

        bool changed = await ProcessAsync(OrderState("ENTRY-1", VenueOrderState.Rejected));

        changed.Should().BeTrue();
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Rejected, "a venue rejection of a still-working order marks it Rejected");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Ownership & robustness.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingestion_ShouldStampOwner_WhenRunningAsBackgroundPlumbing()
    {
        // R-20: the service has no request user — it resolves the owner and writes in a per-owner context, so the
        // Fill row carries the owning operator, not Guid.Empty.
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);

        await ProcessAsync(Fill("ENTRY-1", "FILL-1", quantity: 1));

        Guid fillOwner = await QueryDbAsync(db =>
            db.Fills.IgnoreQueryFilters().Where(f => f.OrderId == orderId).Select(f => f.UserId).SingleAsync());
        fillOwner.Should().Be(fixture.OperatorId, "the journaled fill carries its owner through the R-20-scoped write");
    }

    [Fact]
    public async Task Ingestion_ShouldIgnoreStrayEvent_WhenNoLocalOrderMatches()
    {
        // A fill for a real account but an unknown order key — or for a foreign account entirely — is ignored: no
        // phantom fill, the real order untouched, never a crash. (The foreign-account half holds by construction via
        // ResolveOwnerAsync's key + credential filter; the unknown-order half is the FindOrderAsync guard.)
        Fixture fixture = await FreshAccountAsync();
        Guid orderId = await SeedWorkingOrderAsync(fixture, "ENTRY-1", size: 3);

        (await ProcessAsync(Fill("GHOST-ORDER", "FILL-9", quantity: 1)))
            .Should().BeFalse("a fill for an unknown order key on a real account is ignored");

        FillEvent foreign = new(
            VenueAccountId.Create(VenueId.Parse("projectx"), "NOT-A-DISCOVERED-ACCOUNT"),
            DateTimeOffset.UtcNow, "ENTRY-1", "FILL-8", OrderSide.Buy, 1, new Price(5_000m), 0m, Voided: false);
        (await ProcessAsync(foreign)).Should().BeFalse("a fill for a foreign account is ignored");

        (await FillCountAsync(orderId)).Should().Be(0, "no phantom fill is written");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Working, "the real order is untouched");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Fixture(Guid AccountId, Guid OperatorId);

    private async Task<Fixture> FreshAccountAsync()
    {
        await ExecuteDbAsync(async db =>
        {
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Fills.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync();
        Guid operatorId = await OperatorIdAsync();
        Guid accountId = await SetupAccountAsync(client);
        return new Fixture(accountId, operatorId);
    }

    private FillEvent Fill(string venueOrderKey, string venueFillKey, int quantity, bool voided = false) => new(
        VenueAccountId.Create(VenueId.Parse("projectx"), VenueKey),
        DateTimeOffset.UtcNow, venueOrderKey, venueFillKey, OrderSide.Buy, quantity, new Price(5_000m), Fees: 1.24m, Voided: voided);

    private OrderStateEvent OrderState(string venueOrderKey, VenueOrderState state) => new(
        VenueAccountId.Create(VenueId.Parse("projectx"), VenueKey),
        DateTimeOffset.UtcNow, venueOrderKey, state, FilledQuantity: 0, AverageFillPrice: null);

    private async Task<bool> ProcessAsync(AccountEvent accountEvent)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AccountEventIngestionService ingestion = scope.ServiceProvider.GetRequiredService<AccountEventIngestionService>();
        return await ingestion.ProcessAsync(accountEvent, CancellationToken.None);
    }

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

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Ingest-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        return accounts.First(a => a.VenueAccountKey == VenueKey).Id;
    }

    private async Task<Guid> SeedWorkingOrderAsync(Fixture fixture, string venueOrderKey, int size)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = fixture.OperatorId,
                AccountId = fixture.AccountId,
                Instrument = Contract,
                Symbol = "ES",
                Side = OrderSide.Buy,
                Size = size,
                Type = OrderType.Market,
                EntryPrice = 5_000m,
                WorkingStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ReferencePrice = 5_000m,
                TickSize = 0.25m,
                PointValue = 5m,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                VenueOrderKey = venueOrderKey,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return orderId;
    }

    private async Task<Guid> SeedStopPlanAsync(Guid userId, Guid orderId, StopStaging staging)
    {
        Guid planId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = planId,
                UserId = userId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = 5_000m,
                ActualStopPrice = 4_990m,
                SafetyStopPrice = 4_980m,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8m,
                Staging = staging,
            });
            await db.SaveChangesAsync();
        });
        return planId;
    }

    private Task<Guid> OperatorIdAsync() =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<int> FillCountAsync(Guid orderId) => QueryDbAsync(db =>
        db.Fills.IgnoreQueryFilters().CountAsync(f => f.OrderId == orderId));

    private Task<OrderStatus> OrderStatusAsync(Guid orderId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync());

    private Task<StopStaging> StagingAsync(Guid planId) => QueryDbAsync(db =>
        db.StopPlans.IgnoreQueryFilters().Where(p => p.Id == planId).Select(p => p.Staging).SingleAsync());

    private Task<AuditAction> AuditActionForPlanAsync(Guid planId) => QueryDbAsync(db =>
        db.AuditRecords.IgnoreQueryFilters().Where(a => a.StopPlanId == planId).Select(a => a.Action).SingleAsync());

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
