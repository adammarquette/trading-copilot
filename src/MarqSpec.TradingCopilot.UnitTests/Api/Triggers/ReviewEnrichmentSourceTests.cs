using MarqSpec.TradingCopilot.Api.Triggers;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Triggers;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Triggers;

/// <summary>
/// Assembling the deep-tier <see cref="ReviewEnrichment"/> (gh#476) through <see cref="ReviewEnrichmentSource"/> — the
/// numeric market context (recent bars + recent values of the fired indicator's own series) the escalated deep call
/// reads.
/// </summary>
/// <remarks>
/// The design-defining behaviours: it reads <b>as of the fire</b> and never after (no look-ahead — the same honesty
/// <see cref="MarketData.StoredIndicatorSource"/> keeps), returns each series <b>oldest-first</b> and capped to a
/// recent window, keeps the indicator series apart by name/period/resolution, and returns <see langword="null"/> when
/// there is nothing to add so the caller sends an un-enriched deep call rather than an empty block.
/// </remarks>
public class ReviewEnrichmentSourceTests
{
    private static DateTimeOffset Bucket(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    // Market data is GLOBAL (BarRecord / IndicatorValueRecord are not IUserOwned), so the current user is irrelevant to
    // the read -- any identity resolves the same rows.
    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static ReviewEnrichmentSource Source(TradingCopilotDbContext context) => new(context);

    private static TriggerReviewContext ContextAsOf(
        DateTimeOffset firedAt, string indicator = "rsi", int period = 14, int resolution = 1) => new(
        Guid.NewGuid(), InstrumentId.Parse("ES"), indicator, period, resolution,
        IndicatorComparison.Below, 30m, 25m, firedAt);

    private static BarRecord Bar(int minute, int resolution = 1) => new()
    {
        Venue = "projectx",
        Instrument = "ES",
        ResolutionMinutes = resolution,
        BucketStart = Bucket(minute),
        Open = 100m + minute,
        High = 101m + minute,
        Low = 99m + minute,
        Close = 100.5m + minute,
        Volume = 1000 + minute,
        RecordedAt = Bucket(minute),
    };

    private static IndicatorValueRecord Value(int minute, string indicator = "rsi", int period = 14, int resolution = 1) => new()
    {
        Venue = "projectx",
        Instrument = "ES",
        ResolutionMinutes = resolution,
        Indicator = indicator,
        Period = period,
        BucketStart = Bucket(minute),
        Value = 20m + minute,
        RecordedAt = Bucket(minute),
    };

    // --- As of the fire, oldest-first, no look-ahead ---

    [Fact]
    public async Task BuildAsync_ShouldReturnBarsAtOrBeforeTheFire_OldestFirst_AndExcludeLaterAndOtherResolutions()
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.AddRange(
            Bar(3), Bar(1), Bar(2),      // inserted out of order -- the result must be oldest-first regardless
            Bar(5),                      // AFTER the fire -- must be excluded (no look-ahead)
            Bar(2, resolution: 5));      // a different resolution -- a separate series, must be excluded
        await context.SaveChangesAsync();

        ReviewEnrichment? enrichment = await Source(context).BuildAsync(ContextAsOf(Bucket(3)), CancellationToken.None);

        enrichment.Should().NotBeNull();
        // Exactly minutes 1,2,3 in ascending order: the minute-5 (later) and the resolution-5 (other series) rows are gone,
        // and the boundary bar AT the fire (minute 3) is included (BucketStart <= FiredAt).
        enrichment!.RecentBars.Select(bar => bar.BucketStart).Should().Equal(Bucket(1), Bucket(2), Bucket(3));
    }

    [Fact]
    public async Task BuildAsync_ShouldReturnTheFiredIndicatorSeriesOnly_AtOrBeforeTheFire_OldestFirst()
    {
        await using TradingCopilotDbContext context = Context();
        context.IndicatorValues.AddRange(
            Value(3), Value(1), Value(2),        // the fired rsi(14)@1m series, out of insertion order
            Value(5),                            // after the fire -- excluded
            Value(2, indicator: "atr"),          // a different indicator -- excluded
            Value(2, period: 9),                 // a different period -- excluded
            Value(2, resolution: 5));            // a different resolution -- excluded
        await context.SaveChangesAsync();

        ReviewEnrichment? enrichment = await Source(context).BuildAsync(ContextAsOf(Bucket(3)), CancellationToken.None);

        // Exactly the three fired-series values, oldest-first. Any leaked wrong-series (all at minute 2) or later row
        // would change the count/order and fail here -- so this pins name/period/resolution apartness AND no look-ahead.
        enrichment!.RecentIndicatorValues.Select(value => value.BucketStart).Should().Equal(Bucket(1), Bucket(2), Bucket(3));
    }

    // --- Capped to a recent window ---

    [Fact]
    public async Task BuildAsync_ShouldCapEachSeriesToTheMostRecentWindow_OldestFirst()
    {
        await using TradingCopilotDbContext context = Context();
        for (int minute = 1; minute <= 25; minute++)
        {
            context.Bars.Add(Bar(minute));
            context.IndicatorValues.Add(Value(minute));
        }
        await context.SaveChangesAsync();

        ReviewEnrichment? enrichment = await Source(context).BuildAsync(ContextAsOf(Bucket(25)), CancellationToken.None);

        // The window is the MOST-RECENT 20, presented oldest-first: minutes 6..25 (25 rows exist; the oldest 5 drop).
        enrichment!.RecentBars.Should().HaveCount(20);
        enrichment.RecentIndicatorValues.Should().HaveCount(20);
        enrichment.RecentBars.First().BucketStart.Should().Be(Bucket(6));
        enrichment.RecentBars.Last().BucketStart.Should().Be(Bucket(25));
        enrichment.RecentIndicatorValues.First().BucketStart.Should().Be(Bucket(6));
        enrichment.RecentIndicatorValues.Last().BucketStart.Should().Be(Bucket(25));
    }

    // --- Nothing to add, and one-side-present ---

    [Fact]
    public async Task BuildAsync_ShouldReturnNull_WhenBothSeriesAreEmpty()
    {
        await using TradingCopilotDbContext context = Context();

        ReviewEnrichment? enrichment = await Source(context).BuildAsync(ContextAsOf(Bucket(3)), CancellationToken.None);

        // Nothing to add -> null, so the caller sends an un-enriched deep call rather than an empty <market-context>.
        enrichment.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_ShouldReturnEnrichment_WhenOnlyOneSeriesHasData()
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(Bar(2)); // bars present, but no indicator values for the fired series
        await context.SaveChangesAsync();

        ReviewEnrichment? enrichment = await Source(context).BuildAsync(ContextAsOf(Bucket(3)), CancellationToken.None);

        // One non-empty series is still worth adding; the other is simply empty (not null).
        enrichment.Should().NotBeNull();
        enrichment!.RecentBars.Should().ContainSingle();
        enrichment.RecentIndicatorValues.Should().BeEmpty();
    }

    // --- Faithful numeric mapping ---

    [Fact]
    public async Task BuildAsync_ShouldMapBarFieldsFaithfully()
    {
        await using TradingCopilotDbContext context = Context();
        context.Bars.Add(new BarRecord
        {
            Venue = "projectx",
            Instrument = "ES",
            ResolutionMinutes = 1,
            BucketStart = Bucket(2),
            Open = 100m,
            High = 105m,
            Low = 98m,
            Close = 101m,
            Volume = 4242,
            RecordedAt = Bucket(2),
        });
        await context.SaveChangesAsync();

        BarSnapshot bar = (await Source(context).BuildAsync(ContextAsOf(Bucket(3)), CancellationToken.None))!
            .RecentBars.Should().ContainSingle().Subject;

        bar.BucketStart.Should().Be(Bucket(2));
        bar.Open.Should().Be(100m);
        bar.High.Should().Be(105m);
        bar.Low.Should().Be(98m);
        bar.Close.Should().Be(101m);
        bar.Volume.Should().Be(4242);
    }
}
