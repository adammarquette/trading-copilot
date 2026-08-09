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
/// Independent QA coverage for <c>POST /orders/{id}/reconcile</c> — gh#736, the fifth case of gh#724 (of gh#619;
/// R-11 / R-12 / R-16, ADR-0007, ADR-0013). Written from the issue, not the implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this path is for.</b> A take that faults at the venue seam leaves its row <see cref="OrderStatus.Taking"/>
/// — deliberately, because a maybe-live order must never be released (gh#530: releasing one whose order <i>is</i>
/// resting lets the next take stack a second live order on the account). The reconcile is the operator's way to
/// resolve that strand against <b>venue truth</b>: adopt the row if its order really is resting, release it if
/// nothing rests.
/// </para>
/// <para>
/// <b>Why the container tier.</b> The decision is made from a real <c>ReconcileAsync</c> round trip and committed
/// through the applied migrations, and the release arm is gated on the account being <b>provably flat</b> — none
/// of which the unit tier's in-memory provider exercises. The strand itself is produced the way production
/// produces it: a genuine maybe-live fault at the seam, not a hand-written <c>Taking</c> row.
/// </para>
/// <para>
/// <b>The two arms fail in opposite directions, which is why both are here.</b> Adopting when nothing rests
/// invents an order that does not exist; releasing when something does rest re-opens gh#530. A suite covering
/// only one arm would pass against an implementation that always did that one thing.
/// </para>
/// </remarks>
public class ReconcileStrandedTakeIntegrationTests : IClassFixture<StubbedVenuePostgresFactory>
{
    /// <summary>The venue handle a resting order carries — distinct from anything the take path mints itself.</summary>
    private const string RestingVenueKey = "RESTING-AT-VENUE-736";

    private readonly StubbedVenuePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public ReconcileStrandedTakeIntegrationTests(StubbedVenuePostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Reconcile_ShouldAdoptTheRowAsWorking_WhenTheTakesOrderIsRestingAtTheVenue()
    {
        // gh#736 / gh#724 case 5, adopt arm. The take faulted maybe-live but the order DID land, and it carries
        // this row's id as its customTag — the only thing tying a venue order back to the row that placed it
        // (gh#589). Adoption is the terminal Taking -> Working transition.
        HttpClient client = await AuthenticatedClientAsync();
        (Guid accountId, string venueAccountKey) = await SetupTradeableAccountAsync(client, "Topstep-ReconcileAdopt");
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await StrandATakeAsync(client, accountId, "MES");

        // Venue truth: the order really is resting, tagged with the row that placed it.
        VenueFactory.SeedWorkingOrder(
            venueAccountKey, RestingVenueKey, "CON.F.US.MES.U26", customTag: orderId.ToString());

        using HttpResponseMessage reconcile = await client.PostAsync($"/orders/{orderId}/reconcile", null);

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.OK, "a stranded take whose order is resting is resolvable — that is what this path is for");

        // Read from a FRESH scope: what an independent reader sees over Postgres, not the acting scope's tracker.
        await ExecuteDbContextAsync(async database =>
        {
            Order order = await database.Orders.IgnoreQueryFilters().SingleAsync(row => row.Id == orderId);

            order.Status.Should().Be(
                OrderStatus.Working,
                "the row is adopted onto the order that actually rests — leaving it Taking would keep the account "
                + "blocked against an order the venue is already carrying");
            order.VenueOrderKey.Should().Be(
                RestingVenueKey,
                "the adopted row carries the VENUE's handle, so cancel / flatten and the orphan guard can act on it "
                + "— an adoption that recorded no handle would be unmanageable");
        });
    }

    [Fact]
    public async Task Reconcile_ShouldReleaseToStaged_AndTheTicketShouldBeGenuinelyRetakeable_WhenNothingRests()
    {
        // gh#736 / gh#724 case 5, release arm. The take faulted BEFORE the venue took anything: nothing rests under
        // the tag and the account is flat, so the claim goes back and the operator can amend and retry. "Genuinely
        // re-takeable" is proven BY USE below — a row merely relabelled Staged would pass a status assertion and
        // still be unusable.
        HttpClient client = await AuthenticatedClientAsync();
        (Guid accountId, _) = await SetupTradeableAccountAsync(client, "Topstep-ReconcileRelease");
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await StrandATakeAsync(client, accountId, "MNQ");

        // No working order seeded and no position: nothing rests under the tag, and the account is provably flat —
        // the two conditions the release arm requires before it will hand a claim back (gh#589 round-2 review).
        using HttpResponseMessage reconcile = await client.PostAsync($"/orders/{orderId}/reconcile", null);

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.OK, "nothing rests and the account is flat, so the claim is safe to hand back");

        await ExecuteDbContextAsync(async database =>
        {
            Order order = await database.Orders.IgnoreQueryFilters().SingleAsync(row => row.Id == orderId);

            order.Status.Should().Be(OrderStatus.Staged, "a take that placed nothing releases its claim");
            order.VenueOrderKey.Should().BeNull(
                "nothing rests, so no venue handle may be recorded — a handle here would be an order that does not exist");
        });

        // Proven by USE, not by status: the released ticket takes again and reaches the venue.
        VenueFactory.ClearPlaceOrderFaults();
        using HttpResponseMessage retake = await client.PostAsync($"/orders/{orderId}/take", null);

        retake.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the released ticket is genuinely re-takeable — a row relabelled Staged but still claimed would refuse "
            + "here, and the operator would be stuck with no way to resend");
    }

    // =================================================================================================================
    // Helpers.
    // =================================================================================================================

    /// <summary>
    /// Strands a row <see cref="OrderStatus.Taking"/> the way production does: arm a ticket, then fault the venue
    /// seam with a <b>maybe-live</b> fault so the take cannot conclude the order is dead. Asserts the strand, so a
    /// change that stopped stranding would fail here rather than silently making both cases vacuous.
    /// </summary>
    private async Task<Guid> StrandATakeAsync(HttpClient client, Guid accountId, string symbol)
    {
        Guid orderId = await ArmAsync(client, accountId, ValidProposal(symbol));

        VenueFactory.MakePlaceOrderThrow(() => new VenueRefusalException(
            "accepted but returned no order id", VenueRefusalKind.Indeterminate));

        try
        {
            using HttpResponseMessage take = await client.PostAsync($"/orders/{orderId}/take", null);
            take.StatusCode.Should().NotBe(
                HttpStatusCode.OK, "a maybe-live fault must never report the take as succeeded");
        }
        catch (Exception)
        {
            // Tolerated: the property under test is the DB state, not whether the fault escaped the request.
        }

        await ExecuteDbContextAsync(async database =>
        {
            Order order = await database.Orders.IgnoreQueryFilters().SingleAsync(row => row.Id == orderId);
            order.Status.Should().Be(
                OrderStatus.Taking,
                "the fixture must actually produce the strand this suite reconciles — otherwise both cases below "
                + "would be reconciling a row that was never stranded");
        });

        return orderId;
    }

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

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

    /// <summary>Firm → conventions (no capital at risk ⇒ Practice) → connection → discovered tradeable account.</summary>
    private async Task<(Guid AccountId, string VenueAccountKey)> SetupTradeableAccountAsync(
        HttpClient client, string firmName)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest(firmName, FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConv = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConv.EnsureSuccessStatusCode();

        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse tradeable = accounts.First(account => account.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId, decimal maxDrawdownPerTrade = 300m)
    {
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
            MaxDrawdownPerTrade: maxDrawdownPerTrade,
            DailyDrawdownGovernor: 600m,
            DailyProfitTarget: 1_500m,
            StopForDayAtProfitTarget: true,
            SizingBasis: SizingBasis.SafetyStop,
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", declareReq);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId, SendOrderRequest proposal)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's proposal must arm cleanly");
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    private static SendOrderRequest ValidProposal(string symbol, int quantity = 1) => new(
        Symbol: symbol,
        TickSize: 0.25m,
        PointValue: 5m,
        Side: OrderSide.Buy,
        Quantity: quantity,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market);

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }
}
