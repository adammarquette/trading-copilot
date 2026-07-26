using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Audit;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for <b>cancel-working-order</b> (gh#282, gh#250, R-11, ADR-0007): pulling a resting order
/// off the book via <c>DELETE /orders/{id}</c>. A working order is cancelled at the venue, its stop plan retired,
/// and the action audited — and R-20 holds (another operator can neither see nor cancel it). Driven over HTTP with
/// the adversarial venue stub recording the cancel; the always-on hosts are stripped by
/// <see cref="OcoExitTestPostgresFactory"/>.
/// </summary>
/// <remarks>
/// A cancel is risk-reducing — it bypasses the kill switch and the send ladder — so working orders are seeded
/// directly (with a known venue handle) rather than armed/taken; the endpoint only needs the order row + its
/// account/connection to resolve the venue. On a venue rejection the order status is deliberately NOT forced
/// terminal (the account-event stream reconciles it). Traces R-11 · ADR-0007.
/// </remarks>
public class CancelWorkingOrderIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private const string Contract = "ESM25";
    private const string VenueKey = "PRAC-50K-101";

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public CancelWorkingOrderIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Cancel_ShouldCancelAtVenue_RetirePlan_AndAudit_WhenOrderIsWorking()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, "WORK-1");
        Guid planId = await SeedStopPlanAsync(operatorId, orderId, StopStaging.Hidden);

        using HttpResponseMessage response = await client.DeleteAsync($"/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Cancelled, "cancelling a working order marks it Cancelled");
        VenueFactory.CancelOrderCalls.Should().Contain(call => call.VenueOrderKey == "WORK-1", "the venue leg is cancelled");
        (await StagingAsync(planId)).Should().Be(StopStaging.Retired, "the order's stop plan is retired — it protects nothing now");
        (await AuditActionForPlanAsync(planId)).Should().Be(AuditAction.OrderCancelled, "the cancellation is audited");
    }

    [Fact]
    public async Task Cancel_ShouldReturn409_WhenOrderIsNotWorking()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, "FILLED-1", status: OrderStatus.Filled);

        using HttpResponseMessage response = await client.DeleteAsync($"/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a filled order is already done — cancelling it is refused, not a silent no-op");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Filled, "the order is unchanged");
        VenueFactory.CancelOrderCalls.Should().BeEmpty("no venue cancel is issued for a terminal order");
    }

    [Fact]
    public async Task Cancel_ShouldReturn404_ForAnotherOperator()
    {
        HttpClient owner = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(owner);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, "WORK-1");

        HttpClient intruder = await SecondOperatorClientAsync();
        using HttpResponseMessage response = await intruder.DeleteAsync($"/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "R-20: another operator cannot see or cancel the owner's order");
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Working, "the owner's order is untouched");
        VenueFactory.CancelOrderCalls.Should().BeEmpty("nothing is cancelled at the venue");
    }

    [Fact]
    public async Task Cancel_ShouldReturn409_AndNotForceTerminal_WhenVenueRejectsCancel()
    {
        HttpClient client = await FreshOperatorAsync();
        Guid accountId = await SetupAccountAsync(client);
        Guid operatorId = await OperatorIdAsync();
        Guid orderId = await SeedWorkingOrderAsync(accountId, operatorId, "WORK-1");
        Guid planId = await SeedStopPlanAsync(operatorId, orderId, StopStaging.Hidden);
        VenueFactory.MakeCancelThrow("WORK-1"); // the venue refuses — the order may have filled, not cancelled

        using HttpResponseMessage response = await client.DeleteAsync($"/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await OrderStatusAsync(orderId)).Should().Be(OrderStatus.Working, "a rejected cancel never forces a terminal status — venue truth reconciles it");
        (await StagingAsync(planId)).Should().Be(StopStaging.Hidden, "the plan is not retired when the cancel did not take");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private async Task<HttpClient> FreshOperatorAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
            await db.AuditRecords.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });
        return await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string email, string password)
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<HttpClient> SecondOperatorClientAsync()
    {
        string email = $"intruder-{Guid.NewGuid():N}@example.com";
        const string password = "Password123!";
        HttpClient owner = await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);

        using HttpResponseMessage invite = await owner.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        invite.EnsureSuccessStatusCode();
        IssueInvitationResponse? issued = await invite.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(issued);

        using HttpResponseMessage accept = await _factory.CreateClient().PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(issued.Token, password, "Intruder"));
        accept.EnsureSuccessStatusCode();
        return await AuthenticatedClientAsync(email, password);
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-Cancel-{Guid.NewGuid():N}", FirmType.PropFirm));
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

    private async Task<Guid> SeedWorkingOrderAsync(Guid accountId, Guid userId, string venueOrderKey, OrderStatus status = OrderStatus.Working)
    {
        Guid orderId = Guid.NewGuid();
        await ExecuteDbAsync(async db =>
        {
            db.Orders.Add(new Order
            {
                Id = orderId,
                UserId = userId,
                AccountId = accountId,
                Instrument = Contract,
                Symbol = "ES",
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
