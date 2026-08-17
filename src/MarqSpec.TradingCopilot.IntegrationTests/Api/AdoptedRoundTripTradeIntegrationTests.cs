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
/// <para>
/// <b>One shared discovery, two distinct accounts (a real regression this class found, in two layers).</b>
/// <c>AdversarialTestTradingVenue.GetAccountsAsync</c> returns the SAME fixed, caller-independent roster to
/// every discover call. Every OTHER suite in this repo is blind to that — they route by the internal
/// <c>Account.Id</c> GUID over HTTP — but a live <c>AccountEventStreamHost</c> resolves an inbound event by
/// <c>VenueAccountKey + CredentialKey</c> ALONE (<c>AccountEventIngestionService.ResolveOwnerAsync</c> /
/// <c>TradeJournalService.ResolveAccountAsync</c>), exactly as it would a real broker's genuinely-unique account
/// number — so two DB <c>Account</c> rows sharing one fake key make that resolution ambiguous. <b>Layer one:</b>
/// two cases each picking the roster's first tradeable account both land on <c>PRAC-50K-101</c> directly — one
/// case's closing fill resolves to the OTHER's already-completed order and collides on its
/// <c>{ OrderId, VenueFillKey }</c>, read at first as an inscrutable "already recorded" on what looked like a
/// fresh delivery. <b>Layer two, once each case picked a DIFFERENT index:</b> <c>ConnectionEndpoints.
/// DiscoverAccountsAsync</c> upserts the ENTIRE roster under whichever connection discovers it, so two cases
/// each creating their OWN connection still each end up with all four keys, twice over — an event can now
/// resolve to the OTHER case's untouched duplicate of the SAME key and report "unknown order" for a
/// <c>VenueOrderKey</c> that is very much seeded, just on the row this query didn't land on. The fix is
/// <see cref="SharedTradeableAccountsAsync"/>: ONE discovery for the whole fixture, so each of the four keys
/// exists exactly once, and the two cases pick DIFFERENT indices into that single result.
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

    /// <summary>Memoized across every case in this class — see <see cref="SharedTradeableAccountsAsync"/>.</summary>
    private static Task<IReadOnlyList<(Guid AccountId, string VenueAccountKey)>>? _sharedTradeableAccountsTask;

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
        await using Scenario scenario = await BuildAdoptedRoundTripAsync(tradeableIndex: 0);

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
        await using Scenario scenario = await BuildAdoptedRoundTripAsync(tradeableIndex: 1);

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
    /// <param name="tradeableIndex">Which of the ONE shared discovery's roster entries to use — MUST be distinct
    /// per case in this class fixture (see <see cref="SharedTradeableAccountsAsync"/>'s own remarks for why).</param>
    private async Task<Scenario> BuildAdoptedRoundTripAsync(int tradeableIndex)
    {
        VenueFactory.ResetPositions();

        HttpClient client = await AuthenticatedClientAsync();
        (Guid accountId, string venueAccountKey) = await SetupTradeableAccountAsync(client, tradeableIndex);
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

        // Prove the TWO preconditions AccountEventStreamHost itself checks before it will consume a single
        // event — on the SAME production code paths it uses (VenueCapabilities.Supports,
        // AccountEventIngestionService.DiscoverAccountsAsync) — so a genuine wiring gap here fails LOUDLY and
        // immediately, rather than as an inscrutable timeout whose captured-log tail the poll's own EF Core
        // command-logging noise can drown out.
        VenueFactory.Create(FirmConventions.None).Capabilities.Supports(VenueCapability.AccountStreaming)
            .Should().BeTrue(
                "AccountEventStreamHost refuses to stream AT ALL without this grant — it logs once and goes "
                + "idle for the rest of the process, which otherwise looks identical to a merely-slow CI runner");
        (await DiscoverableAccountsAsync()).Should().Contain(
            candidate => candidate.Key == venueAccountKey,
            "AccountEventStreamHost subscribes only to what DiscoverAccountsAsync returns — an account this "
            + "suite's own HTTP setup created but this query cannot see would leave the host subscribed to "
            + "nothing, and every event armed below would sit in the stream's channel forever unconsumed");

        RunningHost running = await StartHostAsync();

        // Derived from the exit order's own fresh id, never a shared literal: the { OrderId, VenueFillKey }
        // unique index is what makes a redelivery an idempotent no-op, and a fill key reused across scenarios
        // would make THAT property indistinguishable from a genuine wiring defect (gh#960 review).
        string exitVenueFillKey = $"F-EXIT-{exitOrderId:N}";

        DateTimeOffset closedAt = DateTimeOffset.UtcNow;
        running.Stream.Arm(new FillEvent(
            VenueAccountId.Create(_projectx, venueAccountKey),
            closedAt,
            ExitVenueKey,
            exitVenueFillKey,
            OrderSide.Sell,
            Quantity: Size,
            new Price(ExitPrice),
            Fees: 1.24m,
            Voided: false));

        bool exitIngested = await WaitUntilAsync(() => QueryDbAsync(database => database.Orders
            .IgnoreQueryFilters().AsNoTracking().AnyAsync(order => order.Id == exitOrderId && order.Status == OrderStatus.Filled)));
        if (!exitIngested)
        {
            // A prior run collided on the { OrderId, VenueFillKey } unique index on this order's VERY FIRST
            // delivery -- which can only mean either a genuinely duplicate write already sits under this fresh
            // GUID, or the order's status update did not persist despite the Fill row landing. Dump the exact
            // row(s) and the order's live status so the next failure (if any) answers which, directly, instead
            // of another round of inference from a captured log line.
            List<Fill> exitFills = await EntryFillsAsync(exitOrderId);
            Order exitOrderNow = await OrderAsync(exitOrderId);
            exitIngested.Should().BeTrue(
                "the closing FillEvent must be ingested through the real AccountEventIngestionService before the "
                + "flat is delivered — this scenario is about composing, not about the gh#748 defer-then-retry "
                + $"window (that is FlatBeforeFillAdverseOrderIntegrationTests' own subject). Exit order is now "
                + $"{exitOrderNow.Status} with {exitFills.Count} Fill row(s) under it: ["
                + string.Join(", ", exitFills.Select(fill => $"{{Id={fill.Id}, VenueFillKey={fill.VenueFillKey}}}"))
                + "]. " + DescribeHostLogs());
        }

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
            + "no opening leg to pair, and the window never reconciles to flat. " + DescribeHostLogs());

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
    /// Calls <see cref="AccountEventIngestionService.DiscoverAccountsAsync"/> directly — the exact query
    /// <c>AccountEventStreamHost</c> runs to decide what to subscribe to — so this suite can prove an account is
    /// discoverable BEFORE relying on a live host to have done so silently.
    /// </summary>
    private async Task<IReadOnlyList<VenueAccountId>> DiscoverableAccountsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<AccountEventIngestionService>()
            .DiscoverAccountsAsync(CancellationToken.None);
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

    /// <summary>
    /// The ONE firm → conventions → connection → discover flow this whole class fixture ever runs, memoized
    /// across every case (xUnit constructs a fresh test-class instance per <c>[Fact]</c>, so this has to be
    /// <c>static</c> to actually be shared — the point of it).
    /// </summary>
    /// <remarks>
    /// <b>Why one discovery, not one per case (a real regression this class found).</b>
    /// <c>ConnectionEndpoints.DiscoverAccountsAsync</c> upserts EVERY entry of
    /// <c>AdversarialTestTradingVenue.GetAccountsAsync</c>'s fixed roster under whichever connection calls it —
    /// so a SECOND connection discovering the SAME fixed roster does not avoid the first connection's accounts,
    /// it DUPLICATES all of them again under itself. Two cases each creating their own connection therefore
    /// leaves every one of the four fake <c>VenueAccountKey</c>s sitting on TWO different DB <c>Account</c> rows
    /// — regardless of which index each case later picks for its own use — and
    /// <c>AccountEventIngestionService.ResolveOwnerAsync</c> resolves an inbound event by
    /// <c>VenueAccountKey + CredentialKey</c> ALONE, so it cannot tell those two rows apart: an event for one
    /// case's account can resolve to the OTHER case's untouched duplicate of the SAME key, which then reports
    /// "unknown order" for a `VenueOrderKey` that is very much seeded — just under the row this query didn't
    /// land on. A single shared connection makes each of the four keys exist EXACTLY once in the whole fixture,
    /// which is what index-based picking actually needed to be race-proof.
    /// </remarks>
    private Task<IReadOnlyList<(Guid AccountId, string VenueAccountKey)>> SharedTradeableAccountsAsync(
        HttpClient client) =>
        _sharedTradeableAccountsTask ??= DiscoverTradeableAccountsAsync(client);

    private async Task<IReadOnlyList<(Guid AccountId, string VenueAccountKey)>> DiscoverTradeableAccountsAsync(
        HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-960-{Guid.NewGuid():N}", FirmType.PropFirm));
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

        // Ordered by VenueAccountKey (not the response's own, unspecified order) so "index 0 vs 1" is a
        // deterministic, stable pick across runs, never an accident of the venue stub's list literal order.
        return [.. accounts
            .Where(account => account.CanTrade)
            .OrderBy(account => account.VenueAccountKey, StringComparer.Ordinal)
            .Select(account => (account.Id, account.VenueAccountKey))];
    }

    /// <param name="tradeableIndex">
    /// Which of the ONE shared discovery's tradeable accounts to use — MUST be distinct per case in this class
    /// (see <see cref="SharedTradeableAccountsAsync"/>'s remarks for why sharing the discovery, not just
    /// picking different indices, is what actually makes that safe).
    /// </param>
    private async Task<(Guid AccountId, string VenueAccountKey)> SetupTradeableAccountAsync(
        HttpClient client, int tradeableIndex)
    {
        IReadOnlyList<(Guid AccountId, string VenueAccountKey)> accounts = await SharedTradeableAccountsAsync(client);
        return accounts[tradeableIndex];
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

    /// <summary>
    /// The live host's own captured application-level log lines, so a timed-out wait is diagnosable from the CI
    /// output alone rather than guessed at — in particular it surfaces <c>AccountEventStreamHost</c>'s silent
    /// "the account-event host is idle" or a dropped/re-subscribed stream. Excludes
    /// <c>Microsoft.EntityFrameworkCore</c>'s own command-execution logging: this suite's OWN repeated
    /// <c>WaitUntilAsync</c> polling emits one such line per attempt, and a plain <c>TakeLast</c> over the whole
    /// captured stream lets that self-inflicted noise silently push out the one line that would have answered
    /// the question (gh#960 review).
    /// </summary>
    private string DescribeHostLogs() =>
        "Captured application log tail (EF Core command logging excluded): [" + string.Join(
            " | ",
            _factory.Logs.Records
                .Where(record => !record.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                .TakeLast(20)
                .Select(record => $"{record.Category}/{record.Level}: {record.Message}")) + "]";

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
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int attempts = 300, int delayMs = 50)
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
