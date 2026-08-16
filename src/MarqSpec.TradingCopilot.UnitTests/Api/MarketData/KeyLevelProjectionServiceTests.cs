using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// Projecting key-level zones over the clean-historical bar store (gh#597, R-10 / R-22, ADR-0001) and reconciling
/// them into the <see cref="PriceLevel"/> table (gh#596).
/// </summary>
/// <remarks>
/// <para>
/// Follows the <see cref="IndicatorProjectionServiceTests"/> shape exactly: an EF-InMemory context, a work list
/// derived from the store, a per-series guard, and one save per series. The pivot maths belongs to
/// <c>KeyLevelsTests</c>; this tier proves the <b>persistence wiring</b> — insert, idempotent reconcile, retire,
/// role-reversal flip, touch-count raise, and per-series isolation.
/// </para>
/// <para>
/// The seed is a long flat series with a single high spike, which makes exactly one confirmed pivot on the shipped
/// 20/15 Heikin-Ashi window — enough bars for the real window, since the service calls <c>KeyLevels.Detect</c> with
/// <see cref="KeyLevelOptions.Default"/> and cannot tighten it.
/// </para>
/// </remarks>
public class KeyLevelProjectionServiceTests
{
    private const string Venue = "projectx";
    private const int AtrPeriod = 3;
    private const int BarCount = 60;

    private static DateTimeOffset Bucket(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.NewGuid()));

    private static KeyLevelProjectionService Service(TradingCopilotDbContext context, int maxPerKind = 12) =>
        new(context,
            Options.Create(new KeyLevelDetectorOptions { MaxLevelsPerKind = maxPerKind }),
            Options.Create(new IndicatorOptions { AtrPeriod = AtrPeriod }),
            NullLogger<KeyLevelProjectionService>.Instance);

    private static IndicatorValueRecord AtrRow(string instrument, int minute, decimal value) => new()
    {
        Venue = Venue,
        Instrument = instrument,
        ResolutionMinutes = 1,
        Indicator = AtrIndicator.IndicatorName,
        Period = AtrPeriod,
        BucketStart = Bucket(minute),
        Value = value,
        RecordedAt = Bucket(minute),
    };

    /// <summary>
    /// Seeds a flat 60-bar series (o = h = l = c = 100) with a high spike at each given minute, and an ATR row at
    /// each spike bucket unless suppressed. One high spike in a flat Heikin-Ashi series makes exactly one confirmed
    /// pivot high on the 20/15 window.
    /// </summary>
    private async Task SeedAsync(
        TradingCopilotDbContext context,
        string instrument,
        IReadOnlyCollection<int> spikeMinutes,
        decimal atr = 3m,
        bool seedAtr = true)
    {
        for (int minute = 0; minute < BarCount; minute++)
        {
            bool spike = spikeMinutes.Contains(minute);
            context.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = instrument,
                ResolutionMinutes = 1,
                BucketStart = Bucket(minute),
                Open = 100m,
                High = spike ? 200m : 100m,
                Low = 100m,
                Close = 100m,
                Volume = 100,
                RecordedAt = Bucket(minute),
            });
        }

        if (seedAtr)
        {
            foreach (int minute in spikeMinutes)
            {
                context.IndicatorValues.Add(AtrRow(instrument, minute, atr));
            }
        }

        await context.SaveChangesAsync();
    }

    // --- It inserts what the detector finds ---

    [Fact]
    public async Task ProjectAsync_ShouldInsertActiveLevels_ForEverySeriesThatFormsThem()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20]);
        await SeedAsync(context, "NQ", [20]);

        await Service(context).ProjectAsync(CancellationToken.None);

        List<PriceLevel> active = await context.PriceLevels.Where(level => level.Active).ToListAsync();
        active.Select(level => level.Instrument).Should().BeEquivalentTo(["ES", "NQ"]);
        active.Should().OnlyContain(level => level.TimeframeMinutes == 1);
    }

    [Fact]
    public async Task ProjectAsync_ShouldWriteNothing_WhenTheSeriesHasNoAtr()
    {
        // No ATR at the pivot's bucket => the pivot is skipped => no zone => no row. A partial write here would be a
        // level the detector cannot actually measure.
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20], seedAtr: false);

        int written = await Service(context).ProjectAsync(CancellationToken.None);

        written.Should().Be(0);
        (await context.PriceLevels.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ProjectAsync_ShouldDoNothing_WhenTheBarStoreIsEmpty()
    {
        await using TradingCopilotDbContext context = Context();

        int written = await Service(context).ProjectAsync(CancellationToken.None);

        written.Should().Be(0);
        (await context.PriceLevels.CountAsync()).Should().Be(0);
    }

    // --- Rebuild = replay: a 2nd pass over unchanged bars writes nothing ---

    [Fact]
    public async Task ProjectAsync_ShouldBeIdempotent_QuantisingSignificanceToTheColumnScale()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20], atr: 3m);
        KeyLevelProjectionService service = Service(context);

        int first = await service.ProjectAsync(CancellationToken.None);
        int second = await service.ProjectAsync(CancellationToken.None);

        first.Should().BeGreaterThan(0);
        second.Should().Be(0, "an unchanged series recomputes the same levels, so a 2nd pass must write nothing");
        (await context.PriceLevels.CountAsync()).Should().Be(1);

        // significance = prominence / ATR = 12.5 / 3, which does not terminate. It MUST be stored quantised to the
        // column's numeric(18,8) scale; an un-quantised value round-trips to a different number on Postgres and
        // would rewrite the row every pass forever -- the failure the indicator integration test documents.
        PriceLevel level = await context.PriceLevels.SingleAsync();
        level.Significance.Should().Be(4.16666667m);
    }

    // --- Reconcile: retire, flip, and raise, all in place ---

    [Fact]
    public async Task ProjectAsync_ShouldRetireALevel_WhenItNoLongerForms()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20]);
        KeyLevelProjectionService service = Service(context);
        await service.ProjectAsync(CancellationToken.None);
        Guid id = (await context.PriceLevels.SingleAsync()).Id;

        // Flatten the spike: the pivot no longer forms, so the level is retired -- Active=false, kept for the journal.
        BarRecord spike = await context.Bars.SingleAsync(bar => bar.Instrument == "ES" && bar.BucketStart == Bucket(20));
        spike.High = 100m;
        await context.SaveChangesAsync();
        await service.ProjectAsync(CancellationToken.None);

        PriceLevel retired = await context.PriceLevels.SingleAsync();
        retired.Id.Should().Be(id, "the row is retired in place, never deleted");
        retired.Active.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectAsync_ShouldFlipAStoredLevelsKind_InPlace()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20]);
        KeyLevelProjectionService service = Service(context);
        await service.ProjectAsync(CancellationToken.None);
        PriceLevel before = await context.PriceLevels.SingleAsync();
        before.Kind.Should().Be(PriceLevelKind.Resistance);
        Guid id = before.Id;

        // The last bar closes far above the zone: the resistance reverses to support. The SAME row updates in place.
        BarRecord last = await context.Bars.SingleAsync(bar => bar.Instrument == "ES" && bar.BucketStart == Bucket(59));
        last.High = 200m;
        last.Close = 200m;
        await context.SaveChangesAsync();
        await service.ProjectAsync(CancellationToken.None);

        PriceLevel after = await context.PriceLevels.SingleAsync();
        after.Id.Should().Be(id);
        after.Kind.Should().Be(PriceLevelKind.Support, "a role reversal is a Kind flip on the same row");
        after.Active.Should().BeTrue();
        (await context.PriceLevels.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_ShouldRaiseTouchCount_InPlace_WhenAnOverlappingPivotJoins()
    {
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20]);
        KeyLevelProjectionService service = Service(context);
        await service.ProjectAsync(CancellationToken.None);
        PriceLevel before = await context.PriceLevels.SingleAsync();
        before.TouchCount.Should().Be(1);
        Guid id = before.Id;

        // A second spike at the same price forms an overlapping zone that merges into the first, raising its touch
        // count. The merged level dates from the earlier pivot, so it matches the stored row and updates in place.
        BarRecord bar = await context.Bars.SingleAsync(record => record.Instrument == "ES" && record.BucketStart == Bucket(42));
        bar.High = 200m;
        context.IndicatorValues.Add(AtrRow("ES", 42, 3m));
        await context.SaveChangesAsync();
        await service.ProjectAsync(CancellationToken.None);

        PriceLevel after = await context.PriceLevels.SingleAsync();
        after.Id.Should().Be(id);
        after.TouchCount.Should().Be(2);
        (await context.PriceLevels.CountAsync()).Should().Be(1);
    }

    // --- One series failing must not cost the others their levels ---

    [Fact]
    public async Task ProjectAsync_ShouldIsolateAFailingSeries_FromTheOthers()
    {
        // A corrupt series (a negative ATR makes ZoneFor throw mid-Detect) must not stop the healthy one. Mirrors
        // the indicator projection's per-series try/catch + ChangeTracker.Clear; the fault is tied to the series,
        // so the outcome is order-independent.
        await using TradingCopilotDbContext context = Context();
        await SeedAsync(context, "ES", [20]);
        await SeedAsync(context, "NQ", [20], atr: -5m);

        await Service(context).ProjectAsync(CancellationToken.None);

        (await context.PriceLevels.CountAsync(level => level.Instrument == "ES" && level.Active)).Should().Be(1);
        (await context.PriceLevels.CountAsync(level => level.Instrument == "NQ")).Should().Be(0);
    }
}
