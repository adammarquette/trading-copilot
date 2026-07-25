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

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for per-account risk-profile declaration (<c>PUT /accounts/{id}/risk</c>, R-5, gh#10) and
/// the R-12 re-gate at <c>take</c> against real PostgreSQL (gh#143). Realigned to the real route map by gh#160:
/// declaration is <c>PUT</c> (not <c>POST</c>), and the re-gate test reuses the gh#157 staged-ladder arm path
/// rather than a second fixture.
/// </summary>
/// <remarks>
/// The dynamic trailing-drawdown <i>floor</i> itself is runtime state the risk runtime will compute (gh#10-deferred);
/// what is persisted today — and what this suite covers — is the declaration (its trailing inputs and starting
/// balance) plus the property that matters at execution time: a profile tightened <b>after</b> arming binds on the
/// next <c>take</c>, because <c>take</c> re-validates against fresh truth (R-12), never the arm-time decision.
/// Backed by the adversarial venue stub (the sole sanctioned double).
/// </remarks>
public class RiskProfileLifecycleIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public RiskProfileLifecycleIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DeclareRisk_ShouldPersistProfile_AndReplaceOnRedeclaration()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-RiskDeclare");

        // First declaration.
        using (HttpResponseMessage first = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk", RiskRequest(startingBalance: 50_000m, perTradeRiskFraction: 0.15m)))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await ExecuteDbContextAsync(async db =>
        {
            List<RiskProfileRecord> profiles = await db.RiskProfiles.IgnoreQueryFilters()
                .Where(p => p.AccountId == accountId).ToListAsync();
            profiles.Should().ContainSingle("a declaration persists exactly one profile for the account");
            profiles[0].StartingBalance.Should().Be(50_000m).And.BePositive(
                "the trailing-drawdown floor is computed from a positive starting balance");
            profiles[0].PerTradeRiskFraction.Should().Be(0.15m);
        });

        // Redeclaration with different numbers — the request is the WHOLE declaration, so it replaces every field.
        using (HttpResponseMessage second = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk", RiskRequest(startingBalance: 60_000m, perTradeRiskFraction: 0.10m)))
        {
            second.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await ExecuteDbContextAsync(async db =>
        {
            List<RiskProfileRecord> profiles = await db.RiskProfiles.IgnoreQueryFilters()
                .Where(p => p.AccountId == accountId).ToListAsync();
            profiles.Should().ContainSingle("one profile per account — redeclaration replaces, it never accumulates");
            profiles[0].StartingBalance.Should().Be(60_000m, "the new declaration's values win");
            profiles[0].PerTradeRiskFraction.Should().Be(0.10m);
        });
    }

    [Fact]
    public async Task TightenedRiskProfile_ShouldBindNextTake()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupTradeableAccountAsync(client, "Topstep-RiskRegate");

        // Loose profile: a 1-lot at ~$100/contract risk (20 pts × $5, sized off the safety stop) is affordable.
        using (HttpResponseMessage loose = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk", RiskRequest(maxDrawdownPerTrade: 300m)))
        {
            loose.EnsureSuccessStatusCode();
        }

        Guid orderId = await ArmAsync(client, accountId);

        // Tighten so even a single contract ($100) exceeds the per-trade cap ($50) — set AFTER arming.
        using (HttpResponseMessage tightened = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk", RiskRequest(maxDrawdownPerTrade: 50m)))
        {
            tightened.EnsureSuccessStatusCode();
        }

        // R-12: take re-validates against the CURRENT profile, not the arm-time decision, so it now refuses.
        using HttpResponseMessage take = await client.PostAsync($"/orders/{orderId}/take", content: null);
        take.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "the tightened profile must bind at take — the arm-time approval is history, not authorization (R-12)");

        SendOrderResponse? result = await take.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(result);
        result.Outcome.Should().Be("RefusedByRisk");
        result.ApprovedQuantity.Should().Be(0, "not even one contract fits under the tightened per-trade cap");

        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderId);
            order.Status.Should().Be(OrderStatus.Staged, "a refused take leaves the order staged, never Working");
            order.VenueOrderKey.Should().BeNull("nothing was transmitted to the venue");

            List<GateDecisionRecord> blocked = await db.GateDecisions.IgnoreQueryFilters()
                .Where(g => g.AccountId == accountId && g.Outcome == GateOutcome.Blocked)
                .ToListAsync();
            blocked.Should().NotBeEmpty("the take refusal is journaled as a blocked gate decision (R-5/R-16)");
            blocked.Should().Contain(g => g.OrderId == orderId, "the refusal links the staged order it re-gated");
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------------------

    private static DeclareRiskProfileRequest RiskRequest(
        decimal maxDrawdownPerTrade = 300m,
        decimal startingBalance = 50_000m,
        decimal perTradeRiskFraction = 0.15m) => new(
        DailyLossLimit: 1_000m,
        AccountProfitTarget: 3_000m,
        FloorSource: FloorSource.FirmImposed,
        TrailingMode: TrailingMode.Intraday,
        TrailingAmount: 2_000m,
        LocksAt: startingBalance + 100m,
        PerTradeRiskFraction: perTradeRiskFraction,
        TargetRewardRatio: 1.5m,
        MaxDrawdownPerTrade: maxDrawdownPerTrade,
        DailyDrawdownGovernor: 600m,
        DailyProfitTarget: 1_500m,
        StopForDayAtProfitTarget: true,
        SizingBasis: SizingBasis.SafetyStop,
        MaxContractsPerOrder: 5,
        MaxBestDayFraction: 0.4m,
        StartingBalance: startingBalance);

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId)
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

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the loose profile must arm the 1-lot cleanly");
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        staged.Status.Should().Be(nameof(OrderStatus.Staged));
        return staged.OrderId;
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

    /// <summary>Firm → conventions (Practice ⇒ no capital at risk) → connection → discovered tradeable account.</summary>
    private async Task<Guid> SetupTradeableAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

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

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.CanTrade).Id;
    }

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
