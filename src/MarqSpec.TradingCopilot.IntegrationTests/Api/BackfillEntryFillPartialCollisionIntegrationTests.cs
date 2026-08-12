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
/// Independent QA coverage for the per-leg fill-key collision guard inside <c>BackfillEntryFillsAsync</c>
/// (<c>Api/Orders/OrderEndpoints.cs</c>) — gh#793, spun off the #791 review of gh#770 (R-5 / R-8 / R-9, ADR-0001,
/// ADR-0013). Written from the issue body, not the implementation (QA contract §Role).
/// </summary>
/// <remarks>
/// <para>
/// <b>The race this guards.</b> <c>ReconcileTakingOrderAsync</c> adopts a stranded take by stamping its
/// <c>VenueOrderKey</c> — the instant that makes the row matchable by the live account-event stream. ADR-0001 is
/// at-least-once, so a redelivery of one of the entry's real fill events can land and be journalled by
/// <c>AccountEventIngestionService</c> at any point after that. The reconcile's own backfill then offers <b>both</b>
/// legs to the journal: the one the stream already recorded, and one it never has. The colliding leg must be an
/// idempotent skip — and the other, never a duplicate, must still be journalled. Before the per-leg fix, one
/// batched <c>SaveChangesAsync</c> rolled back <b>every</b> leg on that collision, silently losing a real fill and
/// leaving the journal exactly as incomplete as before gh#770.
/// </para>
/// <para>
/// <b>Why this cannot live in the unit tier (the issue's own finding).</b> Proving it needs the real
/// <c>{ OrderId, VenueFillKey }</c> <b>unique index</b> to actually reject the duplicate — EF InMemory does not
/// enforce unique indexes, so a unit-tier attempt writes the duplicate row happily and passes whether the legs are
/// saved one at a time or in a single batch (confirmed empirically while writing #791: the attempted unit test
/// produced <c>{F-501, F-501, F-502}</c>). <see cref="PostgresApiFactory"/> applies the real migrations, so the
/// index is genuinely live here.
/// </para>
/// <para>
/// <b>The pre-existing row simulates the redelivery, not a live race.</b> The property under test — one leg's
/// unique-index rejection must not cost the other leg — does not depend on the two writes genuinely overlapping in
/// wall-clock time; a row the stream already committed collides identically whether it landed a millisecond or a
/// minute before the backfill runs. There is also no seam to race against here: the venue double's one interleave
/// hook (<see cref="AdversarialTestProjectXVenueFactory.OnFillHistoryRead"/>) runs <i>before</i> the adopt stamps
/// the <c>VenueOrderKey</c> that would make a redelivery matchable, so it cannot express this window. Seeding the
/// row directly is exactly the issue's own "suggested shape": seed one leg's <see cref="Fill"/> row, then drive the
/// adopt with evidence carrying that leg plus another.
/// </para>
/// <para>
/// <b>Round-tripped, not still-open.</b> Both adopt branches call <c>BackfillEntryFillsAsync</c> identically; the
/// flat / round-tripped branch (gh#631) needs no seeded position, which keeps this suite to the one fact it is
/// about. Hosts are suppressed (<see cref="OcoExitTestPostgresFactory"/>, the sanctioned reuse other reconcile
/// suites make of it purely for host suppression) so no background pass touches the fixture mid-test.
/// </para>
/// </remarks>
public class BackfillEntryFillPartialCollisionIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    /// <summary>The venue's own handle on the round-tripped order.</summary>
    private const string FilledVenueKey = "FILLED-AT-VENUE-793";

    /// <summary>The fill key an at-least-once redelivery already journalled before the backfill runs.</summary>
    private const string CollidingKey = "F-501";

    /// <summary>The fill key the stream never recorded — the leg that must not be lost alongside the collision.</summary>
    private const string MissingKey = "F-502";

    private readonly OcoExitTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public BackfillEntryFillPartialCollisionIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Reconcile_ShouldJournalOnlyTheMissingLeg_WhenOneLegWasAlreadyRecordedByAnAtLeastOnceRedelivery()
    {
        VenueFactory.ResetPositions();

        HttpClient client = await AuthenticatedClientAsync();
        (Guid accountId, string venueAccountKey) = await SetupTradeableAccountAsync(client, "Topstep-793-Collision");
        await DeclareRiskProfileAsync(client, accountId);
        Guid orderId = await StrandATakeAsync(client, accountId, "MES");

        // The redelivery that beat the backfill to the index (ADR-0001): a REAL Fill row already sits under this
        // order for ONE of the two legs the venue is about to offer. This is the ONLY thing seeded before the
        // reconcile runs — no position, no suggestion — so the suite stays about the one fact it is testing.
        Guid preExistingFillId = await SeedAlreadyRecordedFillAsync(orderId, CollidingKey, size: 1, price: 5_001.25m);

        // Flat account, tag filled (gh#631's round-tripped branch): the venue offers BOTH legs — the one the stream
        // already recorded, and one it never has.
        VenueFactory.SeedTaggedFill(
            venueAccountKey,
            orderId.ToString(),
            filledSize: 2m,
            venueOrderKey: FilledVenueKey,
            legs:
            [
                new TaggedFillLeg(CollidingKey, Price: 5_001.25m, Size: 1, Fees: 1.24m, ExecutedAt: DateTimeOffset.UtcNow),
                new TaggedFillLeg(MissingKey, Price: 5_002.00m, Size: 1, Fees: 1.24m, ExecutedAt: DateTimeOffset.UtcNow),
            ]);

        using HttpResponseMessage reconcile = await client.PostAsync($"/orders/{orderId}/reconcile", null);

        reconcile.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a flat account with a confirmed fill is resolvable regardless of what the stream already journalled "
            + await DescribeAsync(reconcile));

        // Read from a FRESH scope: what an independent reader sees over Postgres, not the acting scope's tracker.
        List<Fill> journalled = await FillsOfAsync(orderId);

        journalled.Select(fill => fill.VenueFillKey).Should().BeEquivalentTo(
            [CollidingKey, MissingKey],
            "the colliding leg must be an IDEMPOTENT SKIP — exactly one row, never a duplicate — and the leg the "
            + "stream never recorded must STILL be journalled: a single batched save that rolled back on the "
            + "collision would silently lose it, understating the realized P&L the R-5 governor reads from the "
            + "journal (gh#770, #791 review)");

        Fill collided = journalled.Single(fill => fill.VenueFillKey == CollidingKey);
        collided.Id.Should().Be(
            preExistingFillId,
            "the pre-existing row from the redelivery must be left exactly as it was — an idempotent skip never "
            + "deletes and re-inserts, which would be indistinguishable from data loss to anything reading by id");
        collided.Price.Should().Be(5_001.25m, "the pre-existing (stream-written) row must be untouched by the backfill");

        Fill missing = journalled.Single(fill => fill.VenueFillKey == MissingKey);
        missing.OrderId.Should().Be(orderId, "the newly-journalled leg belongs to the row being reconciled");
        missing.Price.Should().Be(5_002.00m, "the missing leg's price comes from the venue's own fill history");
        missing.Size.Should().Be(1);

        // The row itself: the adopt must have gone through regardless of the collision beneath it.
        Order journaledOrder = await OrderAsync(orderId);
        journaledOrder.Status.Should().Be(OrderStatus.Filled, "the adopt is unaffected by a downstream journal collision");
        journaledOrder.VenueOrderKey.Should().Be(FilledVenueKey);
    }

    // =================================================================================================================
    // Helpers.
    // =================================================================================================================

    /// <summary>
    /// Strands a row <see cref="OrderStatus.Taking"/> the way production does: arm a ticket, then fault the venue
    /// seam with a <b>maybe-live</b> fault so the take cannot conclude the order is dead. Asserts the strand, so a
    /// change that stopped stranding would fail here rather than silently making the case above vacuous.
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
            // Tolerated: the property under test is the durable DB state, not whether the fault escaped the request.
        }

        VenueFactory.ClearPlaceOrderFaults();

        Order stranded = await OrderAsync(orderId);
        stranded.Status.Should().Be(
            OrderStatus.Taking,
            "the fixture must actually produce the strand this suite reconciles — otherwise the case above would be "
            + "reconciling a row that was never stranded");
        stranded.VenueOrderKey.Should().BeNull(
            "the strand is precisely the state where the venue's answer was never journaled, which is also why no "
            + "redelivery could have matched this order YET — the pre-existing Fill row below simulates one landing "
            + "AFTER the adopt is about to stamp the key that makes it matchable");

        return orderId;
    }

    /// <summary>
    /// Writes a <see cref="Fill"/> row directly — the durable trace of an at-least-once redelivery
    /// (<c>AccountEventIngestionService</c>) that landed and journalled this leg before the reconcile's own
    /// backfill runs (see the suite's remarks for why this is equivalent to racing the two writes).
    /// </summary>
    /// <returns>The written row's id, so the test can prove the backfill left it untouched.</returns>
    private async Task<Guid> SeedAlreadyRecordedFillAsync(Guid orderId, string venueFillKey, int size, decimal price)
    {
        Guid fillId = Guid.NewGuid();

        await ExecuteDbContextAsync(async database =>
        {
            Order order = await database.Orders.IgnoreQueryFilters().SingleAsync(row => row.Id == orderId);

            database.Fills.Add(new Fill
            {
                Id = fillId,
                UserId = order.UserId,
                OrderId = orderId,
                VenueFillKey = venueFillKey,
                Price = price,
                Size = size,
                Fees = 1.24m,
                ExecutedAt = DateTimeOffset.UtcNow,
            });

            await database.SaveChangesAsync();
        });

        return fillId;
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
            "/firms", new CreateFirmRequest($"{firmName}-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage declareConventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([
                new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false),
                new StageConventionDto(AccountStage.Evaluation, CapitalAtRisk: false),
            ]));
        declareConventions.EnsureSuccessStatusCode();

        using HttpResponseMessage createConnection = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConnection.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync($"/connections/{connection.Id}/accounts/discover", null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        AccountResponse tradeable = accounts.First(account => account.CanTrade);
        return (tradeable.Id, tradeable.VenueAccountKey);
    }

    private async Task DeclareRiskProfileAsync(HttpClient client, Guid accountId)
    {
        DeclareRiskProfileRequest request = new(
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
            MaxContractsPerOrder: 5,
            MaxBestDayFraction: 0.4m);

        using HttpResponseMessage response = await client.PutAsJsonAsync($"/accounts/{accountId}/risk", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));
    }

    private async Task<Guid> ArmAsync(HttpClient client, Guid accountId, SendOrderRequest proposal)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/accounts/{accountId}/orders/arm", proposal);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "the fixture's proposal must arm cleanly " + await DescribeAsync(response));
        StagedOrderResponse? staged = await response.Content.ReadFromJsonAsync<StagedOrderResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(staged);
        return staged.OrderId;
    }

    private static SendOrderRequest ValidProposal(string symbol, int quantity = 2) => new(
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

    private Task<Order> OrderAsync(Guid orderId) => QueryDbContextAsync(database => database.Orders
        .IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == orderId));

    private Task<List<Fill>> FillsOfAsync(Guid orderId) => QueryDbContextAsync(database => database.Fills
        .IgnoreQueryFilters().AsNoTracking().Where(fill => fill.OrderId == orderId)
        .OrderBy(fill => fill.VenueFillKey).ToListAsync());

    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"(response was {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()})";

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    private async Task<T> QueryDbContextAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }
}
