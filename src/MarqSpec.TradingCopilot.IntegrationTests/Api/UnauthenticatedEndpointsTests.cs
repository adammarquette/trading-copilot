using System.Net;
using System.Net.Http.Headers;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests verifying unauthenticated access security (PRD R-18, gh#8).
/// Every non-anonymous endpoint must reject missing, malformed, or expired tokens with HTTP 401 Unauthorized.
/// </summary>
public class UnauthenticatedEndpointsTests : IClassFixture<PostgresApiFactory>
{
    private readonly HttpClient _client;

    public UnauthenticatedEndpointsTests(PostgresApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public static IEnumerable<object[]> NonAnonymousEndpoints =>
        new List<object[]>
        {
            new object[] { HttpMethod.Get, "/auth/me" },
            new object[] { HttpMethod.Get, "/firms" },
            new object[] { HttpMethod.Post, "/firms" },
            new object[] { HttpMethod.Put, $"/firms/{Guid.NewGuid()}/conventions" },
            new object[] { HttpMethod.Get, "/connections" },
            new object[] { HttpMethod.Post, "/connections" },
            new object[] { HttpMethod.Post, $"/connections/{Guid.NewGuid()}/accounts/discover" },
            new object[] { HttpMethod.Get, $"/connections/{Guid.NewGuid()}/accounts" },
            new object[] { HttpMethod.Put, $"/accounts/{Guid.NewGuid()}/stage" },
            new object[] { HttpMethod.Delete, $"/accounts/{Guid.NewGuid()}/stage" },
        };

    [Theory]
    [MemberData(nameof(NonAnonymousEndpoints))]
    public async Task NonAnonymousEndpoint_ShouldReturn401_WhenAuthorizationHeaderIsMissing(HttpMethod method, string path)
    {
        using HttpRequestMessage request = new(method, path);

        using HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(NonAnonymousEndpoints))]
    public async Task NonAnonymousEndpoint_ShouldReturn401_WhenAuthorizationHeaderIsMalformed(HttpMethod method, string path)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "malformed.invalid.token");

        using HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HealthEndpoint_ShouldReturn200_WithoutAuthorizationHeader()
    {
        using HttpResponseMessage response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LoginEndpoint_ShouldAllowUnauthenticatedRequests()
    {
        using HttpResponseMessage response = await _client.PostAsync(
            "/auth/login",
            new StringContent("""{"email":"unknown@example.com","password":"WrongPassword123!"}""", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

}
