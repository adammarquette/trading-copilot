using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration tests verifying regression coverage for the dormant invitation plumbing
/// (<c>POST /auth/invitations</c>, <c>POST /auth/accept-invite</c>, <c>POST /auth/login</c>, <c>GET /auth/me</c>)
/// retained for future read-only/mentee logins (ADR-0017 §4, PRD R-18, gh#8, gh#121).
/// </summary>
public class UserInvitationEndpointsIntegrationTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);
    private sealed record UserMeResponse(Guid Id, string Email, string DisplayName);

    public UserInvitationEndpointsIntegrationTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UserInvitationLifecycle_ShouldIssueInvitation_AcceptInvite_CreateUser_AndAllowLogin()
    {
        HttpClient client = _factory.CreateClient();
        string operatorToken = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);

        string inviteeEmail = $"invited-user-{Guid.NewGuid():N}@example.com";

        // 1. POST /auth/invitations -> Issue invitation
        using HttpResponseMessage issueResponse = await client.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(inviteeEmail));
        issueResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        IssueInvitationResponse? inviteResult = await issueResponse.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(inviteResult);
        inviteResult.Id.Should().NotBeEmpty();
        inviteResult.Token.Should().NotBeNullOrWhiteSpace();
        inviteResult.ExpiresUtc.Should().BeAfter(DateTimeOffset.UtcNow);

        // 2. Direct DB Check -> Verify hash stored, plaintext token NEVER stored in DB
        string expectedTokenHash = InvitationTokens.Hash(inviteResult.Token);
        await ExecuteDbContextAsync(async db =>
        {
            Invitation? storedInvite = await db.Invitations.FirstOrDefaultAsync(i => i.Id == inviteResult.Id);
            ArgumentNullException.ThrowIfNull(storedInvite);
            storedInvite.Email.Should().Be(inviteeEmail);
            storedInvite.TokenHash.Should().Be(expectedTokenHash);
            storedInvite.Status.Should().Be(InvitationStatus.Pending);
        });

        // 3. POST /auth/accept-invite -> Accept invitation as unauthenticated invitee
        HttpClient anonymousClient = _factory.CreateClient();
        string newPassword = "StrongPassword123!";
        string displayName = "Invited Trader";

        using HttpResponseMessage acceptResponse = await anonymousClient.PostAsJsonAsync("/auth/accept-invite", new AcceptInviteRequest(inviteResult.Token, newPassword, displayName));
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginTokenResponse? acceptTokenResult = await acceptResponse.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(acceptTokenResult);
        acceptTokenResult.Token.Should().NotBeNullOrWhiteSpace();

        // 4. Direct DB Check -> Verify invitation marked Accepted & User created in DB
        await ExecuteDbContextAsync(async db =>
        {
            Invitation? acceptedInvite = await db.Invitations.FirstOrDefaultAsync(i => i.Id == inviteResult.Id);
            ArgumentNullException.ThrowIfNull(acceptedInvite);
            acceptedInvite.Status.Should().Be(InvitationStatus.Accepted);
            acceptedInvite.AcceptedByUserId.Should().NotBeNull();

            User? newUser = await db.Users.FirstOrDefaultAsync(u => u.Email == inviteeEmail);
            ArgumentNullException.ThrowIfNull(newUser);
            newUser.Id.Should().Be(acceptedInvite.AcceptedByUserId!.Value);
            newUser.DisplayName.Should().Be(displayName);
            newUser.Status.Should().Be(UserStatus.Active);
        });

        // 5. POST /auth/login -> New user authenticates with new credentials
        using HttpResponseMessage newLoginResponse = await anonymousClient.PostAsJsonAsync("/auth/login", new LoginRequest(inviteeEmail, newPassword));
        newLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginTokenResponse? loginResult = await newLoginResponse.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(loginResult);
        loginResult.Token.Should().NotBeNullOrWhiteSpace();

        // 6. GET /auth/me -> Authenticate with new token and verify user profile
        anonymousClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Token);
        using HttpResponseMessage meResponse = await anonymousClient.GetAsync("/auth/me");
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        UserMeResponse? me = await meResponse.Content.ReadFromJsonAsync<UserMeResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(me);
        me.Email.Should().Be(inviteeEmail);
        me.DisplayName.Should().Be(displayName);
    }

    [Fact]
    public async Task InvitedUser_ShouldNotSeeOperatorData_AndProbesInvitationChaining()
    {
        // 1. Operator registers a firm
        HttpClient operatorClient = _factory.CreateClient();
        string operatorToken = await AuthenticateAsync(operatorClient);
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);

        string firmName = $"Topstep-Operator-{Guid.NewGuid():N}";
        using HttpResponseMessage createFirmResp = await operatorClient.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        createFirmResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Operator issues invitation to a second user
        string inviteeEmail = $"invited-user-{Guid.NewGuid():N}@example.com";
        using HttpResponseMessage issueResponse = await operatorClient.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(inviteeEmail));
        IssueInvitationResponse? inviteResult = await issueResponse.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(inviteResult);

        // 3. Invitee accepts invite and obtains authentication Bearer token
        HttpClient inviteeClient = _factory.CreateClient();
        using HttpResponseMessage acceptResponse = await inviteeClient.PostAsJsonAsync(
            "/auth/accept-invite",
            new AcceptInviteRequest(inviteResult.Token, "NewPassword123!", "Mentee User"));
        LoginTokenResponse? acceptToken = await acceptResponse.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(acceptToken);
        inviteeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", acceptToken.Token);

        // 4. R-20 Default-Deny Assertion: Invitee GET /firms MUST return an empty list (sees none of operator's firms)
        using HttpResponseMessage getFirmsResponse = await inviteeClient.GetAsync("/firms");
        getFirmsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        List<FirmResponse>? inviteeFirms = await getFirmsResponse.Content.ReadFromJsonAsync<List<FirmResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(inviteeFirms);
        inviteeFirms.Should().BeEmpty("R-20 default-deny: invited second user must not see operator's registered firms");

        // 5. Invitation Chaining Probe: Test if invitee can issue further invitations
        using HttpResponseMessage chainInviteResponse = await inviteeClient.PostAsJsonAsync(
            "/auth/invitations",
            new IssueInvitationRequest($"chained-user-{Guid.NewGuid():N}@example.com"));

        // gh#128 RESOLVED: only the primary operator may issue invitations. This probe found the defect
        // (any valid token sufficed — an invitee could chain accounts); it is now the fix's regression guard.
        chainInviteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AcceptInvite_ShouldRefuseInvalidToken_WithBadRequest()
    {
        HttpClient anonymousClient = _factory.CreateClient();

        using HttpResponseMessage acceptResponse = await anonymousClient.PostAsJsonAsync(
            "/auth/accept-invite",
            new AcceptInviteRequest("invalid-token-xyz-12345", "Password123!", "Bogus User"));

        acceptResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AcceptInvite_ShouldRefuseAlreadyAcceptedToken_WithBadRequest()
    {
        HttpClient client = _factory.CreateClient();
        string operatorToken = await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);

        string inviteeEmail = $"invited-user-{Guid.NewGuid():N}@example.com";

        // Issue invitation
        using HttpResponseMessage issueResponse = await client.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(inviteeEmail));
        IssueInvitationResponse? inviteResult = await issueResponse.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(inviteResult);

        // Accept invitation first time -> OK
        HttpClient anonymousClient = _factory.CreateClient();
        using HttpResponseMessage firstAccept = await anonymousClient.PostAsJsonAsync(
            "/auth/accept-invite",
            new AcceptInviteRequest(inviteResult.Token, "Password123!", "First User"));
        firstAccept.StatusCode.Should().Be(HttpStatusCode.OK);

        // Accept invitation second time -> BadRequest
        using HttpResponseMessage secondAccept = await anonymousClient.PostAsJsonAsync(
            "/auth/accept-invite",
            new AcceptInviteRequest(inviteResult.Token, "Password123!", "Second User"));
        secondAccept.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AcceptInvite_ShouldRefuseExpiredToken_WithBadRequest()
    {
        string rawToken = Guid.NewGuid().ToString("N");
        string tokenHash = InvitationTokens.Hash(rawToken);
        string expiredEmail = $"expired-user-{Guid.NewGuid():N}@example.com";

        // Seed an expired invitation directly in DB
        await ExecuteDbContextAsync(async db =>
        {
            User operatorUser = await db.Users.FirstAsync(u => u.Email == PostgresApiFactory.OperatorEmail);
            Invitation expiredInvite = new()
            {
                Id = Guid.NewGuid(),
                Email = expiredEmail,
                TokenHash = tokenHash,
                IssuedByUserId = operatorUser.Id,
                Status = InvitationStatus.Pending,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-10),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-3), // Expired 3 days ago
            };
            db.Invitations.Add(expiredInvite);
            await db.SaveChangesAsync();
        });

        // Accept expired invitation -> BadRequest
        HttpClient anonymousClient = _factory.CreateClient();
        using HttpResponseMessage acceptResponse = await anonymousClient.PostAsJsonAsync(
            "/auth/accept-invite",
            new AcceptInviteRequest(rawToken, "Password123!", "Expired User"));

        acceptResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_ShouldReturn401_WhenPasswordIsIncorrect_OrUserDoesNotExist()
    {
        HttpClient anonymousClient = _factory.CreateClient();

        // 1. Valid user, wrong password -> 401 Unauthorized
        using HttpResponseMessage wrongPasswordResponse = await anonymousClient.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest(PostgresApiFactory.OperatorEmail, "WrongPassword999!"));
        wrongPasswordResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string wrongPasswordBody = await wrongPasswordResponse.Content.ReadAsStringAsync();

        // 2. Non-existent user email -> 401 Unauthorized (identical error, no user enumeration)
        using HttpResponseMessage nonexistentUserResponse = await anonymousClient.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("nonexistent-trader-999@example.com", "Password123!"));
        nonexistentUserResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string nonexistentUserBody = await nonexistentUserResponse.Content.ReadAsStringAsync();

        wrongPasswordBody.Should().Be(nonexistentUserBody, "both invalid credentials and non-existent users must return identical error response bodies to prevent user enumeration");
    }

    private async Task<string> AuthenticateAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Token;
    }

    /// <summary>
    /// Executes a DbContext operation within a properly disposed <see cref="AsyncServiceScope"/>.
    /// </summary>
    /// <remarks>
    /// Note: Direct DbContext reads here access un-scoped DbSets (Users, Invitations) without an HTTP tenant context.
    /// Tenant-owned DbSets (Firms, Connections, Accounts) filter by <see cref="MarqSpec.TradingCopilot.Data.Tenancy.ICurrentUser"/>
    /// and will evaluate to empty if not scoped to a specific user.
    /// </remarks>
    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
