using FakeItEasy;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Recovery from an event-log retention gap (gh#306, R-1, ADR-0001): what a consumer missed while blind,
/// reconstructed from the clean-historical bar store (gh#302) rather than from the log, which no longer has it.
/// </summary>
/// <remarks>
/// <para>
/// gh#227 replaced a silent hole with a loud signal and handed recovery here. Three failure modes carry the
/// weight, and each is a quiet one: a hidden stop whose promotion band was crossed <b>unobserved</b> and left
/// hidden; a partial backfill <b>reported as complete</b>, which would be worse than the silence it replaced
/// because it would be believed; and an entry <b>fired on a stale cross</b> at a price that has moved on.
/// </para>
/// </remarks>
public class GapBackfillServiceTests
{
    private const string ContractKey = "CON.F.US.MES.U26";

    private static DateTimeOffset From => new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset To => new(2026, 7, 27, 15, 0, 0, TimeSpan.Zero);

    private readonly Guid _operator = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly ITradingVenue _venue = A.Fake<ITradingVenue>();
    private readonly IIndicatorSource _indicators = A.Fake<IIndicatorSource>();
    private static VenueId Venue => VenueId.Parse("projectx");

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    public GapBackfillServiceTests()
    {
        A.CallTo(() => _venue.Id).Returns(Venue);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._))
            .ReturnsLazily((OrderRequest r, CancellationToken _) => Task.FromResult(
                new PlacedOrder(r.Account, "STOP-1", DateTimeOffset.UnixEpoch)));
    }

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));

    /// <remarks>
    /// The indicator seam is a fake that is never consulted: every plan this suite seeds carries a <b>tick</b>
    /// band, and gh#311 only queries an indicator for an ATR one. It is here to satisfy the constructor, not to
    /// influence a decision — so a promotion in these tests still turns purely on the recovered bar extremes.
    /// </remarks>
    private GapBackfillService Service(TradingCopilotDbContext context) =>
        new(context,
            new StopPromotionService(
                context,
                _indicators,
                Options.Create(new IndicatorOptions()),
                NullLogger<StopPromotionService>.Instance),
            NullLogger<GapBackfillService>.Instance);

    private TradingCopilotDbContext Seed() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(_operator));

    /// <summary>Seeds a hidden stop plan on MES with a 4-tick (1.00) promotion band.</summary>
    private async Task SeedHiddenStopAsync(
        OrderSide side = OrderSide.Buy,
        decimal entry = 5_300m,
        decimal actualStop = 5_295m,
        decimal safetyStop = 5_290m)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        await using TradingCopilotDbContext seed = Seed();
        seed.Firms.Add(new Firm { Id = firmId, UserId = _operator, Name = "Topstep", Type = FirmType.PropFirm });
        seed.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = _operator,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = "topstep-main",
        });
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
            Instrument = ContractKey,
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
            ProximityValue = 4,
            Staging = StopStaging.Hidden,
        });
        await seed.SaveChangesAsync();

        IReadOnlyList<PositionSnapshot> open =
        [
            new PositionSnapshot(
                VenueAccountId.Create(Venue, "9001"), VenueContractId.Create(Venue, ContractKey),
                side == OrderSide.Buy ? 2 : -2, new Price(entry)),
        ];
        A.CallTo(() => _venue.GetPositionsAsync(A<VenueAccountId>._, A<CancellationToken>._)).Returns(open);
    }

    /// <summary>Seeds bars covering the whole window at 1-minute resolution, with the given extremes.</summary>
    private async Task SeedBarsAsync(decimal high, decimal low, int minutes = 60, string symbol = "MES")
    {
        await using TradingCopilotDbContext seed = Seed();
        for (int minute = 0; minute < minutes; minute++)
        {
            seed.Bars.Add(new BarRecord
            {
                Venue = "projectx",
                Instrument = symbol,
                ResolutionMinutes = 1,
                BucketStart = From.AddMinutes(minute),
                Open = 5_300m,
                High = high,
                Low = low,
                Close = 5_300m,
                Volume = 10,
                RecordedAt = From.AddMinutes(minute),
            });
        }

        await seed.SaveChangesAsync();
    }

    // --- Stop promotion: the consumer that DOES recover ---

    [Fact]
    public async Task ReplayForStopPromotionAsync_ShouldPromote_WhenTheBlindWindowCrossedThePromotionBand()
    {
        // Stop 5295, band 4t x 0.25 = 1.00 -> promote once the bid reaches 5296. Price dipped to 5296 during the
        // window and recovered. Without this, the stop stays hidden — its own rule said promote, and the consumer
        // that would have seen it was blind.
        await SeedHiddenStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await SeedBarsAsync(high: 5_310m, low: 5_296m);
        await using TradingCopilotDbContext context = Context();

        GapBackfillOutcome outcome = await Service(context)
            .ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None);

        outcome.Promoted.Should().Be(1);
        outcome.IsComplete.Should().BeTrue();

        await using TradingCopilotDbContext reload = Context();
        (await reload.StopPlans.IgnoreQueryFilters().SingleAsync()).Staging.Should().Be(StopStaging.Native);
    }

    [Fact]
    public async Task ReplayForStopPromotionAsync_ShouldNotPromote_WhenTheBlindWindowNeverReachedTheBand()
    {
        // The window's deepest adverse excursion was 5297 — outside the 5296 band. Promoting anyway would place a
        // native stop the plan's own rule never asked for.
        await SeedHiddenStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await SeedBarsAsync(high: 5_310m, low: 5_297m);
        await using TradingCopilotDbContext context = Context();

        GapBackfillOutcome outcome = await Service(context)
            .ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None);

        outcome.Promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ReplayForStopPromotionAsync_ShouldMeasureAShortOnTheWindowHigh_NotItsLow()
    {
        // A short is protected by a stop ABOVE it and exits at the ask, so its worst case is the window's HIGH.
        // Measuring a short on the low would silently never promote it — the mirror-image bug, and invisible.
        await SeedHiddenStopAsync(OrderSide.Sell, entry: 5_300m, actualStop: 5_305m, safetyStop: 5_310m);
        await SeedBarsAsync(high: 5_304m, low: 5_290m); // high is inside the band (>= 5305 - 1.00), low is nowhere near
        await using TradingCopilotDbContext context = Context();

        GapBackfillOutcome outcome = await Service(context)
            .ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None);

        outcome.Promoted.Should().Be(1);
    }

    [Fact]
    public async Task ReplayForStopPromotionAsync_ShouldBeIdempotent_WhenTheSameWindowIsReplayedTwice()
    {
        // At-least-once redelivery on restart is normal (ADR-0001), so a second replay must be a no-op rather than
        // a second native stop resting at the exchange for the same position.
        await SeedHiddenStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await SeedBarsAsync(high: 5_310m, low: 5_296m);

        await using (TradingCopilotDbContext first = Context())
        {
            (await Service(first).ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None))
                .Promoted.Should().Be(1);
        }

        await using TradingCopilotDbContext second = Context();
        GapBackfillOutcome again = await Service(second)
            .ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None);

        again.Promoted.Should().Be(0);
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ReplayForStopPromotionAsync_ShouldReportShortfall_WhenTheStoreHasNoBarsForTheInstrument()
    {
        // The instrument is not in Backfill:Instruments, so nothing about the window is recoverable for it.
        // Reporting completion here would claim a recovery that did not happen at all.
        await SeedHiddenStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await SeedBarsAsync(high: 5_310m, low: 5_296m, symbol: "NQ"); // a different instrument entirely
        await using TradingCopilotDbContext context = Context();

        GapBackfillOutcome outcome = await Service(context)
            .ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None);

        outcome.Promoted.Should().Be(0);
        outcome.IsComplete.Should().BeFalse();
        outcome.Shortfalls.Should().ContainSingle()
            .Which.Coverage.Shortfall.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ReplayForStopPromotionAsync_ShouldReportShortfall_WhenTheStoreCoversOnlyPartOfTheWindow()
    {
        // A partial backfill that read as "recovered" is the failure this reporting exists to prevent.
        await SeedHiddenStopAsync(OrderSide.Buy, actualStop: 5_295m);
        await SeedBarsAsync(high: 5_310m, low: 5_296m, minutes: 20); // 20 of the 60 minutes
        await using TradingCopilotDbContext context = Context();

        GapBackfillOutcome outcome = await Service(context)
            .ReplayForStopPromotionAsync(From, To, Venue, _venue, CancellationToken.None);

        outcome.Promoted.Should().Be(1, "what the store DID cover is still acted on");
        outcome.IsComplete.Should().BeFalse("but the 40 uncovered minutes are reported, not rounded away");
        outcome.Shortfalls.Should().ContainSingle()
            .Which.Coverage.Shortfall.Should().Be(TimeSpan.FromMinutes(40));
    }

    // --- Conditional firing: the consumer that deliberately does NOT recover ---

    [Fact]
    public async Task DetectMissedTriggersAsync_ShouldNameACrossedTrigger_WithoutFiringIt()
    {
        await SeedConditionalAsync(triggerPrice: 5_305m, direction: ConditionalCrossDirection.RisesTo);
        await SeedBarsAsync(high: 5_310m, low: 5_296m);
        await using TradingCopilotDbContext context = Context();

        IReadOnlyList<MissedTrigger> missed = await Service(context)
            .DetectMissedTriggersAsync(From, To, CancellationToken.None);

        missed.Should().ContainSingle();
        missed[0].TriggerPrice.Should().Be(5_305m);
        missed[0].ReachedPrice.Should().Be(5_310m);

        // The whole point: detected, reported, NOT acted on. A conditional fires an entry, and firing one on a
        // cross this old would open a position at a price that has moved on (ADR-0013, R-12).
        A.CallTo(() => _venue.PlaceOrderAsync(A<OrderRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        await using TradingCopilotDbContext reload = Context();
        (await reload.ConditionalOrders.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be(ConditionalStatus.Pending, "it stays Pending and re-decides on the next live quote");
    }

    [Fact]
    public async Task DetectMissedTriggersAsync_ShouldIgnoreATrigger_TheWindowNeverCrossed()
    {
        await SeedConditionalAsync(triggerPrice: 5_320m, direction: ConditionalCrossDirection.RisesTo);
        await SeedBarsAsync(high: 5_310m, low: 5_296m);
        await using TradingCopilotDbContext context = Context();

        (await Service(context).DetectMissedTriggersAsync(From, To, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task DetectMissedTriggersAsync_ShouldMeasureAFallsToTriggerOnTheWindowLow()
    {
        // The mirror of the short-stop case: measuring a falls-to trigger against the high would never report it.
        await SeedConditionalAsync(triggerPrice: 5_297m, direction: ConditionalCrossDirection.FallsTo);
        await SeedBarsAsync(high: 5_310m, low: 5_296m);
        await using TradingCopilotDbContext context = Context();

        IReadOnlyList<MissedTrigger> missed = await Service(context)
            .DetectMissedTriggersAsync(From, To, CancellationToken.None);

        missed.Should().ContainSingle();
        missed[0].ReachedPrice.Should().Be(5_296m);
    }

    private async Task SeedConditionalAsync(decimal triggerPrice, ConditionalCrossDirection direction)
    {
        await using TradingCopilotDbContext seed = Seed();
        seed.ConditionalOrders.Add(new ConditionalOrderRecord
        {
            Id = Guid.NewGuid(),
            UserId = _operator,
            AccountId = Guid.NewGuid(),
            Instrument = ContractKey,
            Symbol = "MES",
            Side = OrderSide.Buy,
            Size = 1,
            Type = OrderType.Market,
            EntryPrice = 5_306m,
            WorkingStopPrice = 5_300m,
            SafetyStopPrice = 5_295m,
            ReferencePrice = 5_306m,
            TickSize = 0.25m,
            PointValue = 5m,
            TriggerPrice = triggerPrice,
            TriggerDirection = direction,
            Status = ConditionalStatus.Pending,
            Mode = TradingMode.Practice,
            CreatedAt = From,
        });
        await seed.SaveChangesAsync();
    }
}
