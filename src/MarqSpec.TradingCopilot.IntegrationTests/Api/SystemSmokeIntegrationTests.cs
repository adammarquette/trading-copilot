using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Dedicated production-safe, strictly read-only Smoke Test suite (<c>[Trait("Category", "Smoke")]</c>)
/// verifying core system health, operator authentication, and resource reading against local test hosts
/// or deployed environments (<c>SMOKE_TARGET_BASE_URL</c>) (Engineering Guide §5/§10, QA Agent Contract, gh#26, gh#131).
/// </summary>
[Trait("Category", "Smoke")]
public class SystemSmokeIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record UserMeResponse(Guid Id, string Email, string DisplayName);

    /// <summary>
    /// Transport-level safety guard ensuring smoke test clients are strictly read-only.
    /// Only <c>GET</c> requests and <c>POST /auth/login</c> (for token acquisition) are permitted.
    /// Any mutating method (<c>POST</c>, <c>PUT</c>, <c>DELETE</c>) throws <see cref="InvalidOperationException"/>.
    /// </summary>
    private sealed class ReadOnlyHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool isAllowed = request.Method == HttpMethod.Get
                || (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/auth/login");

            if (!isAllowed)
            {
                throw new InvalidOperationException(
                    $"Smoke tests are strictly read-only; {request.Method} {request.RequestUri?.AbsolutePath} is forbidden.");
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    public SystemSmokeIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetAuthMe_ShouldReturnOperatorProfile_WhenAuthenticated()
    {
        HttpClient client = CreateSmokeClient();
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
        HttpClient client = CreateSmokeClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/firms");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<FirmResponse>? firms = await response.Content.ReadFromJsonAsync<List<FirmResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firms);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetConnections_ShouldReturnConnections_WhenAuthenticated()
    {
        HttpClient client = CreateSmokeClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync("/connections");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<ConnectionResponse>? connections = await response.Content.ReadFromJsonAsync<List<ConnectionResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connections);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetAccounts_ShouldReturnAccountRoster_WhenAuthenticated()
    {
        HttpClient client = CreateSmokeClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage connsResponse = await client.GetAsync("/connections");
        connsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        List<ConnectionResponse>? connections = await connsResponse.Content.ReadFromJsonAsync<List<ConnectionResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connections);

        if (connections.Count > 0)
        {
            Guid connectionId = connections[0].Id;
            using HttpResponseMessage accountsResponse = await client.GetAsync($"/connections/{connectionId}/accounts");
            accountsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            List<AccountResponse>? accounts = await accountsResponse.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
            ArgumentNullException.ThrowIfNull(accounts);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GetRiskProfile_ShouldReturnDeclaredProfile_WhenAuthenticated()
    {
        HttpClient client = CreateSmokeClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage connsResponse = await client.GetAsync("/connections");
        connsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        List<ConnectionResponse>? connections = await connsResponse.Content.ReadFromJsonAsync<List<ConnectionResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connections);

        if (connections.Count > 0)
        {
            Guid connectionId = connections[0].Id;
            using HttpResponseMessage accountsResponse = await client.GetAsync($"/connections/{connectionId}/accounts");
            accountsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            List<AccountResponse>? accounts = await accountsResponse.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
            ArgumentNullException.ThrowIfNull(accounts);

            if (accounts.Count > 0)
            {
                Guid accountId = accounts[0].Id;
                using HttpResponseMessage riskResponse = await client.GetAsync($"/accounts/{accountId}/risk");
                riskResponse.StatusCode.Should().Match(s => s == HttpStatusCode.OK || s == HttpStatusCode.NotFound);
            }
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_Endpoints_ShouldReturn401Unauthorized_WhenUnauthenticated()
    {
        HttpClient anonymousClient = CreateSmokeClient();

        using HttpResponseMessage meResp = await anonymousClient.GetAsync("/auth/me");
        meResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage firmsResp = await anonymousClient.GetAsync("/firms");
        firmsResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage connsResp = await anonymousClient.GetAsync("/connections");
        connsResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SmokeTests_MustBeStrictlyReadOnly_AndEnforcedByTransport()
    {
        HttpClient client = CreateSmokeClient();
        string token = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Attempting a mutating request (e.g. POST /firms) through the smoke client MUST be rejected at transport level
        Func<Task> actPost = async () => await client.PostAsJsonAsync("/firms", new CreateFirmRequest("Forbidden", FirmType.PropFirm));
        await actPost.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Smoke tests are strictly read-only; POST /firms is forbidden.");

        // Attempting a PUT request through the smoke client MUST also be rejected at transport level
        Func<Task> actPut = async () => await client.PutAsJsonAsync($"/accounts/{Guid.NewGuid()}/risk", new { });
        await actPut.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Smoke tests are strictly read-only; PUT * is forbidden.");
    }

    private HttpClient CreateSmokeClient()
    {
        string? targetBaseUrl = Environment.GetEnvironmentVariable("SMOKE_TARGET_BASE_URL");
        HttpMessageHandler primaryHandler = string.IsNullOrWhiteSpace(targetBaseUrl)
            ? _factory.Server.CreateHandler()
            : new HttpClientHandler();

        ReadOnlyHandler readOnlyHandler = new() { InnerHandler = primaryHandler };
        HttpClient client = new(readOnlyHandler);

        if (!string.IsNullOrWhiteSpace(targetBaseUrl))
        {
            client.BaseAddress = new Uri(targetBaseUrl);
        }
        else
        {
            client.BaseAddress = _factory.ClientOptions.BaseAddress;
        }

        return client;
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }
}
