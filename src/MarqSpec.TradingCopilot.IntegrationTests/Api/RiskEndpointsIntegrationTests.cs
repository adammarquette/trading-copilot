using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests verifying the risk declaration and retrieval API (<c>/accounts/{id}/risk</c>)
/// against real PostgreSQL with EF Core migrations and DB-level CHECK constraints (PRD R-5, gh#10, gh#121).
/// </summary>
public class RiskEndpointsIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public RiskEndpointsIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RiskProfileLifecycle_ShouldReturn404Initially_DeclareProfile_Roundtrip_AndAllowCompleteReplacement()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Setup firm, connection, and discover account
        Guid accountId = await SetupAccountAsync(client, "Topstep-RiskLifecycle");

        // 2. GET /accounts/{id}/risk -> MUST return 404 Not Found initially (fail-closed, no default profile invented)
        using HttpResponseMessage initialGet = await client.GetAsync($"/accounts/{accountId}/risk");
        initialGet.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 3. PUT /accounts/{id}/risk -> Declare initial risk profile
        DeclareRiskProfileRequest declareReq = new(
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
            MaxContractsPerOrder: 3,
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage putResponse = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        RiskProfileResponse? declared = await putResponse.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(declared);
        declared.AccountId.Should().Be(accountId);
        declared.DailyLossLimit.Should().Be(1_000m);
        declared.FloorSource.Should().Be(FloorSource.FirmImposed);
        declared.TrailingMode.Should().Be(TrailingMode.Intraday);
        declared.PerTradeRiskFraction.Should().Be(0.15m);
        declared.DailyDrawdownGovernor.Should().Be(600m);
        declared.SizingBasis.Should().Be(SizingBasis.SafetyStop);

        // 4. GET /accounts/{id}/risk -> Verify round-trip persistence
        using HttpResponseMessage getResponse = await client.GetAsync($"/accounts/{accountId}/risk");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        RiskProfileResponse? fetched = await getResponse.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(fetched);
        fetched.Should().BeEquivalentTo(declared);

        // 5. PUT /accounts/{id}/risk -> Complete replacement (varies EVERY field to ensure no partial merging occurs)
        DeclareRiskProfileRequest replacementReq = new(
            DailyLossLimit: 1_500m,
            AccountProfitTarget: null,
            StartingBalance: 50_000m,
            FloorSource: FloorSource.SelfImposed,
            TrailingMode: TrailingMode.EndOfDay,
            TrailingAmount: 2_500m,
            LocksAt: null,
            PerTradeRiskFraction: 0.25m,
            TargetRewardRatio: 2.0m,
            MaxDrawdownPerTrade: 500m,
            DailyDrawdownGovernor: 800m,
            DailyProfitTarget: null,
            StopForDayAtProfitTarget: false,
            SizingBasis: SizingBasis.ActualStop,
            MaxContractsPerOrder: 10,
            MaxBestDayFraction: null);

        using HttpResponseMessage replaceResponse = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", replacementReq);
        replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        RiskProfileResponse? replaced = await replaceResponse.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(replaced);
        replaced.DailyLossLimit.Should().Be(1_500m);
        replaced.AccountProfitTarget.Should().BeNull();
        replaced.FloorSource.Should().Be(FloorSource.SelfImposed);
        replaced.TrailingMode.Should().Be(TrailingMode.EndOfDay);
        replaced.TrailingAmount.Should().Be(2_500m);
        replaced.LocksAt.Should().BeNull();
        replaced.PerTradeRiskFraction.Should().Be(0.25m);
        replaced.TargetRewardRatio.Should().Be(2.0m);
        replaced.MaxDrawdownPerTrade.Should().Be(500m);
        replaced.DailyDrawdownGovernor.Should().Be(800m);
        replaced.DailyProfitTarget.Should().BeNull();
        replaced.StopForDayAtProfitTarget.Should().BeFalse();
        replaced.SizingBasis.Should().Be(SizingBasis.ActualStop);
        replaced.MaxContractsPerOrder.Should().Be(10);
        replaced.MaxBestDayFraction.Should().BeNull();

        // 6. GET /accounts/{id}/risk -> Verify full replacement persisted in DB
        using HttpResponseMessage finalGet = await client.GetAsync($"/accounts/{accountId}/risk");
        finalGet.StatusCode.Should().Be(HttpStatusCode.OK);
        RiskProfileResponse? finalFetched = await finalGet.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(finalFetched);
        finalFetched.Should().BeEquivalentTo(replaced);
    }

    [Theory]
    [InlineData(1.5)]    // Fraction > 1
    [InlineData(0.0)]    // Fraction <= 0
    [InlineData(-0.1)]   // Negative fraction
    public async Task DeclareRisk_ShouldRefuseInvalidPerTradeRiskFraction_WithBadRequest(decimal invalidFraction)
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid accountId = await SetupAccountAsync(client, $"Topstep-RiskInvalidFrac-{invalidFraction}");

        DeclareRiskProfileRequest badRequest = ValidRequest(perTradeRiskFraction: invalidFraction);
        using HttpResponseMessage putResponse = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", badRequest);

        putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Verify database remains clean (GET returns 404)
        using HttpResponseMessage getResponse = await client.GetAsync($"/accounts/{accountId}/risk");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(-100)]   // Negative drawdown governor
    [InlineData(0)]      // Zero drawdown governor
    public async Task DeclareRisk_ShouldRefuseInvalidDrawdownGovernor_WithBadRequest(decimal invalidGovernor)
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid accountId = await SetupAccountAsync(client, $"Topstep-RiskInvalidGov-{invalidGovernor}");

        DeclareRiskProfileRequest badRequest = ValidRequest(dailyDrawdownGovernor: invalidGovernor);
        using HttpResponseMessage putResponse = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", badRequest);

        putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Verify database remains clean (GET returns 404)
        using HttpResponseMessage getResponse = await client.GetAsync($"/accounts/{accountId}/risk");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeclareRisk_ShouldRefuseInvalidBestDayFraction_WithBadRequest()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid accountId = await SetupAccountAsync(client, "Topstep-RiskInvalidBestDay");

        DeclareRiskProfileRequest badRequest = ValidRequest(maxBestDayFraction: 1.5m);
        using HttpResponseMessage putResponse = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", badRequest);

        putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Verify database remains clean (GET returns 404)
        using HttpResponseMessage getResponse = await client.GetAsync($"/accounts/{accountId}/risk");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RiskEndpoints_ShouldReturn404_WhenAccountDoesNotExist()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Guid nonexistentId = Guid.NewGuid();

        using HttpResponseMessage getResponse = await client.GetAsync($"/accounts/{nonexistentId}/risk");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage putResponse = await client.PutAsJsonAsync($"/accounts/{nonexistentId}/risk", ValidRequest());
        putResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First().Id;
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    private static DeclareRiskProfileRequest ValidRequest(
        decimal? dailyLossLimit = 1_000m,
        decimal perTradeRiskFraction = 0.15m,
        decimal dailyDrawdownGovernor = 600m,
        decimal? maxBestDayFraction = 0.4m)
    {
        return new DeclareRiskProfileRequest(
            DailyLossLimit: dailyLossLimit,
            AccountProfitTarget: 3_000m,
            StartingBalance: 50_000m,
            FloorSource: FloorSource.FirmImposed,
            TrailingMode: TrailingMode.Intraday,
            TrailingAmount: 2_000m,
            LocksAt: 50_100m,
            PerTradeRiskFraction: perTradeRiskFraction,
            TargetRewardRatio: 1.5m,
            MaxDrawdownPerTrade: 300m,
            DailyDrawdownGovernor: dailyDrawdownGovernor,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 3,
            MaxBestDayFraction: maxBestDayFraction);
    }
}
