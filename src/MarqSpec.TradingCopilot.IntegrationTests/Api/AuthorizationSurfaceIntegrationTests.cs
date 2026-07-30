using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The <b>authorization surface</b> (gh#511 ⇒ gh#8, R-18): every route the application maps must refuse an
/// anonymous caller, and the token itself must actually be validated.
/// </summary>
/// <remarks>
/// <para>
/// R-18 requires that execution, kill-switch and account endpoints sit behind the same gate — <i>no
/// unauthenticated path to order actions</i> (R-11 / R-16). Until this suite that was asserted for none of them:
/// <see cref="UnauthenticatedEndpointsTests"/> probes an <b>eleven-entry hand-maintained list</b> containing no
/// execution route, no kill-switch route and neither venue-truth read, against 45 authorized routes.
/// </para>
/// <para>
/// <b>Why enumeration rather than a longer list.</b> <c>AddAuthorization()</c> is registered with no
/// <c>FallbackPolicy</c>, so each route's gate is its own <c>.RequireAuthorization()</c> — individually
/// forgettable, with nothing above it to catch an omission. A hand list cannot notice a route that was never
/// added to it, and demonstrably does not: <c>PUT /connections/{id}/credentials</c> and
/// <c>DELETE /connections/{id}</c> shipped in gh#210 and were never added. Reading the route table from
/// <see cref="EndpointDataSource"/> means a new route is covered the moment it is mapped.
/// </para>
/// <para>
/// <b>The allow-list is hard-coded on purpose.</b> Deciding "should this route be authorized?" from the
/// endpoint's own <see cref="IAuthorizeData"/> metadata would be self-referential — a route missing
/// <c>.RequireAuthorization()</c> would classify itself as not-expected-to-be-authorized and be skipped, so the
/// sweep would go green on exactly the defect it exists to find. The four anonymous routes are named here, and
/// everything else must produce a 401.
/// </para>
/// </remarks>
public class AuthorizationSurfaceIntegrationTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public AuthorizationSurfaceIntegrationTests(PostgresApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Routes that say so — they carry <c>[AllowAnonymous]</c>, so being reachable is a decision on the record.
    /// </summary>
    private static string[] DeclaredAnonymous { get; } =
    [
        "POST /auth/login",
        "POST /auth/accept-invite",
    ];

    /// <summary>
    /// The probes, which are open because nothing gates them — <b>not</b> because anything declared them open.
    /// </summary>
    /// <remarks>
    /// Worth separating, because it is the same mechanism as the defect this suite hunts. With no
    /// <c>FallbackPolicy</c>, a route with neither <c>[AllowAnonymous]</c> nor <c>.RequireAuthorization()</c> is
    /// silently public, and these two are the only ones that may be. A new route that simply forgets its gate lands
    /// in exactly this category, which is why
    /// <see cref="TheSweep_ShouldEnumerateTheRealRouteTable_IncludingEveryExecutionAndKillSwitchRoute"/> pins the
    /// set by name rather than counting it.
    /// </remarks>
    private static string[] UngatedByOmission { get; } =
    [
        "GET /health",
        "GET /ready",
    ];

    /// <summary>The complete set of routes that may be reached without a token. Anything else must 401.</summary>
    private static HashSet<string> AnonymousRoutes { get; } =
        new([.. DeclaredAnonymous, .. UngatedByOmission], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Routes R-18 calls out by name. Membership is asserted explicitly so the sweep cannot quietly stop covering
    /// the ones that matter most — an execution or kill-switch route dropping out of enumeration would otherwise
    /// just shrink the loop.
    /// </summary>
    private static string[] MustBeCovered { get; } =
    [
        // Execution (R-11 / R-16) — an unauthenticated path here transmits a live order.
        "POST /accounts/{id:guid}/orders",
        "POST /accounts/{id:guid}/orders/arm",
        "POST /accounts/{id:guid}/orders/send-as-is",
        "POST /accounts/{id:guid}/orders/conditional",
        "PUT /orders/{id:guid}",
        "POST /orders/{id:guid}/take",
        "DELETE /orders/{id:guid}",
        "PATCH /orders/{id:guid}/price",

        // The kill switch (R-13) — engaging or, far worse, DISENGAGING it.
        "POST /kill-switch",
        "POST /kill-switch/disengage",
        "GET /kill-switch",

        // Venue truth — what the operator's account actually holds.
        "GET /accounts/{id:guid}/positions",
        "GET /accounts/{id:guid}/orders",

        // The risk limits the gate enforces (R-5).
        "PUT /accounts/{id:guid}/risk",
    ];

    // =================================================================================================================
    // The sweep
    // =================================================================================================================

    [Fact]
    public async Task EveryMappedRoute_ShouldReturn401_Anonymously()
    {
        List<string> reachable = [];

        foreach (RouteProbe probe in MappedRoutes())
        {
            if (AnonymousRoutes.Contains(probe.Key))
            {
                continue;
            }

            using HttpRequestMessage request = new(new HttpMethod(probe.Method), probe.Path);
            using HttpResponseMessage response = await _client.SendAsync(request);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                reachable.Add($"{probe.Key} → {(int)response.StatusCode}");
            }
        }

        reachable.Should().BeEmpty(
            "every mapped route except the four anonymous ones must refuse an anonymous caller (R-18). A route "
            + "listed here either has no .RequireAuthorization() — there is no FallbackPolicy to catch that — or "
            + "was deliberately made anonymous without being added to AnonymousRoutes");
    }

    [Fact]
    public async Task EveryMappedRoute_ShouldReturn401_WithAMalformedToken()
    {
        List<string> reachable = [];

        foreach (RouteProbe probe in MappedRoutes())
        {
            if (AnonymousRoutes.Contains(probe.Key))
            {
                continue;
            }

            using HttpRequestMessage request = new(new HttpMethod(probe.Method), probe.Path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "malformed.invalid.token");
            using HttpResponseMessage response = await _client.SendAsync(request);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                reachable.Add($"{probe.Key} → {(int)response.StatusCode}");
            }
        }

        reachable.Should().BeEmpty("a token that is not even a JWT must never authenticate anyone");
    }

    // =================================================================================================================
    // Guarding the guard — a sweep that enumerates nothing is green and worthless
    // =================================================================================================================

    [Fact]
    public void TheSweep_ShouldEnumerateTheRealRouteTable_IncludingEveryExecutionAndKillSwitchRoute()
    {
        IReadOnlyList<RouteProbe> probes = MappedRoutes();

        // Substituting route values wrongly is the quiet failure mode: `{id:guid}` filled with a non-GUID makes the
        // route 404 instead of 401, and the sweep above would pass while probing nothing real. So: the table must be
        // populated, and the routes R-18 names must be in it BY TEMPLATE.
        probes.Should().HaveCountGreaterThanOrEqualTo(45,
            "the application maps 49 routes, 4 of them anonymous; an enumeration returning fewer than 45 means the "
            + "route table is not being read and both sweeps are passing vacuously");

        string[] templates = [.. probes.Select(probe => probe.Key)];
        foreach (string required in MustBeCovered)
        {
            templates.Should().Contain(required,
                $"R-18 names {required} as sitting behind the gate, so the sweep must actually be probing it");
        }

        // Cross-check the hard-coded allow-list against the route table's own metadata. This does not decide the
        // sweep — it makes "a route became reachable" fail HERE, by name and by cause, rather than as a puzzling
        // 200 fifty lines up.
        string[] declared = [.. probes.Where(probe => probe.IsAnonymous).Select(probe => probe.Key)];
        declared.Should().BeEquivalentTo(DeclaredAnonymous,
            "the only routes declaring [AllowAnonymous] must be the two this suite sanctions");

        // The sharp one. With no FallbackPolicy, a route carrying NEITHER [AllowAnonymous] nor an authorization
        // policy is silently public — that is the whole failure mode, and it is a property of the route table, so
        // it can be named rather than inferred from a status code. Anything new appearing here forgot its gate.
        string[] ungated =
        [
            .. probes.Where(probe => !probe.IsAnonymous && !probe.IsAuthorized).Select(probe => probe.Key)
        ];
        ungated.Should().BeEquivalentTo(UngatedByOmission,
            "only the liveness and readiness probes may be reachable through the ABSENCE of a gate; any other "
            + "route here has no .RequireAuthorization() and there is no FallbackPolicy to save it");
    }

    [Fact]
    public async Task TheFourAnonymousRoutes_ShouldBeReachable_WithoutAToken()
    {
        // The other half of the allow-list's honesty: if these silently began requiring a token, the sweep would
        // still be green (it skips them), and the deployment would be broken with nothing to say so.
        using (HttpResponseMessage health = await _client.GetAsync("/health"))
        {
            health.StatusCode.Should().Be(HttpStatusCode.OK, "the liveness probe must not require a token");
        }

        using (HttpResponseMessage ready = await _client.GetAsync("/ready"))
        {
            ready.StatusCode.Should().Be(HttpStatusCode.OK, "the readiness probe must not require a token");
        }

        // Valid credentials, deliberately: an unknown-credentials 401 is indistinguishable from the "no token" 401
        // this suite is about, so it could never witness login becoming authorized.
        using (HttpResponseMessage login = await _client.PostAsJsonAsync(
            "/auth/login", new { email = PostgresApiFactory.OperatorEmail, password = PostgresApiFactory.OperatorPassword }))
        {
            login.StatusCode.Should().Be(HttpStatusCode.OK, "logging in is how a caller obtains a token at all");
        }

        using (HttpResponseMessage accept = await _client.PostAsJsonAsync(
            "/auth/accept-invite", new { token = "not-a-real-invitation", password = "Whatever123!" }))
        {
            accept.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "accepting an invitation is by nature done by someone who has no token yet; it must fail on the "
                + "invitation being bad, not on authentication");
        }
    }

    // =================================================================================================================
    // The token itself is validated — four one-line flips nothing else in the repo can catch
    // =================================================================================================================

    [Fact]
    public async Task ProtectedRoute_ShouldSucceed_WithACorrectlyMintedToken()
    {
        // THE POSITIVE CONTROL for every negative case below. Each of those differs from this token by exactly one
        // field, so without this they could all be passing because the minting itself is wrong — the whole set
        // would be vacuous and would report validation that is not happening.
        string token = await MintTokenAsync();

        using HttpResponseMessage response = await GetAuthMeAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a token minted exactly as JwtTokenIssuer mints one must be accepted — otherwise the negative cases "
            + "below prove nothing about lifetime, key, issuer or audience");
    }

    [Fact]
    public async Task ProtectedRoute_ShouldReturn401_WithAnExpiredToken()
    {
        // Well past the 5-minute default ClockSkew, so this cannot pass or fail on tolerance.
        string token = await MintTokenAsync(expires: DateTime.UtcNow.AddMinutes(-30));

        using HttpResponseMessage response = await GetAuthMeAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "ValidateLifetime must be enforced. Nothing else in the repository can witness this: the existing "
            + "malformed-token theory sends a garbage string, which dies at JWT parse and so returns 401 whether "
            + "or not lifetimes are checked at all");
    }

    [Fact]
    public async Task ProtectedRoute_ShouldReturn401_WithATokenSignedByAnotherKey()
    {
        string token = await MintTokenAsync(signingKey: "a-completely-different-signing-key-also-32-bytes-long");

        using HttpResponseMessage response = await GetAuthMeAsync(token);

        // This guards SIGNATURE VERIFICATION against the configured key — that the deployment trusts exactly one
        // secret — not the ValidateIssuerSigningKey flag, which is a different thing than its name suggests: it
        // validates the signing key's own properties (a certificate's lifetime and so on) and turning it off does
        // NOT stop the signature being checked. Verified: flipping that flag to false leaves this test green,
        // whereas adding a second key to IssuerSigningKeys turns it red while the positive control stays green.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the deployment must trust exactly the key it was configured with — otherwise anyone holding any "
            + "other key mints themselves the operator's identity, which is an unauthenticated path to every "
            + "execution route");
    }

    [Theory]
    [InlineData("https://attacker.example/issuer", null)]
    [InlineData(null, "some-other-audience")]
    public async Task ProtectedRoute_ShouldReturn401_WithAForeignIssuerOrAudience(string? issuer, string? audience)
    {
        // One flag per case, so each of ValidateIssuer and ValidateAudience has a guard that goes red on its own.
        string token = await MintTokenAsync(issuer: issuer, audience: audience);

        using HttpResponseMessage response = await GetAuthMeAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token minted for a different issuer or audience is not a token for this deployment");
    }

    // =================================================================================================================
    // Helpers
    // =================================================================================================================

    private async Task<HttpResponseMessage> GetAuthMeAsync(string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Mints a token the way <c>JwtTokenIssuer</c> does, with one field overridable per test. Defaults come from
    /// the host's own configuration, so the positive control proves the shape is right before anything is varied.
    /// </summary>
    private async Task<string> MintTokenAsync(
        string? signingKey = null,
        string? issuer = null,
        string? audience = null,
        DateTime? expires = null)
    {
        SigningCredentials credentials = new(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? HostSetting("Jwt:SigningKey"))),
            SecurityAlgorithms.HmacSha256);

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = issuer ?? HostSetting("Jwt:Issuer"),
            Audience = audience ?? HostSetting("Jwt:Audience"),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(60),
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = (await OperatorIdAsync()).ToString(),
                [JwtRegisteredClaimNames.Email] = PostgresApiFactory.OperatorEmail,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private string HostSetting(string key) =>
        _factory.Services.GetRequiredService<IConfiguration>()[key]
        ?? throw new InvalidOperationException($"The host has no {key} configured.");

    /// <summary>
    /// The seeded operator's id, taken from a real login rather than the database — so the minted token carries the
    /// same subject production issues, and the positive control exercises the genuine user lookup behind /auth/me.
    /// </summary>
    private async Task<Guid> OperatorIdAsync()
    {
        using HttpResponseMessage login = await _client.PostAsJsonAsync(
            "/auth/login", new { email = PostgresApiFactory.OperatorEmail, password = PostgresApiFactory.OperatorPassword });
        login.EnsureSuccessStatusCode();

        LoginTokenResponse? issued = await login.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(issued);

        JsonWebToken parsed = new JsonWebTokenHandler().ReadJsonWebToken(issued.Token);
        return Guid.Parse(parsed.GetClaim(JwtRegisteredClaimNames.Sub).Value);
    }

    /// <summary>Reads the application's real route table.</summary>
    private IReadOnlyList<RouteProbe> MappedRoutes()
    {
        // IEnumerable<EndpointDataSource>, not a bare GetRequiredService<EndpointDataSource>(): the host composes
        // several sources and resolving the single service throws or returns only one of them.
        IEnumerable<EndpointDataSource> sources = _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();

        List<RouteProbe> probes = [];
        foreach (RouteEndpoint endpoint in sources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            string? template = endpoint.RoutePattern.RawText;
            if (template is null)
            {
                continue;
            }

            HttpMethodMetadata? methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
            bool anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null;
            bool authorized = endpoint.Metadata.GetMetadata<IAuthorizeData>() != null;

            foreach (string method in methods?.HttpMethods ?? [])
            {
                probes.Add(new RouteProbe(method, Normalize(template), anonymous, authorized));
            }
        }

        return probes;
    }

    private static string Normalize(string template) => "/" + template.Trim('/');

    private sealed record LoginTokenResponse(string Token);

    /// <summary>One (verb, route) pair, with the concrete path to send at it.</summary>
    private sealed record RouteProbe(string Method, string Template, bool IsAnonymous, bool IsAuthorized)
    {
        /// <summary>The stable identity used in allow-lists and failure messages, e.g. <c>POST /kill-switch</c>.</summary>
        public string Key => $"{Method} {Template}";

        /// <summary>
        /// The template with route values filled in. A <c>:guid</c> constraint must receive a real GUID or routing
        /// returns 404 and the probe silently stops testing authorization.
        /// </summary>
        public string Path => ParameterPattern.Replace(Template, match =>
            match.Groups["constraint"].Value.Contains("guid", StringComparison.OrdinalIgnoreCase)
                ? Guid.NewGuid().ToString()
                : "probe");

        private static Regex ParameterPattern { get; } =
            new(@"\{(?<name>[^}:]+)(?<constraint>:[^}]+)?\}", RegexOptions.Compiled);
    }
}
