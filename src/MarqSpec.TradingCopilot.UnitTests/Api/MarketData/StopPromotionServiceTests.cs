using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The stop-promotion watcher's core (gh#153, ADR-0007): the event log's first consumer. On each quote it
/// promotes any <b>hidden</b> stop whose price has come within its band — transmitting the actual stop as a
/// native working order and flipping its staging. The safety-relevant behaviours: promote only when due, use
/// the side-appropriate price, transmit the protective stop before recording it native, and never re-promote.
/// </summary>
public class StopPromotionServiceTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private static VenueId Venue => VenueId.Parse("projectx");
    private static VenueContractId Contract => VenueContractId.Create(Venue, "CON.F.US.MES.U26");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public StopPromotionServiceTests()
    {
        A.CallTo(() => _venue.Id).Returns(Venue);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily((OrderRequest r, CancellationToken _) => Task.FromResult(
                new PlacedOrder(r.Account, "STOP-1", DateTimeOffset.UnixEpoch)));
    }

    // No ICurrentUser: the watcher is background plumbing, so the context has no authenticated user. Its reads
    // therefore rely on IgnoreQueryFilters -- if they did not, R-20 default-deny would hide every staged stop.
    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));

    private StopPromotionService Service(TradingCopilotDbContext context) =>
        new(context, NullLogger<StopPromotionService>.Instance);

    private async Task<Guid> SeedStagedStopAsync(
        OrderSide side = OrderSide.Buy,
        decimal entry = 5_300m,
        decimal actualStop = 5_295m,
        decimal safetyStop = 5_290m,
        int promotionTicks = 4,
        StopStaging staging = StopStaging.Hidden,
        string instrument = "CON.F.US.MES.U26")
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        // A seeding context WITH the operator, so the rows are written owned; the service reads them back with
        // no user context (filters ignored), exactly as the running watcher does.
        await using TradingCopilotDbContext seed = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

        seed.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection { Id = connectionId, UserId = _operator, FirmId = firmId, Platform = "projectx", CredentialKey = "topstep-main" });
        seed.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = _operator,
            ConnectionId = connectionId,
            VenueAccountKey = "9001",
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            Mode = TradingMode.Practice,
        });
        seed.Orders.Add(new Order
        {
            Id = orderId,
            UserId = _operator,
            AccountId = accountId,
            Instrument = instrument,
            Symbol = "MES",
            Side = side,
            Size = 2,
            Type = OrderType.Market,
            Status = OrderStatus.Working,
            Mode = TradingMode.Practice,
            EntryPrice = entry,
            WorkingStopPrice = actualStop,
            SafetyStopPrice = safetyStop,
            ReferencePrice = entry,
            TickSize = 0.25m,
            PointValue = 5m,
            PlacedAt = DateTimeOffset.UnixEpoch,
        });
        seed.StopPlans.Add(new StopPlanRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            OrderId = orderId,
            Side = side,
            EntryPrice = entry,
            ActualStopPrice = actualStop,
            SafetyStopPrice = safetyStop,
            ProximityMetric = StopProximityMetric.Ticks,
            ProximityValue = promotionTicks,
            Staging = staging,
        });
        await seed.SaveChangesAsync();

        // Venue truth for the position-awareness check (gh#183 follow-up): by default the seeded position is OPEN
        // on the entered side, so a due stop promotes exactly as before. Tests that exercise the skip override this.
        IReadOnlyList<PositionSnapshot> open =
        [
            new PositionSnapshot(
                VenueAccountId.Create(Venue, "9001"),
                VenueContractId.Create(Venue, instrument),
                side == OrderSide.Buy ? 2 : -2,
                new Price(entry)),
        ];
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(open);
        return orderId;
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldPromoteADueStop_TransmittingTheNativeStop_AndFlippingStaging()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m); // band 4t x 0.25 = 1.00 -> promote at bid <= 5296
        await using TradingCopilotDbContext context = Context();

        // A long promotes on the BID approaching the stop -- that is the price a long exits at.
        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(1);
        OrderRequest sent = Fake.GetCalls(_venue)
            .Single(call => call.Method.Name == nameof(IOrderExecutor.PlaceOrderAsync))
            .Arguments.Get<OrderRequest>(0)!;
        sent.Type.Should().Be(OrderType.Stop);
        sent.Side.Should().Be(OrderSide.Sell);          // the protective exit for a long
        sent.StopPrice.Should().Be(new Price(5_295m));  // at the actual stop
        sent.Quantity.Should().Be(2);
        sent.Account.Key.Should().Be("9001");

        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Native);
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldDoNothing_WhilePriceIsFarFromTheStop()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m); // promote at bid <= 5296
        await using TradingCopilotDbContext context = Context();

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_299m, ask: 5_299.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Hidden); // still hidden
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldUseTheAsk_ForAShortPosition()
    {
        // A short's stop is ABOVE entry; it exits by buying at the ask, so the ask approaching the stop promotes.
        await SeedStagedStopAsync(OrderSide.Sell, entry: 5_300m, actualStop: 5_305m, safetyStop: 5_310m);
        await using TradingCopilotDbContext context = Context();

        // Bid is far, ask is within the band (5305 - 1.00 = 5304): a bid-only check would miss this.
        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_303.5m, ask: 5_304m, _venue, CancellationToken.None);

        promoted.Should().Be(1);
        OrderRequest sent = Fake.GetCalls(_venue)
            .Single(call => call.Method.Name == nameof(IOrderExecutor.PlaceOrderAsync))
            .Arguments.Get<OrderRequest>(0)!;
        sent.Side.Should().Be(OrderSide.Buy); // the protective exit for a short
        sent.StopPrice.Should().Be(new Price(5_305m));
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldSkipAnAlreadyNativeStop()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m, staging: StopStaging.Native);
        await using TradingCopilotDbContext context = Context();

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_294m, ask: 5_294.25m, _venue, CancellationToken.None);

        // Already at the venue -- must not re-transmit a stop that already rests there.
        promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldIgnoreStops_ForOtherContracts()
    {
        await SeedStagedStopAsync(OrderSide.Buy, instrument: "CON.F.US.ENQ.U26"); // an NQ stop
        await using TradingCopilotDbContext context = Context();

        // A quote for MES must not promote an NQ stop, however close the number looks.
        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_295m, ask: 5_295.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void TryDecodeQuote_ShouldDecodeAMarketQuoteEvent()
    {
        EventEnvelope quote = new(
            Guid.NewGuid(), 1, "market.quote", "projectx", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            """{"contract":"projectx:CON.F.US.MES.U26","bid":5300.25,"ask":5300.50,"bidSize":12,"askSize":8}""",
            TraceParent: null);

        StopPromotionService.TryDecodeQuote(quote, out StopPromotionService.DecodedQuote decoded).Should().BeTrue();
        decoded.Venue.Should().Be(VenueId.Parse("projectx"));
        decoded.ContractKey.Should().Be("CON.F.US.MES.U26");
        decoded.Bid.Should().Be(5_300.25m);
        decoded.Ask.Should().Be(5_300.50m);
    }

    [Theory]
    [InlineData("market.trade", """{"contract":"projectx:CON.F.US.MES.U26","bid":1,"ask":2}""")] // not a quote
    [InlineData("market.quote", """{"contract":"no-colon-key","bid":1,"ask":2}""")]              // malformed contract
    [InlineData("market.quote", """{"bid":1,"ask":2}""")]                                         // missing contract
    [InlineData("market.quote", "not json")]
    public void TryDecodeQuote_ShouldReturnFalse_ForANonQuoteOrMalformedEvent(string type, string payload)
    {
        // A consumer sees every event on the shared log; one it cannot read is skipped, never fatal.
        EventEnvelope evt = new(Guid.NewGuid(), 1, type, "projectx", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, payload, null);

        StopPromotionService.TryDecodeQuote(evt, out _).Should().BeFalse();
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldNotFlipStaging_WhenTheVenueRejectsTheStop()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue rejected the stop"));
        await using TradingCopilotDbContext context = Context();

        Func<Task> act = () => Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        // If the native stop did not reach the venue, the plan must stay Hidden -- marking it Native would claim
        // an exchange-held stop that does not exist, on a safety path. Surface the failure; do not persist a lie.
        await act.Should().ThrowAsync<InvalidOperationException>();
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Hidden);
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldSkipPromotion_WhenTheVenueReportsThePositionFlat()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m); // a due stop: bid 5296 is within the 1.00 band
        await using TradingCopilotDbContext context = Context();

        // The position has already been flattened (a manual flatten, the actual stop firing, auto-flatten): the
        // venue reports nothing open. Promoting now would place a native protective stop for a flat position -- a
        // live resting order with no position behind it (gh#183). Skip, leaving the plan for OcoExitService to retire.
        IReadOnlyList<PositionSnapshot> flat = [];
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(flat);

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Hidden); // untouched
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldSkipPromotion_WhenThePositionHasReversed()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await using TradingCopilotDbContext context = Context();

        // A stop-and-reverse during the outage window: the long is gone and a SHORT is now open on the contract. A
        // long's protective sell-stop must not be placed against a short position -- that is net exposure on the
        // wrong side, not the position this plan protects.
        IReadOnlyList<PositionSnapshot> reversed =
        [
            new PositionSnapshot(VenueAccountId.Create(Venue, "9001"), Contract, -3, new Price(5_296m)),
        ];
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(reversed);

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldSkipPromotion_WhenThePositionCannotBeConfirmed()
    {
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await using TradingCopilotDbContext context = Context();

        // The venue cannot be reached to confirm the position. Uncertainty resolves to the safe state (§9): do not
        // promote on an unverified assumption. The native safety stop remains the floor and the next quote retries.
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue unreachable"));

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Hidden);
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldSelfCancelAndNotClobber_WhenThePlanIsRetiredConcurrentlyDuringPlacement()
    {
        // The position reads open, so promotion transmits. But WHILE the place is in flight, OCO-cancel-on-exit
        // retires the plan (Hidden -> Retired) as the position goes flat. The promotion must, on re-reading the
        // staging, find it no longer Hidden and (a) NOT clobber Retired back to Native, and (b) cancel the native
        // stop it just placed -- it is now a dangling resting order with no position behind it (the gh#183 hazard).
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await using TradingCopilotDbContext context = Context();

        // Simulate the concurrent retire as the side effect of the placement round-trip (a separate context, as the
        // OcoExitService would use). "STOP-1" is the venue handle the placement returns, and the leg to cancel.
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async (OrderRequest r, CancellationToken _) =>
            {
                await using TradingCopilotDbContext concurrent = Context();
                StopPlanRecord plan = await concurrent.StopPlans.IgnoreQueryFilters().SingleAsync();
                plan.Staging = StopStaging.Retired;
                await concurrent.SaveChangesAsync();
                return new PlacedOrder(r.Account, "STOP-1", DateTimeOffset.UnixEpoch);
            });

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        // The just-placed dangling stop is cancelled by the promoter itself -- not left for another sweep to miss.
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, "STOP-1", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        // The winner's Retired staging survives -- never clobbered back to Native.
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldNotThrowAndLeaveRetired_WhenTheDanglingLegCannotBeCancelled()
    {
        // Same lost race, but the self-cancel itself fails (the venue rejects it). The pass must not throw -- the
        // failure is the synthetic_risk case (a live resting order with no position behind it), logged for manual
        // intervention -- and it must still not clobber the winner's Retired.
        await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await using TradingCopilotDbContext context = Context();

        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async (OrderRequest r, CancellationToken _) =>
            {
                await using TradingCopilotDbContext concurrent = Context();
                StopPlanRecord plan = await concurrent.StopPlans.IgnoreQueryFilters().SingleAsync();
                plan.Staging = StopStaging.Retired;
                await concurrent.SaveChangesAsync();
                return new PlacedOrder(r.Account, "STOP-1", DateTimeOffset.UnixEpoch);
            });
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("venue rejected the cancel"));

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(0);
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, "STOP-1", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Retired);
    }

    [Fact]
    public async Task PromoteForQuoteAsync_ShouldPromoteTheOtherStops_WhenOneLosesARaceInTheSamePass()
    {
        // Two due stops in one pass, both positions open. On the FIRST placement a concurrent OCO-exit retires plan
        // A (only). Whichever order that first placement was for, A ends up self-cancelled and B still promotes --
        // proving one plan's lost race is isolated from the other's (the per-record reconcile, not a batched save
        // that would roll back B too).
        Guid orderA = await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        Guid orderB = await SeedStagedStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await using TradingCopilotDbContext context = Context();

        bool firstPlace = true;
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async (OrderRequest r, CancellationToken _) =>
            {
                if (firstPlace)
                {
                    firstPlace = false;
                    await using TradingCopilotDbContext concurrent = Context();
                    StopPlanRecord planA = await concurrent.StopPlans.IgnoreQueryFilters().SingleAsync(p => p.OrderId == orderA);
                    planA.Staging = StopStaging.Retired;
                    await concurrent.SaveChangesAsync();
                }

                return new PlacedOrder(r.Account, "STOP-1", DateTimeOffset.UnixEpoch);
            });

        int promoted = await Service(context).PromoteForQuoteAsync(Venue, "CON.F.US.MES.U26", bid: 5_296m, ask: 5_296.25m, _venue, CancellationToken.None);

        promoted.Should().Be(1); // exactly B
        A.CallTo(() => _venue.CancelOrderAsync(A<VenueAccountId>._, "STOP-1", A<CancellationToken>._)).MustHaveHappenedOnceExactly(); // A's dangling leg
        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().Where(p => p.OrderId == orderA).SingleAsync()).Staging.Should().Be(StopStaging.Retired);
        (await reload.StopPlans.IgnoreQueryFilters().Where(p => p.OrderId == orderB).SingleAsync()).Staging.Should().Be(StopStaging.Native);
    }
}
