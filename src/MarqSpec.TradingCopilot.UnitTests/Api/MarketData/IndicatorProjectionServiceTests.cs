using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Projecting indicators over the clean-historical bar store (gh#310, R-1, ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0001 fixes the shape — *"indicators are projections over the log… rebuild = replay"* — with the later
/// correction that a full rebuild reprocesses the <b>clean-historical store</b> (gh#302), not the 24-hour event
/// log. This is that projection.
/// </para>
/// <para>
/// The work list is <b>derived from the store</b> rather than configured: whatever instrument × resolution has
/// bars gets indicators. A second symbol list would have been a second thing to keep in sync, and the failure
/// mode of drifting apart is an instrument whose bars are archived but whose ATR is silently absent — which the
/// execution path would meet as "no value", i.e. a stop that never promotes.
/// </para>
/// </remarks>
public class IndicatorProjectionServiceTests
{
    private const string Venue = "projectx";

    private static DateTimeOffset Bucket(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    // Regression pin: the ATR path is now one indicator in a collection. Re-pointing the ctor while keeping every
    // assertion below verbatim is the proof the generalisation did not move a single stored ATR value.
    private static IndicatorProjectionService Service(TradingCopilotDbContext context, int period = 3) =>
        new(context, [new AtrIndicator(period)], NullLogger<IndicatorProjectionService>.Instance);

    /// <summary>The worked series from the ATR tests: true ranges 10, 20, 30, 50 → ATR 20 then 30.</summary>
    private static async Task SeedWorkedSeriesAsync(TradingCopilotDbContext context, string instrument = "ES")
    {
        (int Minute, decimal High, decimal Low, decimal Close)[] bars =
        [
            (0, 100, 100, 100),
            (1, 110, 100, 105),
            (2, 125, 105, 120),
            (3, 150, 120, 140),
            (4, 190, 140, 180),
        ];

        foreach ((int minute, decimal high, decimal low, decimal close) in bars)
        {
            context.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = instrument,
                ResolutionMinutes = 1,
                BucketStart = Bucket(minute),
                Open = low,
                High = high,
                Low = low,
                Close = close,
                Volume = 100,
                RecordedAt = Bucket(minute),
            });
        }

        await context.SaveChangesAsync();
    }

    // --- It computes, and the arithmetic survives the round trip ---

    [Fact]
    public async Task ProjectAsync_ShouldStoreTheHandComputedValues()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);

        await Service(context).ProjectAsync(CancellationToken.None);

        List<IndicatorValueRecord> stored = await context.IndicatorValues.OrderBy(v => v.BucketStart).ToListAsync();
        stored.Should().HaveCount(2); // only the buckets where the period is satisfied
        stored[0].Value.Should().Be(20m);
        stored[1].Value.Should().Be(30m);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotStoreAValue_WhereThePeriodIsNotYetSatisfied()
    {
        // A partial ATR would place a stop at the wrong distance while looking entirely ordinary.
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);

        await Service(context).ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.AnyAsync(v => v.BucketStart < Bucket(3))).Should().BeFalse();
    }

    [Fact]
    public async Task ProjectAsync_ShouldStoreNothing_WhenThereIsTooLittleHistory()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);
        context.Bars.RemoveRange(await context.Bars.Where(b => b.BucketStart >= Bucket(2)).ToListAsync());
        await context.SaveChangesAsync();

        await Service(context).ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.CountAsync()).Should().Be(0);
    }

    // --- Rebuild = replay ---

    [Fact]
    public async Task ProjectAsync_ShouldBeIdempotent_WhenRunTwiceOverUnchangedBars()
    {
        // ADR-0001's rebuild story: recomputing must restore the same series, not append a second one.
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);
        IndicatorProjectionService service = Service(context);

        await service.ProjectAsync(CancellationToken.None);
        await service.ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ProjectAsync_ShouldUpdateTheDerivedValue_WhenABarIsRevised()
    {
        // gh#302 upserts a restated bar in place. The value derived from it has to follow, or the store holds a
        // number no longer supported by the data underneath it.
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);
        IndicatorProjectionService service = Service(context);
        await service.ProjectAsync(CancellationToken.None);

        BarRecord revised = await context.Bars.SingleAsync(b => b.BucketStart == Bucket(4));
        revised.High = 240m; // widens the last true range
        await context.SaveChangesAsync();
        await service.ProjectAsync(CancellationToken.None);

        IndicatorValueRecord last = await context.IndicatorValues
            .OrderByDescending(v => v.BucketStart).FirstAsync();
        last.Value.Should().BeGreaterThan(30m);
        (await context.IndicatorValues.CountAsync()).Should().Be(2);
    }

    // --- The work list comes from the store ---

    [Fact]
    public async Task ProjectAsync_ShouldCoverEveryInstrumentThatHasBars()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context, "ES");
        await SeedWorkedSeriesAsync(context, "NQ");

        await Service(context).ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.Select(v => v.Instrument).Distinct().ToListAsync())
            .Should().BeEquivalentTo(["ES", "NQ"]);
    }

    [Fact]
    public async Task ProjectAsync_ShouldKeepSeriesApart_ByInstrument()
    {
        // Values keyed loosely would let one instrument's ATR answer for another's -- and a stop distance taken
        // from the wrong market is not a small error.
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context, "ES");
        await SeedWorkedSeriesAsync(context, "NQ");

        await Service(context).ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.CountAsync(v => v.Instrument == "ES")).Should().Be(2);
        (await context.IndicatorValues.CountAsync(v => v.Instrument == "NQ")).Should().Be(2);
    }

    [Fact]
    public async Task ProjectAsync_ShouldRecordThePeriodItUsed()
    {
        // The period is part of the identity: an ATR(14) and an ATR(3) are different numbers, and a consumer
        // asking for one must never be handed the other.
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);

        await Service(context, period: 3).ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.FirstAsync()).Period.Should().Be(3);
    }

    // --- The framework runs more than one indicator ---

    [Fact]
    public async Task ProjectAsync_ShouldWriteEveryIndicator_KeptApartByName()
    {
        // The whole point of the increment: a second indicator projects over the SAME bars into its own
        // (Indicator, Period) family, and the safety-critical ATR is untouched by RSI riding alongside it.
        await using TradingCopilotDbContext context = Context();
        await SeedWorkedSeriesAsync(context);

        IndicatorProjectionService service = new(
            context, [new AtrIndicator(3), new RsiIndicator(3)], NullLogger<IndicatorProjectionService>.Instance);
        await service.ProjectAsync(CancellationToken.None);

        List<IndicatorValueRecord> atr = await context.IndicatorValues
            .Where(v => v.Indicator == "atr").OrderBy(v => v.BucketStart).ToListAsync();
        atr.Select(v => v.Value).Should().Equal(20m, 30m); // unchanged from the ATR-only case

        // The worked closes only rise (100 → 105 → 120 → 140 → 180), so RSI is a pure-gains 100.
        List<IndicatorValueRecord> rsi = await context.IndicatorValues
            .Where(v => v.Indicator == "rsi").OrderBy(v => v.BucketStart).ToListAsync();
        rsi.Should().HaveCount(2);
        rsi.Should().AllSatisfy(v =>
        {
            v.Period.Should().Be(3);
            v.Value.Should().Be(100m);
        });

        (await context.IndicatorValues.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task ProjectAsync_ShouldDoNothing_WhenTheBarStoreIsEmpty()
    {
        await using TradingCopilotDbContext context = Context();

        await Service(context).ProjectAsync(CancellationToken.None);

        (await context.IndicatorValues.CountAsync()).Should().Be(0);
    }
}
