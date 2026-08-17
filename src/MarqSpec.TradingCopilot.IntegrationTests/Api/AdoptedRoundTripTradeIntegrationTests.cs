using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Api.Risk;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Independent end-to-end QA coverage for gh#960: the full <c>#770</c> chain — strand a Filled take with no venue
/// key and its entry fills dropped, adopt it through the <b>still-open</b> arm of <c>POST /orders/{id}/reconcile</c>
/// (gh#723), drive the position flat through the real account-event pipeline, and prove exactly one realized-P&amp;L
/// <see cref="Trade"/> composes — plus that a redelivered entry fill is an idempotent no-op. Written from gh#960's
/// own text and gh#770's, not from either implementation (QA contract §Role).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the pieces alone are not the whole story.</b>
/// <see cref="BackfillEntryFillPartialCollisionIntegrationTests"/> proves the collision guard inside
/// <c>BackfillEntryFillsAsync</c> by asserting the <b>fills</b> it journals (gh#793).
/// <c>TradeJournalServiceTests</c> (unit) and <see cref="FlatBeforeFillAdverseOrderIntegrationTests"/> prove
/// <c>ProcessFlatAsync</c> composes a round-trip <see cref="Trade"/> — but from fills seeded directly or delivered
/// through a <b>different</b> adverse-order case (gh#911), never from an adopt's own backfilled entry leg. No
/// suite before this one drove the adopt-strand → backfill → flat → <c>Trade</c> chain through in one run and
/// asserted the composed row's signed <see cref="Trade.RealizedPnL"/> — which is exactly what the R-5 gate reads.
/// </para>
/// <para>
/// <b>Adopt-still-open (gh#723), not round-tripped (gh#631).</b> The strand is adopted while the position is
/// still <b>open</b> at the venue — <see cref="ReconcileAdoptStillOpenIntegrationTests"/>'s own subject — so
/// <c>BackfillEntryFillsAsync</c> journals only the entry leg the strand dropped; the position rides the venue's
/// native bracket (no synthetic <c>StopPlan</c> is written, so there is nothing for this suite to seed there
/// either). The round trip only closes once a <b>separate, later</b> flat arrives through the real stream — the
/// one thing none of the pieces above exercises together.
/// </para>
/// <para>
/// <b>The closing leg is a seeded Order, not a hand-written Fill (judgment call).</b> Whatever mechanism produces
/// the close in production — a promoted synthetic stop, an operator flatten, the venue's own native bracket
/// resolving through an order this process happens to hold — <c>TradeJournalService.ProcessFlatAsync</c> only
/// ever reads <em>our own journaled orders and their fills</em> for the account + contract (never fills routed
/// some other way), so a Working order with no fill yet, closed through a REAL <see cref="FillEvent"/> delivered
/// by the stream, is the shape that exercises the composer honestly — the same shape
/// <see cref="FlatBeforeFillAdverseOrderIntegrationTests"/> uses for its own closing leg. Hand-writing the closing
/// <see cref="Fill"/> row directly would skip the very delivery path (<c>AccountEventIngestionService</c>) the
/// entry-fill replay below depends on.
/// </para>
/// <para>
/// <b>The idempotent-replay assertion, precisely.</b> The entry <see cref="Fill"/> that
/// <c>BackfillEntryFillsAsync</c> wrote at adopt time carries the <b>venue's own</b> fill key (never a synthesized
/// one — the whole point of gh#770's design), so it is indistinguishable from one the live stream would have
/// written. Redelivering the identical <see cref="FillEvent"/> through <c>AccountEventIngestionService</c> must
/// therefore collide on the real <c>{ OrderId, VenueFillKey }</c> unique index and be swallowed as an idempotent
/// skip — no second row, no second <see cref="Trade"/>. The replay leaves no other durable trace (the order is
/// already <see cref="OrderStatus.Filled"/>, so the ingestion path's status-advance branch does not even run), so
/// the suite's only signal that the event was actually <b>processed</b> — not merely enqueued — is the ingestion
/// service's own log line, captured via <see cref="AdoptedRoundTripTradePostgresFactory.Logs"/>.
/// </para>
/// <para>
/// <b>Hosts are suppressed except the one this suite starts itself</b>
/// (<see cref="AdoptedRoundTripTradePostgresFactory"/>, the same discipline
/// <see cref="FlatBeforeFillAdverseOrderPostgresFactory"/> and <see cref="TradeJournalWriteFaultPostgresFactory"/>
/// use), so no background pass touches the seeded surface outside the window each case controls.
/// </para>
/// </remarks>
public sealed class AdoptedRoundTripTradeIntegrationTests : IClassFixture<AdoptedRoundTripTradePostgresFactory>
{
    /// <summary>The contract the adversarial venue resolves the "MES" symbol to (matches gh#769's own constant).</summary>
    private const string ContractKey = "MESM25";

    /// <summary>The venue's own handle on the adopted entry order.</summary>
    private const string EntryVenueKey = "ENTRY-AT-VENUE-960";

    /// <summary>The venue's own fill key behind the adopted entry — never synthesized (gh#770's own rule).</summary>
    private const string EntryVenueFillKey = "F-ENTRY-960";

    /// <summary>The venue's own handle on the order whose fill closes the position.</summary>
    private const string ExitVenueKey = "EXIT-AT-VENUE-960";

    /// <summary>What the take armed and the venue confirmed filled.</summary>
    private const int Size = 1;

    private const decimal EntryPrice = 5_001.25m;
    private const decimal ExitPrice = 5_011.25m;
    private const decimal PointValue = 5m;

    /// <summary>The signed realized P&amp;L a 1-lot long entered at <see cref="EntryPrice"/> and closed at
    /// <see cref="ExitPrice"/> must journal: (5,011.25 - 5,001.25) * 1 * 1 * 5 — a 10-point win.</summary>
    private const decimal ExpectedRealizedPnL = 50m;

    private static readonly VenueId _projectx = VenueId.Parse("projectx");

    private readonly AdoptedRoundTripTradePostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    public AdoptedRoundTripTradeIntegrationTests(AdoptedRoundTripTradePostgresFactory factory)
    {
        _factory = factory;
    }

    // =================================================================================================================
    // 1. The chain composes: adopt-still-open's backfilled entry leg + the later flat's closing leg = one Trade.
    // =================================================================================================================

    [Fact]
    public async Task AccountEventStreamHost_ShouldComposeExactlyOneRealizedTrade_WhenAnAdoptedStillOpenTakeIsLaterDrivenFlat()
    {
        await using Scenario scenario = await BuildAdoptedRoundTripAsync("Topstep-960-Compose");

        List<Trade> trades = await TradesAsync(scenario.AccountId);
        trades.Should().ContainSingle(
            "the adopt backfilled the entry leg and the later flat supplied the closing leg — exactly ONE round "
            + "trip closed here, and a second row would double-count into the R-4 / R-5 daily governor");

        Trade trade = trades[0];
        trade.RealizedPnL.Should().Be(
            ExpectedRealizedPnL,
            $"buy @{EntryPrice}, sell @{ExitPrice}, {Size} lot at a point value of {PointValue} — a signed "
            + "realized P&L computed from the ADOPT's backfilled entry leg and the stream's own closing leg");
        trade.OpeningFillId.Should().Be(
            scenario.EntryFillId,
            "the opening leg is the very Fill row BackfillEntryFillsAsync wrote at adopt time (gh#770) — not one "
            + "hand-seeded for this suite");
        trade.ClosingFillId.Should().NotBeNull("keyed on the closing fill the stream ingested after the flat");
        trade.AccountId.Should().Be(scenario.AccountId);
        trade.Side.Should().Be(OrderSide.Buy, "the adopted entry was the armed long");

        // The outcome is genuinely `journalled`, not merely "a row exists" — the same discriminator
        // FlatBeforeFillAdverseOrderIntegrationTests asserts on for its own compose case.
        _factory.Capture.For(ExecutionMetrics.JournalOutcomes).Should().Contain(
            measurement => (string?)measurement.Tags.GetValueOrDefault("outcome") == ExecutionMetrics.JournalWritten,
            "the compose must report through the SAME outcome discriminator the R-4/R-5 governors' alerting reads, "
            + "not merely leave a row behind");
    }

    // =================================================================================================================
    // 2. The replay: the SAME entry fill, redelivered through the real ingestion path, is an idempotent no-op.
    // =================================================================================================================

    [Fact]
    public async Task AccountEventStreamHost_ShouldTreatARedeliveredEntryFill_AsAnIdempotentNoOp()
    {
        await using Scenario scenario = await BuildAdoptedRoundTripAsync("Topstep-960-Replay");

        List<Fill> beforeReplay = await EntryFillsAsync(scenario.EntryOrderId);
        beforeReplay.Should().ContainSingle();
        Guid survivingFillId = beforeReplay[0].Id;

        _factory.Logs.Clear();

        // The IDENTICAL event a redelivery of the venue's own trade record would carry — same venue order key
        // (stamped by the adopt), same venue fill key (the one BackfillEntryFillsAsync wrote), same side and size.
        // Only the wall-clock delivery instant differs, exactly as an at-least-once redelivery would look.
        scenario.Running.Stream.Arm(new FillEvent(
            VenueAccountId.Create(_projectx, scenario.VenueAccountKey),
            DateTimeOffset.UtcNow,
            EntryVenueKey,
            EntryVenueFillKey,
            OrderSide.Buy,
            Quantity: Size,
            new Price(EntryPrice),
            Fees: 0m,
            Voided: false));

        bool skippedAsIdempotent = await WaitUntilAsync(() => Task.FromResult(_factory.Logs.Entries.Any(
            entry => entry.Message.Contains(EntryVenueFillKey, StringComparison.Ordinal)
                && entry.Message.Contains("idempotent skip", StringComparison.Ordinal))));
        skippedAsIdempotent.Should().BeTrue(
            "the replay must actually reach AccountEventIngestionService.ProcessFillAsync and be recognized as "
            + "the idempotent skip its own { OrderId, VenueFillKey } unique index exists to produce — not merely "
            + "sit unprocessed in the stream's channel");

        List<Fill> afterReplay = await EntryFillsAsync(scenario.EntryOrderId);
        afterReplay.Should().ContainSingle(
            "the venue's own fill key is what the backfilled row already carries (gh#770's design), so a "
            + "redelivery collides with the SAME row rather than minting a second one");
        afterReplay[0].Id.Should().Be(
            survivingFillId, "an idempotent skip never deletes and re-inserts — the surviving row is the original");

        (await TradesAsync(scenario.AccountId)).Should().ContainSingle(
            "the replay must not disturb the already-composed round trip — a second Trade here would double-count "
            + "the same execution into the daily governor");
    }

    // =================================================================================================================
    // Scenario builder — strand, adopt-still-open, drive flat through the real stream, wait for the Trade.
    // =================================================================================================================

    /// <summary>Everything a case needs about the composed scenario, plus the still-running host so a case may
    /// continue driving events against it (the replay case does).</summary>
    private sealed class Scenario(
        RunningHost running,
        Guid accountId,
        string venueAccountKey,
        Guid entryOrderId,
        Guid entryFillId) : IAsyncDisposable
    {
        public RunningHost Running { get; } = running;

        public Guid AccountId { get; } = accountId;

        public string VenueAccountKey { get; } = venueAccountKey;

        public Guid EntryOrderId { get; } = entryOrderId;

        public Guid EntryFillId { get; } = entryFillId;

        public ValueTask DisposeAsync() => Running.DisposeAsync();
    }

    /// <summary>
    /// Strands a Filled take with no venue key and its entry fills dropped, adopts it through the still-open arm
    /// (gh#723), starts the real account-event host, delivers the closing fill and the flat through it, and waits
    /// for exactly the Trade this scenario's fills describe to compose. Left RUNNING on return — the replay case
    /// keeps driving events against it; the compose-only case disposes it immediately via <c>await using</c>.
    /// </summary>
    private async Task<Scenario> BuildAdoptedRoundTripAsync(string firmName)
    {
        VenueFactory.ResetPositions();

        HttpClient client = await AuthenticatedClientAsync();
        (Guid accountId, string venueAccountKey) = await SetupTradeableAccountAsync(client, firmName);
        await DeclareRiskProfileAsync(client, accountId);

        Guid entryOrderId = await StrandATakeAsync(client, accountId);

        // gh#723: nothing rests under the tag, but the position is STILL OPEN — the arm that backfills only the
        // entry leg and writes no synthetic stop (the venue's own native bracket protects the open position).
        VenueFactory.SeedPosition(venueAccountKey, ContractKey, netQuantity: Size);
        VenueFactory.SeedTaggedFill(
            venueAccountKey,
            entryOrderId.ToString(),
            filledSize: Size,
            filledPrice: EntryPrice,
            venueOrderKey: EntryVenueKey,
            legs:
            [
                new TaggedFillLeg(EntryVenueFillKey, EntryPrice, Size, Fees: 1.24m, ExecutedAt: DateTimeOffset.UtcNow.AddMinutes(-10)),
            ]);

        using HttpResponseMessage reconcile = await client.PostAsync($"/orders/{entryOrderId}/reconcile", null);
        reconcile.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a strand the venue positively reports as FILLED with an open position behind it is resolvable via "
            + "gh#723's adopt arm " + await DescribeAsync(reconcile));

        Order adopted = await OrderAsync(entryOrderId);
        adopted.Status.Should().Be(OrderStatus.Filled, "the adopt must actually have happened, or this scenario proves nothing");
        adopted.VenueOrderKey.Should().Be(EntryVenueKey);

        List<Fill> entryFills = await EntryFillsAsync(entryOrderId);
        entryFills.Should().ContainSingle(
            "BackfillEntryFillsAsync must have journaled the ONE entry leg the strand dropped (gh#770) — the fixture "
            + "proves nothing about the #770 chain if the backfill itself did not run");
        entryFills[0].VenueFillKey.Should().Be(EntryVenueFillKey);

        // The closing leg: an order this process journals (Working, no fill yet) — the shape a promoted stop, an
        // operator flatten, or a native-bracket-resolved order all share from ProcessFlatAsync's point of view (see
        // the class remarks for why a seeded Order, closed through a REAL FillEvent, is the honest shape here).
        Guid exitOrderId = await SeedWorkingExitOrderAsync(adopted.UserId, accountId);

        VenueFactory.MakeAccountStreamingSupported();
        RunningHost running = await StartHostAsync();

        DateTimeOffset closedAt = DateTimeOffset.UtcNow;
        running.Stream.Arm(new FillEvent(
            VenueAccountId.Create(_projectx, venueAccountKey),
            closedAt,
            ExitVenueKey,
            "F-EXIT-960",
            OrderSide.Sell,
            Quantity: Size,
            new Price(ExitPrice),
            Fees: 1.24m,
            Voided: false));

        bool exitIngested = await WaitUntilAsync(() => QueryDbAsync(database => database.Orders
            .IgnoreQueryFilters().AsNoTracking().AnyAsync(order => order.Id == exitOrderId && order.Status == OrderStatus.Filled)));
        exitIngested.Should().BeTrue(
            "the closing FillEvent must be ingested through the real AccountEventIngestionService before the flat "
            + "is delivered — this scenario is about composing, not about the gh#748 defer-then-retry window "
            + "(that is FlatBeforeFillAdverseOrderIntegrationTests' own subject)");

        running.Stream.Arm(new PositionEvent(
            VenueAccountId.Create(_projectx, venueAccountKey),
            closedAt,
            VenueContractId.Create(_projectx, ContractKey),
            NetQuantity: 0,
            new Price(ExitPrice)));

        bool composed = await WaitUntilAsync(() => QueryDbAsync(database =>
            database.Trades.IgnoreQueryFilters().AnyAsync(trade => trade.AccountId == accountId)));
        composed.Should().BeTrue(
            "the flat must trigger TradeJournalService.ProcessFlatAsync and compose the round trip from the "
            + "adopt's backfilled entry leg plus the stream's own closing leg — without gh#770's backfill there is "
            + "no opening leg to pair, and the window never reconciles to flat");

        return new Scenario(running, accountId, venueAccountKey, entryOrderId, entryFills[0].Id);
    }

    /// <summary>
    /// Strands a take the way production does: arm a ticket, then fault the venue seam with a maybe-live fault so
    /// the take cannot conclude the order is dead. Asserts the strand, so a change that stopped stranding would
    /// fail here rather than silently making the scenario above vacuous.
    /// </summary>
    private async Task<Guid> StrandATakeAsync(HttpClient client, Guid accountId)
    {
        Guid orderId = await ArmAsync(client, accountId, Proposal("MES"));

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
            "the fixture must actually produce the strand this suite reconciles — otherwise the scenario would be "
            + "reconciling a row that was never stranded");
        stranded.VenueOrderKey.Should().BeNull(
            "the strand is precisely the state where the venue's answer was never journaled, and with no key the "
            + "real fill events for this entry were dropped by AccountEventIngestionService while it was stranded "
            + "(gh#770) — which is exactly what BackfillEntryFillsAsync exists to repair at adopt time");

        return orderId;
    }

    /// <summary>Seeds the closing order directly — Working, no fill yet, on the same instrument the adopted entry
    /// resolved to — so the real closing FillEvent has an order of ours to attribute to.</summary>
    private async Task<Guid> SeedWorkingExitOrderAsync(Guid userId, Guid accountId)
    {
        Guid exitOrderId = Guid.NewGuid();

        await ExecuteDbContextAsync(async database =>
        {
            database.Orders.Add(new Order
            {
                Id = exitOrderId,
                UserId = userId,
                AccountId = accountId,
                Instrument = ContractKey,
                Side = OrderSide.Sell,
                Size = Size,
                Type = OrderType.Market,
                EntryPrice = ExitPrice,
                PointValue = PointValue,
                TickSize = 0.25m,
                Status = OrderStatus.Working,
                Mode = TradingMode.Practice,
                VenueOrderKey = ExitVenueKey,
                PlacedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync();
        });

        return exitOrderId;
    }

    /// <summary>
    /// Starts a fresh, isolated <see cref="AccountEventStreamHost"/> against this fixture's stream double —
    /// resetting the channel and clearing captured measurements/logs first, so one test's events can never leak
    /// into another's on this shared-container fixture.
    /// </summary>
    private async Task<RunningHost> StartHostAsync()
    {
        TestAccountEventStream stream = _factory.Services.GetRequiredService<TestAccountEventStream>();
        stream.Reset();
        _factory.Capture.Clear();
        _factory.Logs.Clear();

        AccountEventStreamHost host = new(
            _factory.Services,
            _factory.Services.GetRequiredService<PendingFlatJournal>(),
            _factory.Services.GetRequiredService<ILogger<AccountEventStreamHost>>());
        await host.StartAsync(CancellationToken.None);
        return new RunningHost(host, stream);
    }

    /// <summary>A started host plus its stream double, torn down on dispose — the same shape
    /// <see cref="FlatBeforeFillAdverseOrderIntegrationTests"/> uses.</summary>
    private sealed class RunningHost(AccountEventStreamHost host, TestAccountEventStream stream) : IAsyncDisposable
    {
        public TestAccountEventStream Stream { get; } = stream;

        private readonly AccountEventStreamHost _host = host;
        private bool _stopped;

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            await _host.StopAsync(CancellationToken.None);
            _stopped = true;
        }

        public ValueTask DisposeAsync() => new(StopAsync());
    }

    // =================================================================================================================
    // Helpers.
    // =================================================================================================================

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

    private static SendOrderRequest Proposal(string symbol) => new(
        Symbol: symbol,
        TickSize: 0.25m,
        PointValue: PointValue,
        Side: OrderSide.Buy,
        Quantity: Size,
        Entry: 5_000m,
        Stop: 4_990m,
        SafetyStop: 4_980m,
        ReferencePrice: 5_000m,
        Type: OrderType.Market);

    private Task<Order> OrderAsync(Guid orderId) => QueryDbAsync(database => database.Orders
        .IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == orderId));

    private Task<List<Fill>> EntryFillsAsync(Guid orderId) => QueryDbAsync(database => database.Fills
        .IgnoreQueryFilters().AsNoTracking().Where(fill => fill.OrderId == orderId).ToListAsync());

    /// <summary>Scoped to one account: each scenario runs on its own account under this shared-container fixture.</summary>
    private Task<List<Trade>> TradesAsync(Guid accountId) => QueryDbAsync(database => database.Trades
        .IgnoreQueryFilters().Where(trade => trade.AccountId == accountId).OrderBy(trade => trade.ClosedAt).ToListAsync());

    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        $"(response was {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()})";

    private async Task ExecuteDbContextAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>());
    }

    /// <summary>Polls until <paramref name="condition"/> holds, returning <see langword="false"/> on timeout rather
    /// than throwing — the shape the sibling stream-driven suites use, so a genuine red reports as an assertion
    /// failure instead of an unhandled exception.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int attempts = 200, int delayMs = 50)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }
}
