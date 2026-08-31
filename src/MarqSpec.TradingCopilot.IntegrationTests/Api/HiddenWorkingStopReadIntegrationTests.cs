using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for gh#934 (of gh#865, gh#656, gh#267; R-11, ADR-0007) — the <b>PLATFORM-held hidden
/// working-stop</b> now carried on <c>GET /accounts/{id}/orders</c> alongside the venue-truth resting-orders
/// read: <c>workingStopPrice</c>, <c>stopStaging</c>, <c>safetyStopPrice</c> and <c>entryPrice</c>, joined from
/// the local <see cref="StopPlanRecord"/> to the venue-reported leg by the journaled <see cref="Order"/>'s id.
/// </summary>
/// <remarks>
/// <para>
/// Written independently of <c>WorkingOrderReconciliationEndpoints.ReadAsync</c> / <c>MapRestingOrder</c> — from
/// the gh#934 issue body, not the implementation (QA contract §Role). <c>Api/WorkingOrderReadIntegrationTests.cs</c>
/// (gh#656) already proves the venue-truth half (size, protective classification, the declared-unknown basis,
/// R-20 at the account level); this suite is the independent proof of the four PLATFORM-held fields it does not
/// touch — none of which is venue truth, so a fabricated <see cref="StopPlan"/>-shaped stub could never stand in
/// for the real join over real Postgres.
/// </para>
/// <para>
/// The four fields are asserted as <b>present or absent together</b> (a working stop without its staging or band
/// is not safely movable), the join is asserted to be <b>Working-scoped</b> (a terminal order's plan is never
/// surfaced even when the venue still reports the leg resting — a stale post-fill report), and R-20 is re-proven
/// at the <i>plan</i> level, not only the account level: two operators seeded with distinct geometry must never
/// see each other's numbers merged into their own response.
/// </para>
/// </remarks>
public class HiddenWorkingStopReadIntegrationTests : IClassFixture<WorkingOrderReadTestPostgresFactory>
{
    private readonly WorkingOrderReadTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public HiddenWorkingStopReadIntegrationTests(WorkingOrderReadTestPostgresFactory factory)
    {
        _factory = factory;
    }

    private const string VenueKeyA = "PRAC-50K-101";
    private const string VenueKeyB = "50KTC-V2-202";
    private const string Contract = "MESU26";

    [Fact]
    public async Task Read_ShouldSurfaceHiddenPlan_MatchedByJournaledOrderId()
    {
        // The named behavior: a Working order with a Hidden StopPlanRecord returns workingStopPrice ==
        // ActualStopPrice, stopStaging == "Hidden", and the safety/entry band — matched to the VENUE leg by the
        // journaled Order.Id, not by array position or venue key alone.
        (HttpClient client, Guid accountId, Guid operatorId) = await FreshAsync();
        VenueFactory.SeedWorkingOrder(VenueKeyA, "STOP-1", Contract, stopPrice: 4_980m, size: 3);
        Guid orderId = await SeedJournaledOrderAsync(accountId, operatorId, "STOP-1");
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden, actualStop: 4_990m, safety: 4_980m, entry: 5_000m);

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement leg = await LegAsync(response, "STOP-1");
        leg.GetProperty("orderId").GetGuid().Should().Be(orderId, "the platform fields are matched by the journaled Order.Id");
        leg.GetProperty("workingStopPrice").GetDecimal().Should().Be(4_990m, "the hidden working stop is ActualStopPrice");
        leg.GetProperty("stopStaging").GetString().Should().Be("Hidden");
        leg.GetProperty("safetyStopPrice").GetDecimal().Should().Be(4_980m);
        leg.GetProperty("entryPrice").GetDecimal().Should().Be(5_000m);

        // Venue-truth fields unaffected (a pure additive read) — asserted here rather than a separate test so a
        // future join regression that clobbers the venue-truth projection while assembling the platform fields
        // fails on the SAME row being asserted for the new fields.
        leg.GetProperty("stopPrice").GetDecimal().Should().Be(4_980m, "the venue-truth stop trigger is untouched by the platform join");
        leg.GetProperty("size").GetInt32().Should().Be(3, "the venue-truth size is untouched by the platform join");
    }

    [Fact]
    public async Task Read_ShouldReturnAllFourFieldsNull_WhenTheJournaledOrderHasNoStopPlan()
    {
        // Present-or-absent as a whole: a bare entry (journaled, matched, but no StopPlanRecord at all) must never
        // surface a PARTIAL platform shape — e.g. a working stop with no staging. orderId is still present; only
        // the four platform fields are null.
        (HttpClient client, Guid accountId, Guid operatorId) = await FreshAsync();
        VenueFactory.SeedWorkingOrder(VenueKeyA, "BARE-1", Contract, stopPrice: 4_980m, size: 1);
        Guid orderId = await SeedJournaledOrderAsync(accountId, operatorId, "BARE-1");

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement leg = await LegAsync(response, "BARE-1");
        leg.GetProperty("orderId").GetGuid().Should().Be(orderId, "the order IS journaled and matched — only the plan is absent");
        leg.GetProperty("workingStopPrice").ValueKind.Should().Be(JsonValueKind.Null);
        leg.GetProperty("stopStaging").ValueKind.Should().Be(JsonValueKind.Null);
        leg.GetProperty("safetyStopPrice").ValueKind.Should().Be(JsonValueKind.Null);
        leg.GetProperty("entryPrice").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Read_ShouldReturnAllFourFieldsNull_ForAVenueSpawnedLegWithNoOrderRow()
    {
        // The other absence case named in the acceptance criteria: a venue-spawned protective leg carries no
        // Order row at all (ADR-0007) — orderId is null, and so are the four platform fields (there is nothing
        // to key the plan lookup on).
        (HttpClient client, Guid accountId, _) = await FreshAsync();
        VenueFactory.SeedWorkingOrder(VenueKeyA, "SPAWNED-1", Contract, stopPrice: 4_980m, size: 1);

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement leg = await LegAsync(response, "SPAWNED-1");
        leg.GetProperty("orderId").ValueKind.Should().Be(JsonValueKind.Null, "no Order row exists for this leg");
        leg.GetProperty("workingStopPrice").ValueKind.Should().Be(JsonValueKind.Null);
        leg.GetProperty("stopStaging").ValueKind.Should().Be(JsonValueKind.Null);
        leg.GetProperty("safetyStopPrice").ValueKind.Should().Be(JsonValueKind.Null);
        leg.GetProperty("entryPrice").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(StopStaging.Native)]
    [InlineData(StopStaging.Orphaned)]
    public async Task Read_ShouldExposeStopStaging_ForNonHiddenPlans(StopStaging staging)
    {
        // "The UI gates its local-move control on Hidden only, so the staging must be readable" for every kind —
        // a Native (promoted to the venue) or Orphaned (re-arm pending) plan still surfaces its stopStaging, not
        // just a Hidden one. A read that only special-cased Hidden would leave the move-stop UI unable to tell a
        // movable stop from a promoted or orphaned one.
        (HttpClient client, Guid accountId, Guid operatorId) = await FreshAsync();
        VenueFactory.SeedWorkingOrder(VenueKeyA, "STAGED-1", Contract, stopPrice: 4_980m, size: 1);
        Guid orderId = await SeedJournaledOrderAsync(accountId, operatorId, "STAGED-1");
        await SeedStopPlanAsync(orderId, operatorId, staging, actualStop: 4_990m, safety: 4_980m, entry: 5_000m);

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement leg = await LegAsync(response, "STAGED-1");
        leg.GetProperty("stopStaging").GetString().Should().Be(staging.ToString(),
            $"a {staging} plan's staging must be readable so the UI can gate its move control on it");
        leg.GetProperty("workingStopPrice").GetDecimal().Should().Be(4_990m, "the band still surfaces alongside a non-Hidden staging");
    }

    [Fact]
    public async Task Read_ShouldNeverSurfaceThePlan_ForATerminalOrder()
    {
        // Working-scoped: a plan for an order that has already gone terminal (Filled) must NEVER surface, even
        // when the venue still (staleness) reports the leg resting — the join keys off Working-only journaled
        // ids, so a terminal order's id never enters it. Surfacing it would offer a move control on an order that
        // is gone.
        (HttpClient client, Guid accountId, Guid operatorId) = await FreshAsync();
        VenueFactory.SeedWorkingOrder(VenueKeyA, "TERM-1", Contract, stopPrice: 4_980m, size: 1);
        Guid orderId = await SeedJournaledOrderAsync(accountId, operatorId, "TERM-1", status: OrderStatus.Filled);
        await SeedStopPlanAsync(orderId, operatorId, StopStaging.Hidden, actualStop: 4_990m, safety: 4_980m, entry: 5_000m);

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement leg = await LegAsync(response, "TERM-1");
        leg.GetProperty("orderId").ValueKind.Should().Be(JsonValueKind.Null,
            "the journaled-id join is Working-scoped; a Filled order's id never enters it");
        leg.GetProperty("workingStopPrice").ValueKind.Should().Be(JsonValueKind.Null,
            "the plan row still exists in the DB, but a terminal order's plan must never reach the wire");
        leg.GetProperty("stopStaging").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Read_ShouldScopeThePlatformFields_ToTheirOwner_AcrossTwoOperators()
    {
        // R-20 at the PLAN level, not merely the account level: two operators each hold a Working order with a
        // Hidden plan, seeded with deliberately DIFFERENT geometry. Reading operator B's own account must return
        // ONLY B's numbers — never A's, even mixed in. Both operators discover the shared fixed venue roster
        // (AdversarialTestProjectXVenueFactory.GetAccountsAsync), but each gets a distinct owner-scoped app-level
        // Account row and StopPlanRecord — a regression that ever widened the plan lookup off the caller's own
        // owner-scoped order ids (e.g. by venue key alone) would leak here.
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient operatorA = await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);
        Guid accountIdA = await SetupAccountAsync(operatorA, VenueKeyA);
        Guid operatorIdA = await OperatorIdAsync(PostgresApiFactory.OperatorEmail);
        VenueFactory.SeedWorkingOrder(VenueKeyA, "STOP-A", Contract, stopPrice: 4_980m, size: 1);
        Guid orderIdA = await SeedJournaledOrderAsync(accountIdA, operatorIdA, "STOP-A");
        await SeedStopPlanAsync(orderIdA, operatorIdA, StopStaging.Hidden, actualStop: 4_990m, safety: 4_980m, entry: 5_000m);

        (HttpClient operatorB, string emailB) = await SecondOperatorClientAsync(operatorA);
        Guid accountIdB = await SetupAccountAsync(operatorB, VenueKeyB);
        Guid operatorIdB = await OperatorIdAsync(emailB);
        VenueFactory.SeedWorkingOrder(VenueKeyB, "STOP-B", Contract, stopPrice: 1_980m, size: 1);
        Guid orderIdB = await SeedJournaledOrderAsync(accountIdB, operatorIdB, "STOP-B");
        await SeedStopPlanAsync(orderIdB, operatorIdB, StopStaging.Hidden, actualStop: 1_990m, safety: 1_980m, entry: 2_000m);

        using HttpResponseMessage response = await operatorB.GetAsync($"/accounts/{accountIdB}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("STOP-A", "operator B's read must never carry operator A's venue key");
        body.Should().NotContain("4990", "operator B's read must never carry operator A's working-stop price");
        body.Should().NotContain("4980", "operator B's read must never carry operator A's safety-stop price");

        JsonElement leg = await LegAsync(response, "STOP-B");
        leg.GetProperty("orderId").GetGuid().Should().Be(orderIdB);
        leg.GetProperty("workingStopPrice").GetDecimal().Should().Be(1_990m, "operator B sees B's own working stop");
        leg.GetProperty("safetyStopPrice").GetDecimal().Should().Be(1_980m, "operator B sees B's own safety stop");
        leg.GetProperty("entryPrice").GetDecimal().Should().Be(2_000m, "operator B sees B's own entry");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    private static async Task<JsonElement> LegAsync(HttpResponseMessage response, string venueOrderKey)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("orders").Clone().EnumerateArray()
            .Single(order => order.GetProperty("venueOrderKey").GetString() == venueOrderKey);
    }

    /// <summary>Resets the venue and DB state, and stands up one primary-operator account.</summary>
    private async Task<(HttpClient Client, Guid AccountId, Guid OperatorId)> FreshAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(async db =>
        {
            await db.StopPlans.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        });

        HttpClient client = await AuthenticatedClientAsync(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword);
        Guid accountId = await SetupAccountAsync(client, VenueKeyA);
        Guid operatorId = await OperatorIdAsync(PostgresApiFactory.OperatorEmail);
        return (client, accountId, operatorId);
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

    private async Task<(HttpClient Client, string Email)> SecondOperatorClientAsync(HttpClient operatorClient)
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
        return (client, email);
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client, string venueKey)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-HiddenStop-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        // Both stages the fixed venue roster can resolve to (VenueKeyA -> Practice, VenueKeyB "50KTC-V2-202" ->
        // Evaluation per ProjectXAccountStage) are declared no-capital-at-risk, so every account this suite
        // discovers resolves to TradingMode.Practice -- matching the Mode a directly-seeded Order carries (the
        // R-14 mode guard rejects a mismatch at the DB).
        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.VenueAccountKey == venueKey).Id;
    }

    private async Task<Guid> SeedJournaledOrderAsync(
        Guid accountId,
        Guid userId,
        string venueOrderKey,
        OrderStatus status = OrderStatus.Working,
        decimal entry = 5_000m,
        decimal working = 4_990m,
        decimal safety = 4_980m,
        int size = 1)
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
                Symbol = "MES",
                Side = OrderSide.Buy,
                Size = size,
                Type = OrderType.Limit,
                EntryPrice = entry,
                LimitPrice = entry,
                WorkingStopPrice = working,
                SafetyStopPrice = safety,
                ReferencePrice = entry,
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

    private async Task SeedStopPlanAsync(
        Guid orderId, Guid userId, StopStaging staging, decimal actualStop, decimal safety, decimal entry)
    {
        await ExecuteDbAsync(async db =>
        {
            db.StopPlans.Add(new StopPlanRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrderId = orderId,
                Side = OrderSide.Buy,
                EntryPrice = entry,
                ActualStopPrice = actualStop,
                SafetyStopPrice = safety,
                ProximityMetric = StopProximityMetric.Ticks,
                ProximityValue = 8m,
                Staging = staging,
            });
            await db.SaveChangesAsync();
        });
    }

    private Task<Guid> OperatorIdAsync(string email) =>
        QueryDbAsync(async db => (await db.Users.FirstAsync(u => u.Email == email)).Id);

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

    private sealed record LoginTokenResponse(string Token);
    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);
}
