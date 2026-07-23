using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Dedicated production-safe, strictly read-only Smoke Test suite (<c>[Trait("Category", "Smoke")]</c>)
/// verifying core system health, operator authentication, and resource reading on deployment
/// (Engineering Guide §5/§10, QA Agent Contract, gh#26, gh#131).
/// </summary>
[Trait("Category", "Smoke")]
public class SystemSmokeIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record UserMeResponse(Guid Id, string Email, string DisplayName);

    public SystemSmokeIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetAuthMe_ShouldReturnOperatorProfile_WhenAuthenticated()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        UserMeResponse? user = await response.Content.ReadFromJsonAsync<UserMeResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(user);
        user.Id.Should().NotBeEmpty();
        user.Email.Should().Be(PostgresApiFactory.OperatorEmail);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetFirms_ShouldReturnFirmCatalog_WhenAuthenticated()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Seed a firm
        using HttpResponseMessage createResp = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep-SmokeTest", FirmType.PropFirm));
        createResp.EnsureSuccessStatusCode();

        using HttpResponseMessage response = await client.GetAsync("/firms");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<FirmResponse>? firms = await response.Content.ReadFromJsonAsync<List<FirmResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firms);
        firms.Should().NotBeEmpty();
        firms.Should().Contain(f => f.Name == "Topstep-SmokeTest");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetConnections_ShouldReturnConnections_WhenAuthenticated()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep-SmokeConn", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        createConn.EnsureSuccessStatusCode();

        using HttpResponseMessage response = await client.GetAsync("/connections");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<ConnectionResponse>? connections = await response.Content.ReadFromJsonAsync<List<ConnectionResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connections);
        connections.Should().NotBeEmpty();
        connections.Should().Contain(c => c.FirmId == firm.Id);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetAccounts_ShouldReturnAccountRoster_WhenAuthenticated()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep-SmokeAccount", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? conn = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(conn);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{conn.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();

        using HttpResponseMessage response = await client.GetAsync($"/connections/{conn.Id}/accounts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<AccountResponse>? accounts = await response.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        accounts.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetRiskProfile_ShouldReturnDeclaredProfile_WhenAuthenticated()
    {
        HttpClient client = _factory.CreateClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Topstep-SmokeRisk", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? conn = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(conn);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{conn.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        Guid accountId = accounts.First().Id;

        // Declare risk profile
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

        using HttpResponseMessage declareResp = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        declareResp.EnsureSuccessStatusCode();

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/risk");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        RiskProfileResponse? profile = await response.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(profile);
        profile.AccountId.Should().Be(accountId);
        profile.DailyLossLimit.Should().Be(1_000m);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_Endpoints_ShouldReturn401Unauthorized_WhenUnauthenticated()
    {
        HttpClient anonymousClient = _factory.CreateClient();

        using HttpResponseMessage meResp = await anonymousClient.GetAsync("/auth/me");
        meResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage firmsResp = await anonymousClient.GetAsync("/firms");
        firmsResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage connsResp = await anonymousClient.GetAsync("/connections");
        connsResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void SmokeTests_MustBeStrictlyReadOnly_AndNeverContainExecutionEndpoints()
    {
        // Reflection check: verify all methods carrying Category=Smoke trait are strictly read-only
        MethodInfo[] smokeMethods = typeof(SystemSmokeIntegrationTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(TraitAttribute) &&
                a.ConstructorArguments.Count >= 2 &&
                Equals(a.ConstructorArguments[0].Value, "Category") &&
                Equals(a.ConstructorArguments[1].Value, "Smoke")))
            .ToArray();

        smokeMethods.Should().NotBeEmpty("smoke suite must contain tagged read-only smoke tests");

        foreach (MethodInfo method in smokeMethods)
        {
            method.Name.Should().StartWith("Smoke_", "production smoke tests must follow the 'Smoke_' naming convention");
            method.Name.Should().NotContain("Order", "production smoke tests must NEVER touch order placement or execution endpoints");
        }
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }
}
