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
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// End-to-end R-20 default-deny workspace isolation between two authenticated operators against real PostgreSQL
/// (ADR-0017, gh#132). Operator A owns firms, connections, accounts, and staged orders; Operator B — a second
/// user created through the invitation flow (gh#8, dormant per ADR-0017 §4) — must never read, enumerate, or
/// mutate any of them. Cross-tenant requests return <c>404 Not Found</c>, never <c>403</c> or detail, so the
/// workspace's very existence cannot be probed.
/// </summary>
/// <remarks>
/// The isolation lives in the per-user query filter every <see cref="IUserOwned"/> entity carries
/// (<c>TenantDbContext</c>): a request scoped to B simply cannot see A's rows. This suite proves that property
/// at the HTTP surface for the routes that exist on <c>develop</c>, and — where no read surface exists yet
/// (gate decisions) — directly at the <see cref="TradingCopilotDbContext"/>. Realigned to the real route map by
/// gh#160 (the original by-id connection read and gate-decision audit endpoint do not exist). Backed by the
/// adversarial venue stub (the sole sanctioned double). Traces R-20 · ADR-0017.
/// </remarks>
public class MultiTenantIsolationIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);

    public MultiTenantIsolationIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListConnections_ShouldExcludeOtherOperatorsConnections()
    {
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient operatorB, _, _) = await CreateSecondOperatorAsync(operatorA);

        Guid connectionA = await CreateConnectionAsync(operatorA, "Topstep-Isolation-Connections");

        // Positive control: A sees its own connection (so B's exclusion is real isolation, not an empty system).
        List<ConnectionResponse> seenByA = await GetConnectionsAsync(operatorA);
        seenByA.Should().Contain(c => c.Id == connectionA, "the owning operator sees its own connection");

        // R-20: B's collection is scoped to B — it can contain none of A's.
        List<ConnectionResponse> seenByB = await GetConnectionsAsync(operatorB);
        seenByB.Should().NotContain(c => c.Id == connectionA, "a second operator must never enumerate operator A's connections");
    }

    [Fact]
    public async Task GetAccounts_ShouldReturn404_WhenOperatorBQueriesOperatorAConnection()
    {
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient operatorB, _, _) = await CreateSecondOperatorAsync(operatorA);

        (Guid connectionA, _) = await SetupAccountAsync(operatorA, "Topstep-Isolation-Accounts");

        // Positive control: A can read its own connection's accounts.
        using HttpResponseMessage asA = await operatorA.GetAsync($"/connections/{connectionA}/accounts");
        asA.StatusCode.Should().Be(HttpStatusCode.OK, "the owning operator reads its own account roster");

        // R-20: to B, operator A's connection does not exist — a 404, never a 403 or an empty-but-200 that would
        // confirm the id is real.
        using HttpResponseMessage asB = await operatorB.GetAsync($"/connections/{connectionA}/accounts");
        asB.StatusCode.Should().Be(HttpStatusCode.NotFound, "operator A's connection must be invisible to operator B");
    }

    [Fact]
    public async Task StagedOrderActions_ShouldReturn404_WhenOperatorBTakesOrCancelsOperatorAOrder()
    {
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient operatorB, _, _) = await CreateSecondOperatorAsync(operatorA);

        (_, Guid accountA) = await SetupAccountAsync(operatorA, "Topstep-Isolation-Orders");
        await DeclareRiskProfileAsync(operatorA, accountA);
        Guid orderA = await ArmAsync(operatorA, accountA);

        // R-20: B can neither take nor cancel A's staged order — both are 404 (the order does not exist for B).
        using HttpResponseMessage takeByB = await operatorB.PostAsync($"/orders/{orderA}/take", content: null);
        takeByB.StatusCode.Should().Be(HttpStatusCode.NotFound, "operator B must not be able to take operator A's staged order");

        using HttpResponseMessage cancelByB = await operatorB.DeleteAsync($"/orders/{orderA}");
        cancelByB.StatusCode.Should().Be(HttpStatusCode.NotFound, "operator B must not be able to cancel operator A's staged order");

        // The order is untouched — still A's, still Staged.
        await ExecuteDbContextAsync(async db =>
        {
            Order order = await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == orderA);
            order.Status.Should().Be(OrderStatus.Staged, "neither of B's forbidden actions may have moved the order");
        });
    }

    [Fact]
    public async Task GateDecisions_ShouldBeVisibleOnlyToTheirOwner_AtTheDbContext()
    {
        // Parked at the DbContext level per gh#160: GateDecisionRecord is persisted but has no read endpoint, so
        // the HTTP guard cannot be exercised yet. This proves the isolation property directly — the same per-user
        // query filter (ADR-0017) that backs the HTTP routes above — and becomes the HTTP test's foundation when
        // a gate-decision read surface lands.
        HttpClient operatorA = await AuthenticatedOperatorClientAsync();
        (HttpClient operatorB, Guid userIdB, _) = await CreateSecondOperatorAsync(operatorA);
        Guid userIdA = await OperatorUserIdAsync();

        // Each operator gets a real, FK-valid account, then one gate decision apiece.
        (_, Guid accountA) = await SetupAccountAsync(operatorA, "Topstep-Isolation-GateA");
        (_, Guid accountB) = await SetupAccountAsync(operatorB, "Topstep-Isolation-GateB");

        Guid decisionA = Guid.NewGuid();
        Guid decisionB = Guid.NewGuid();
        await ExecuteDbContextAsync(async db =>
        {
            db.GateDecisions.Add(SeedDecision(decisionA, userIdA, accountA));
            db.GateDecisions.Add(SeedDecision(decisionB, userIdB, accountB));
            await db.SaveChangesAsync();
        });

        // A context scoped to B sees B's decision and NONE of A's; and the mirror for A.
        List<GateDecisionRecord> visibleToB = await GateDecisionsVisibleToAsync(userIdB);
        visibleToB.Should().Contain(d => d.Id == decisionB);
        visibleToB.Should().NotContain(d => d.Id == decisionA, "operator B's context must not surface operator A's gate decisions");

        List<GateDecisionRecord> visibleToA = await GateDecisionsVisibleToAsync(userIdA);
        visibleToA.Should().Contain(d => d.Id == decisionA);
        visibleToA.Should().NotContain(d => d.Id == decisionB, "operator A's context must not surface operator B's gate decisions");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Operators.
    // ---------------------------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthenticatedOperatorClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>Creates a second operator through the invitation flow and returns an authenticated client plus its
    /// user id and email. The primary operator issues the invite; the invitee accepts it anonymously.</summary>
    private async Task<(HttpClient Client, Guid UserId, string Email)> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"operator-b-{Guid.NewGuid():N}@example.com";

        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync("/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK, "the primary operator may issue invitations");
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "OperatorB-Pass123!", "Operator B"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        Guid userId = await QueryDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        return (client, userId, email);
    }

    private Task<Guid> OperatorUserIdAsync() => QueryDbAsync(db =>
        db.Users.Where(u => u.Email == PostgresApiFactory.OperatorEmail).Select(u => u.Id).SingleAsync());

    // ---------------------------------------------------------------------------------------------------------
    // Resource setup (as whichever operator owns the client).
    // ---------------------------------------------------------------------------------------------------------

    private async Task<Guid> CreateConnectionAsync(HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync("/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage createConn = await client.PostAsJsonAsync("/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Id;
    }

    /// <summary>Firm → conventions (Practice ⇒ no capital at risk) → connection → discovered accounts. Returns the
    /// connection id and a tradeable account id.</summary>
    private async Task<(Guid ConnectionId, Guid AccountId)> SetupAccountAsync(HttpClient client, string firmName)
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

        return (connection.Id, accounts.First(account => account.CanTrade).Id);
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
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's proposal must arm cleanly");
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    private async Task<List<ConnectionResponse>> GetConnectionsAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/connections");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<ConnectionResponse>? connections = await response.Content.ReadFromJsonAsync<List<ConnectionResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connections);
        return connections;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Data helpers.
    // ---------------------------------------------------------------------------------------------------------

    private static GateDecisionRecord SeedDecision(Guid id, Guid userId, Guid accountId) => new()
    {
        Id = id,
        UserId = userId,
        AccountId = accountId,
        Outcome = GateOutcome.Allowed,
        ApprovedQuantity = 1,
        Reason = "seeded for isolation coverage (gh#132)",
        DecidedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Reads the gate decisions a <see cref="TradingCopilotDbContext"/> scoped to <paramref name="userId"/>
    /// can see — i.e. through the live per-user query filter, not <c>IgnoreQueryFilters</c>.</summary>
    private async Task<List<GateDecisionRecord>> GateDecisionsVisibleToAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext scoped = new(options, new FixedUser(userId));
        return await scoped.GateDecisions.ToListAsync();
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    /// <summary>A fixed authenticated-user accessor, so a <see cref="TradingCopilotDbContext"/> can be scoped to a
    /// specific operator outside an HTTP request — exactly what the per-user query filter reads.</summary>
    private sealed class FixedUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }
}
