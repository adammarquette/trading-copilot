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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests verifying the gated order execution API (<c>POST /accounts/{id}/orders</c>)
/// against real PostgreSQL with EF Core migrations, DB-level CHECK constraints, and audit trail persistence
/// (PRD R-5, R-11, R-14, R-16, gh#11, gh#121, gh#130).
/// </summary>
public class OrderEndpointsIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public OrderEndpointsIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SendOrder_ShouldReturn422UnprocessableEntity_WhenNoRiskProfileIsDeclared()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid accountId = await SetupAccountAsync(client, "Topstep-OrderNoRisk");

        SendOrderRequest request = ValidOrderRequest();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        // Verify database audit tables remain clean (no order placed or decision recorded)
        await ExecuteDbContextAsync(async db =>
        {
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId)).Should().BeFalse();
            (await db.GateDecisions.IgnoreQueryFilters().AnyAsync(g => g.AccountId == accountId)).Should().BeFalse();
        });
    }

    [Fact]
    public async Task SendOrder_ShouldReturn409Conflict_WhenCredentialKeyMismatches()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Setup firm and connection with a mismatched credential key ("other-key" vs process "topstep-main")
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep-OrderMismatchKey", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "other-key"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        // Seed account directly
        Guid accountId = Guid.NewGuid();
        await ExecuteDbContextAsync(async db =>
        {
            User op = await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail);
            Account account = new()
            {
                Id = accountId,
                UserId = op.Id,
                ConnectionId = connection.Id,
                VenueAccountKey = "mismatch-acct-1",
                Name = "Topstep-Mismatch",
                Stage = AccountStage.Evaluation,
                Mode = TradingMode.Practice,
                CanTrade = true,
                IsVisible = true,
                Balance = 50_000m,
            };
            db.Accounts.Add(account);

            RiskProfileRecord profile = ValidRiskRecord(op.Id, accountId);
            db.RiskProfiles.Add(profile);
            await db.SaveChangesAsync();
        });

        SendOrderRequest request = ValidOrderRequest();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SendOrder_ShouldReturn422UnprocessableEntity_AndPersistGateDecision_WhenOversizedOrderRefusedByRisk()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid accountId = await SetupAccountAsync(client, "Topstep-OrderRefusedByRisk");
        await DeclareRiskProfileAsync(client, accountId);

        // Oversized risk per contract: 100 points stop distance = $500 risk per contract (exceeds MaxDrawdownPerTrade limit of $300)
        SendOrderRequest oversizedRequest = ValidOrderRequest(quantity: 1) with { Stop = 4_900m, SafetyStop = 4_900m };

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", oversizedRequest);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        SendOrderResponse? sendResponse = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(sendResponse);
        sendResponse.Outcome.Should().Be("RefusedByRisk");
        sendResponse.ApprovedQuantity.Should().Be(0);
        sendResponse.OrderId.Should().BeNull();
        sendResponse.Reason.Should().NotBeNullOrWhiteSpace();

        // Audit Trail Assertions: 0 Orders persisted, 1 GateDecisionRecord persisted with Blocked outcome
        await ExecuteDbContextAsync(async db =>
        {
            (await db.Orders.IgnoreQueryFilters().AnyAsync(o => o.AccountId == accountId)).Should().BeFalse("no order was transmitted or journaled");

            List<GateDecisionRecord> decisions = await db.GateDecisions.IgnoreQueryFilters().Where(g => g.AccountId == accountId).ToListAsync();
            decisions.Should().HaveCount(1);
            GateDecisionRecord decision = decisions.First();
            decision.Outcome.Should().Be(GateOutcome.Blocked);
            decision.ApprovedQuantity.Should().Be(0);
            decision.OrderId.Should().BeNull();
            decision.Reason.Should().Be(sendResponse.Reason);
        });
    }

    [Fact]
    public async Task SendOrder_ShouldReturn200Ok_AndPersistOrderAndGateDecision_WhenValidOrderIsPlaced()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid accountId = await SetupAccountAsync(client, "Topstep-OrderPlaced");
        await DeclareRiskProfileAsync(client, accountId);

        SendOrderRequest validRequest = ValidOrderRequest(quantity: 1);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", validRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SendOrderResponse? sendResponse = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(sendResponse);
        sendResponse.Outcome.Should().Be("Placed");
        sendResponse.ApprovedQuantity.Should().Be(1);
        sendResponse.OrderId.Should().NotBeNull();
        sendResponse.VenueOrderKey.Should().NotBeNullOrWhiteSpace();

        // Audit Trail Assertions: 1 Order persisted and 1 GateDecisionRecord linked to the Order
        await ExecuteDbContextAsync(async db =>
        {
            Order? dbOrder = await db.Orders.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == sendResponse.OrderId);
            ArgumentNullException.ThrowIfNull(dbOrder);
            dbOrder.AccountId.Should().Be(accountId);
            dbOrder.Instrument.Should().Be("MESM25");
            dbOrder.Side.Should().Be(OrderSide.Buy);
            dbOrder.Size.Should().Be(1);
            dbOrder.Status.Should().Be(OrderStatus.Working);
            dbOrder.VenueOrderKey.Should().Be(sendResponse.VenueOrderKey);

            GateDecisionRecord? decision = await db.GateDecisions.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.AccountId == accountId);
            ArgumentNullException.ThrowIfNull(decision);
            decision.OrderId.Should().Be(dbOrder.Id);
            decision.Outcome.Should().Be(GateOutcome.Allowed);
            decision.ApprovedQuantity.Should().Be(1);
        });
    }

    [Fact]
    public async Task SendOrder_ShouldReturn409Conflict_WhenEnvironmentRefusesMode()
    {
        // Create client hosted in Staging environment
        using WebApplicationFactory<Program> stagingHost = _factory.CreateHost(DeploymentEnvironment.Staging);
        HttpClient client = stagingHost.CreateClient();

        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Setup firm with Funded stage convention -> CapitalAtRisk = true
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep-StagingRefusal", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Funded, CapitalAtRisk: true)]));
        declareConv.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        // Discover account -> resolves to TradingMode.Live
        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        Guid accountId = accounts.First().Id;

        await DeclareRiskProfileAsync(client, accountId);

        // Attempting to send order on Live mode account in Staging host -> Refused by R-14 mode x environment guard
        SendOrderRequest request = ValidOrderRequest(quantity: 1);
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders", request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SendOrder_ShouldReturn404NotFound_WhenAccountDoesNotExist()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid nonexistentId = Guid.NewGuid();

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{nonexistentId}/orders", ValidOrderRequest());
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        // Declare stage conventions: Practice and Evaluation carry no capital at risk -> TradingMode.Practice
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

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
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

    private static RiskProfileRecord ValidRiskRecord(Guid userId, Guid accountId)
    {
        return new RiskProfileRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountId = accountId,
            DailyLossLimit = 1_000m,
            AccountProfitTarget = 3_000m,
            FloorSource = FloorSource.FirmImposed,
            TrailingMode = TrailingMode.Intraday,
            TrailingAmount = 2_000m,
            LocksAt = 50_100m,
            PerTradeRiskFraction = 0.15m,
            TargetRewardRatio = 1.5m,
            MaxDrawdownPerTrade = 300m,
            DailyDrawdownGovernor = 600m,
            DailyProfitTarget = 1_500m,
            StopForDayAtProfitTarget = true,
            SizingBasis = SizingBasis.SafetyStop,
            MaxContractsPerOrder = 5,
            MaxBestDayFraction = 0.4m,
            StartingBalance = 50_000m,
        };
    }

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
