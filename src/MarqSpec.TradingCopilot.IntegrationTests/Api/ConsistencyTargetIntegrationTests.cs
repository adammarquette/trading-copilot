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
/// The prop-firm <b>consistency target</b> end to end (gh#394 ⇒ gh#380, R-5, ADR-0007): a cap on how much of an
/// evaluation's cumulative profit may come from any single day. Unlike every other risk layer it protects payout
/// <i>eligibility</i> rather than capital, so whether a breach <b>refuses</b> or only <b>warns</b> belongs to the
/// account — hence two postures.
/// </summary>
/// <remarks>
/// <para>
/// Written from the <b>spec</b> (gh#380's scope and acceptance, R-5, ADR-0007's gh#380 Update), not from the
/// implementation. The unit suite already covers the arithmetic in isolation; what it cannot cover is the thing
/// that was actually broken for this rule's entire prior life — <b>the target was persisted, validated and
/// returned by the API and still never reached the gate</b>. Every layer was correct alone. Only declaring a
/// profile over HTTP, trading against it, and observing the gate's decision catches that class of defect.
/// </para>
/// <para>
/// <b>Tier:</b> pre-merge, container-backed Postgres. Venue-independent — the venue seam is the one sanctioned
/// stub and it is <b>adversarial</b>: it accepts orders and reports fills, and never supplies the consumed
/// fraction the system is supposed to compute. Every window in this suite is built from <c>Trade</c> rows.
/// </para>
/// <para>
/// <b>Two guards pin a DEFECT rather than the intended behaviour</b> — see <c>gh#407</c>. The gate <i>computes</i>
/// a <c>GateAdvisory</c> and attaches it to the decision, but nothing carries it across the API boundary:
/// <c>SendOrderResponse</c> has no advisory field and <c>GateDecisionRecord</c> does not persist one. Since
/// <c>Advisory</c> is the migration default, every pre-existing account is on the one posture that produces no
/// observable output at all. Pinned per the QA contract's rule 2; the pins flip when gh#407 lands.
/// </para>
/// </remarks>
public class ConsistencyTargetIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public ConsistencyTargetIntegrationTests(StubbedVenuePostgresFactory factory) => _factory = factory;

    // -------------------------------------------------------------------------------------------------------
    // The last mile — the gh#380 defect itself
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConsistencyTarget_ShouldReachTheGate_WhenDeclaredOverTheRiskApi()
    {
        // THE regression guard for gh#380. The target was persisted, validated and returned correctly while the
        // mapping from the stored profile to AccountRiskRules dropped it, so the gate never saw it. Nothing short
        // of declaring over HTTP and observing a decision can witness that: every layer looked right alone.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.40m, enforcement: ConsistencyEnforcement.Block);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 900m), (Utc(7, 22, 15), 100m)); // best 90% of 1000

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);
        SendOrderResponse? decision = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);

        decision.Should().NotBeNull();
        decision!.BindingLayer.Should().Be(
            RiskLayer.ConsistencyTarget,
            "a target declared over the risk API must reach the gate — it was configurable but unenforced (gh#380)");
    }

    [Fact]
    public async Task ConsistencyEnforcement_ShouldRoundTripThroughTheRiskApi()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.35m, enforcement: ConsistencyEnforcement.Block);

        using HttpResponseMessage read = await client.GetAsync($"/accounts/{accountId}/risk");
        RiskProfileResponse? profile = await read.Content.ReadFromJsonAsync<RiskProfileResponse>(_jsonOptions);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        profile.Should().NotBeNull();
        profile!.MaxBestDayFraction.Should().Be(0.35m, "the target must survive the round trip");
        profile.ConsistencyEnforcement.Should().Be(
            ConsistencyEnforcement.Block, "the posture is the operator's choice and must not be silently rewritten");
    }

    // -------------------------------------------------------------------------------------------------------
    // Both postures, same breach
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_ShouldBeRefused_WhenTheTargetIsBreachedAndTheAccountBlocks()
    {
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.40m, enforcement: ConsistencyEnforcement.Block);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 900m), (Utc(7, 22, 15), 100m));

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);
        SendOrderResponse? decision = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a blocked breach refuses the order");
        decision!.ApprovedQuantity.Should().Be(0, "a refusal approves nothing — a partial fill would still trade");
        decision.Reason.Should().Contain("consistency target", "the operator must be told which rule bound");
        (await OrderCountAsync(accountId)).Should().Be(0, "and nothing was journaled, because nothing was sent");
    }

    [Fact]
    public async Task Order_ShouldBeAccepted_WhenTheTargetIsBreachedAndTheAccountOnlyWarns()
    {
        // Same breach, opposite posture. Advisory exists because a consistency breach disqualifies a payout
        // rather than blowing the account — that is the operator's risk to take, not the gate's to refuse.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.40m, enforcement: ConsistencyEnforcement.Advisory);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 900m), (Utc(7, 22, 15), 100m));

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);
        string body = await response.Content.ReadAsStringAsync();
        SendOrderResponse? decision = await response.Content.ReadFromJsonAsync<SendOrderResponse>(_jsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an advisory account trades through a breach");
        decision!.ApprovedQuantity.Should().BeGreaterThan(0, "the order is sized by every other layer as usual");
        decision.BindingLayer.Should().NotBe(
            RiskLayer.ConsistencyTarget, "advisory means it did not bind — it must not masquerade as the binding layer");

        // DEFECT gh#407: the gate BUILDS a GateAdvisory and attaches it to the decision, then nothing carries it
        // out -- SendOrderResponse has no advisory field and GateDecisionRecord persists none. So the posture that
        // is the MIGRATION DEFAULT produces no observable output whatsoever: it does not refuse (by design) and it
        // does not warn (the defect). Pins the OBSERVED behaviour per QA contract rule 2; when #407 lands this
        // flips to asserting the advisory is present and breached, and becomes its regression guard.
        CarriesAdvisory(body).Should().BeFalse(
            "DEFECT gh#407: the advisory is computed and dropped at the API boundary, so an operator on the "
            + "default posture is never told their best day is disqualifying the evaluation");
    }

    [Fact]
    public async Task Advisory_ShouldBeRaisedBeforeItBinds_WhenTheTargetIsSetButNotBreached()
    {
        // The early-warning property (ADR-0007): the consumed fraction is visible while still COMPLIANT, so the
        // operator can steer before the target disqualifies them. A warning that only appears once it is too late
        // to act is not an early warning.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.80m, enforcement: ConsistencyEnforcement.Advisory);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 500m), (Utc(7, 22, 15), 500m)); // 50% of 1000

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "well inside the target, nothing should bind");

        // DEFECT gh#407 (the same drop, one step earlier): below the threshold there is likewise nothing to see.
        CarriesAdvisory(body).Should().BeFalse(
            "DEFECT gh#407: no advisory crosses the API boundary at all, breached or not, so the early-warning "
            + "behaviour ADR-0007 describes does not exist for any account");
    }

    // -------------------------------------------------------------------------------------------------------
    // The migration default — the upgrade-safety property
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExistingProfile_ShouldNotBeginRefusingOrders_AfterTheEnforcementColumnIsAdded()
    {
        // A profile that predates the column carries a target but never declared a posture. The migration defaults
        // to Advisory precisely so a SCHEMA CHANGE cannot start refusing orders on an account whose operator never
        // asked for it. That is a behavioural claim about the migration, so it is asserted rather than commented.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.40m, enforcement: ConsistencyEnforcement.Block);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 900m), (Utc(7, 22, 15), 100m));

        // Reset the column to whatever the MIGRATION installed as its default -- i.e. exactly the value a row
        // written before the column existed would have. Asserting the column's own default, not the DTO's.
        await ExecuteDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"RiskProfiles\" SET \"ConsistencyEnforcement\" = DEFAULT WHERE \"AccountId\" = {0}", accountId));

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the column default must be Advisory — a migration that defaulted to Block would silently start "
            + "refusing orders on every account that already had a target");
    }

    // -------------------------------------------------------------------------------------------------------
    // The day boundary — the one that hides a breach
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConsistencyWindow_ShouldBucketDaysInCentralTime_NotUtc()
    {
        // A CME session runs past UTC midnight. Bucketing on the UTC date splits one trading day in two, which
        // halves its apparent profit and lets an outsized day slip under the target -- a breach reading as
        // compliant. Constructed so the two bucketings DISAGREE on the verdict, which a same-day pair alone
        // cannot show:
        //   Central: Jul-20 = 600 + 400 = 1000, Jul-22 = 500  -> best 1000 / total 1500 = 66.7%  BREACH (> 50%)
        //   UTC:     Jul-20 = 600, Jul-21 = 400, Jul-22 = 500 -> best  600 / total 1500 = 40.0%  no breach
        // Both trades below are the SAME Central day (US Central is UTC-5 in July); the second is past UTC midnight.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.50m, enforcement: ConsistencyEnforcement.Block);
        await SeedClosedTradesAsync(
            accountId,
            (Utc(7, 20, 15), 600m),  // Central Jul-20 10:00
            (Utc(7, 21, 1), 400m),   // Central Jul-20 20:00 -- same session, next UTC date
            (Utc(7, 22, 15), 500m)); // Central Jul-22 10:00

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity,
            "the two evening trades are one Central trading day; bucketing them on the UTC date splits that day "
            + "and reports 40% against a 50% target, so a real breach would trade freely");
    }

    // -------------------------------------------------------------------------------------------------------
    // The degenerate windows
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_ShouldNotBeRefused_WhenTheWindowIsAtALoss()
    {
        // A negative denominator must not read as a breach. An account that is DOWN has no payout to disqualify,
        // and refusing its trades would strand an operator exactly when they need to trade back.
        //
        // The figures are chosen so the guard can actually FAIL. A winning day of +900 against a cumulative -800
        // means the obvious mis-implementation -- normalising the denominator, |−800| -- reads 900/800 = 112% and
        // refuses. With a small best day the fraction stays under the target however it is computed, and the test
        // would pass against a broken implementation as readily as a correct one.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.40m, enforcement: ConsistencyEnforcement.Block);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 900m), (Utc(7, 22, 15), -1_700m)); // cumulative -800

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a losing window has no consistency breach to speak of — the fraction is undefined, not exceeded");
    }

    [Fact]
    public async Task Order_ShouldNotBeRefused_WhenTheAccountHasClosedNoTrades()
    {
        // Absence of evidence is not evidence of a breach. A fresh evaluation must be tradeable on day one.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.40m, enforcement: ConsistencyEnforcement.Block);

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an account with no closed trades has no window to breach");
    }

    [Fact]
    public async Task OpenPositions_ShouldNotCountTowardTheWindow()
    {
        // The rule is measured on REALIZED profit. Counting an open position would make the fraction move with the
        // market -- an operator could be refused, then allowed, then refused again without closing anything.
        // The closed trades sit at 50% against a 60% target; the open one would put the best day at 4400/5400 =
        // 81% if it counted, flipping the verdict.
        HttpClient client = await AuthenticatedClientAsync();
        Guid accountId = await SetupAccountAsync(client, target: 0.60m, enforcement: ConsistencyEnforcement.Block);
        await SeedClosedTradesAsync(accountId, (Utc(7, 20, 15), 500m), (Utc(7, 22, 15), 500m));
        await SeedOpenTradeAsync(accountId, unrealised: 4_000m);

        using HttpResponseMessage response = await SendOrderAsync(client, accountId);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an open position is not realized profit; counting it would make a consistency verdict swing with the market");
    }

    // -------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------

    /// <summary>Whether the payload carries gate advisories at all — the gh#407 pin reads this.</summary>
    /// <remarks>
    /// Asserted against the raw JSON rather than the DTO because <see cref="SendOrderResponse"/> has no advisory
    /// member to bind: there is nothing to deserialize, which IS the defect. When gh#407 adds the field this
    /// starts returning true and the pins fail, which is exactly the signal to flip them.
    /// </remarks>
    private static bool CarriesAdvisory(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(
                property => property.Name.Contains("advisor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An instant in 2026, given as UTC — the tests convert to Central deliberately in their comments.</summary>
    private static DateTimeOffset Utc(int month, int day, int hour) =>
        new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

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

    private async Task<Guid> SetupAccountAsync(HttpClient client, decimal target, ConsistencyEnforcement enforcement)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        conventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);
        Guid accountId = accounts.First(account => account.CanTrade).Id;

        // Generous elsewhere on purpose: every other layer must leave room for one contract, so that when an order
        // IS refused the consistency target is unambiguously the layer that bound. StopForDayAtProfitTarget is off
        // for the same reason -- it would otherwise interact with the seeded profit.
        using HttpResponseMessage risk = await client.PutAsJsonAsync(
            $"/accounts/{accountId}/risk",
            new DeclareRiskProfileRequest(
                DailyLossLimit: 5_000m,
                AccountProfitTarget: 10_000m,
                StartingBalance: 50_000m,
                FloorSource: FloorSource.FirmImposed,
                TrailingMode: TrailingMode.Intraday,
                TrailingAmount: 2_000m,
                LocksAt: 50_100m,
                PerTradeRiskFraction: 0.15m,
                TargetRewardRatio: 1.5m,
                MaxDrawdownPerTrade: 1_000m,
                DailyDrawdownGovernor: 2_000m,
                DailyProfitTarget: null,
                StopForDayAtProfitTarget: false,
                SizingBasis: SizingBasis.SafetyStop,
                MaxContractsPerOrder: 5,
                MaxBestDayFraction: target,
                ConsistencyEnforcement: enforcement));
        risk.EnsureSuccessStatusCode();
        return accountId;
    }

    private Task<HttpResponseMessage> SendOrderAsync(HttpClient client, Guid accountId) =>
        client.PostAsJsonAsync(
            $"/accounts/{accountId}/orders/",
            new SendOrderRequest(
                Symbol: "MES",
                TickSize: 0.25m,
                PointValue: 5m,
                Side: OrderSide.Buy,
                Quantity: 1,
                Entry: 5_000m,
                Stop: 4_990m,
                SafetyStop: 4_980m,
                ReferencePrice: 5_000m,
                Type: OrderType.Market));

    private async Task SeedClosedTradesAsync(Guid accountId, params (DateTimeOffset ClosedAt, decimal RealizedPnL)[] trades)
    {
        Guid userId = await OperatorIdAsync();
        await ExecuteDbAsync(async db =>
        {
            foreach ((DateTimeOffset closedAt, decimal realized) in trades)
            {
                db.Trades.Add(NewTrade(accountId, userId, closedAt, realized));
            }

            await db.SaveChangesAsync();
        });
    }

    /// <summary>An OPEN position: no close instant and no realized result, however large the unrealized figure.</summary>
    private async Task SeedOpenTradeAsync(Guid accountId, decimal unrealised)
    {
        Guid userId = await OperatorIdAsync();
        await ExecuteDbAsync(async db =>
        {
            Trade open = NewTrade(accountId, userId, closedAt: null, realized: unrealised);
            open.ExitPrice = null;
            db.Trades.Add(open);
            await db.SaveChangesAsync();
        });
    }

    private static Trade NewTrade(Guid accountId, Guid userId, DateTimeOffset? closedAt, decimal? realized) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        AccountId = accountId,
        Instrument = "CON.F.US.MES.M26",
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5_000m,
        ExitPrice = 5_010m,
        RealizedPnL = realized,
        Mode = TradingMode.Practice,
        ClosedAt = closedAt,
    };

    private Task<Guid> OperatorIdAsync() => QueryDbAsync(async db =>
        (await db.Users.FirstAsync(user => user.Email == PostgresApiFactory.OperatorEmail)).Id);

    private Task<int> OrderCountAsync(Guid accountId) => QueryDbAsync(db =>
        db.Orders.IgnoreQueryFilters().CountAsync(order => order.AccountId == accountId));

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }
}
