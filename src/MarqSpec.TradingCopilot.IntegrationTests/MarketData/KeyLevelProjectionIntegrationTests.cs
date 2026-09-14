using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Independent QA integration coverage for the <b>key-level projection host</b> (gh#597, R-10 / R-22, gh#946)
/// against real Timescale/Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Written independently of <c>KeyLevelProjectionServiceTests</c> (the coding agent's EF-InMemory suite) per the
/// QA adversarial-double rule: the inputs (bars, and the ATR that is not driven through the real gh#311
/// projection) are fed directly here, never the production-computed zone. What only real Postgres proves and the
/// InMemory unit tier cannot: the four <c>CK_PriceLevels_*</c> checks actually reject a bad row (InMemory
/// enforces the EF model's shape, never a DB constraint); idempotence over the <c>numeric(18,8)</c> column, where
/// <c>Significance = prominence / ATR</c> does not terminate; that a reconcile (role reversal, overlap merge,
/// retire) survives a real <c>SaveChangesAsync</c> round trip; that the per-series ATR reuse chain (gh#311 →
/// gh#597) actually differentiates two real series rather than sharing one cached value; and that one series'
/// <c>SaveChangesAsync</c> failure is durably isolated from a sibling series already committed to the database —
/// a claim only a real backend's commit semantics can prove.
/// </para>
/// <para>
/// The seed shape mirrors the unit tier's own (flat baseline + one high spike, 60 one-minute bars) because it is
/// documented, by the unit suite, to form exactly one confirmed pivot under the shipped 20/15 Heikin-Ashi window —
/// the smallest series that exercises the real window this suite must not tighten.
/// </para>
/// </remarks>
public class KeyLevelProjectionIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private readonly OcoExitTestPostgresFactory _factory;

    public KeyLevelProjectionIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    private const string VenueKey = "projectx";
    private const string Symbol = "MES";
    private const string OtherSymbol = "MNQ";
    private const int AtrPeriod = 14;
    private const int BarCount = 60;
    private const int SpikeMinute = 20;

    private static DateTimeOffset Origin { get; } = new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Bucket(int minute) => Origin.AddMinutes(minute);

    // -----------------------------------------------------------------------------------------------------------
    // The DB CHECKs hold on detector output
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PriceLevels_ShouldRejectRowsThatViolateTheirCheckConstraints()
    {
        // The InMemory unit tier enforces the EF model's shape, never a DB constraint -- a detector defect that
        // produced one of these rows would pass unit and 500 on a real save. Each candidate is otherwise valid so
        // the failure isolates to the one violated constraint; a fresh scope per attempt keeps a failed save from
        // poisoning the next.
        await ClearAsync();

        await AssertViolatesCheckAsync(
            NewLevel(top: 100m, bottom: 100m), "CK_PriceLevels_ZoneOrdered (Top must exceed Bottom)");
        await AssertViolatesCheckAsync(
            NewLevel(top: 100m, bottom: 0m), "CK_PriceLevels_Bottom_Positive (Bottom must be positive)");
        await AssertViolatesCheckAsync(
            NewLevel(kind: PriceLevelKind.Unknown), "CK_PriceLevels_Kind_NotUnknown (Kind must not be the refusable zero)");
        await AssertViolatesCheckAsync(
            NewLevel(timeframeMinutes: 0), "CK_PriceLevels_Timeframe_Positive (TimeframeMinutes must be positive)");
    }

    // -----------------------------------------------------------------------------------------------------------
    // Idempotence over numeric(18,8)
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectAsync_ShouldWriteNothingOnASecondPass_AndQuantiseSignificanceToTheColumnScale()
    {
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 200m, atr: 3m);

        int first = await ProjectAsync();
        int second = await ProjectAsync();

        first.Should().BeGreaterThan(0, "the first pass must write the detected zone");
        second.Should().Be(0,
            "unchanged bars must recompute the identical zone and write nothing at all -- a non-zero count here "
            + "means the projection rewrites its whole level history every pass, forever");

        // significance = prominence / ATR = 12.5 / 3, which does not terminate in decimal. This is the exact
        // failure IndicatorProjectionIntegrationTests documents for indicator values: an un-quantised value round-
        // trips to something the in-memory change-compare considers different from the numeric(18,8) column's
        // stored value, and the row is rewritten every pass. It must arrive already quantised to eight places.
        PriceLevel level = await QueryDbAsync(db => db.PriceLevels.SingleAsync());
        level.Significance.Should().Be(4.16666667m,
            "the scale-8 quantise must round-trip through real Postgres, not just C#'s in-memory decimal");
    }

    // -----------------------------------------------------------------------------------------------------------
    // Reconcile persisted: role reversal, overlap merge, retire -- all in place, on the SAME row
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectAsync_ShouldFlipAStoredLevelsKind_InPlace_OnRoleReversal()
    {
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 200m, atr: 3m);
        await ProjectAsync();

        PriceLevel before = await QueryDbAsync(db => db.PriceLevels.SingleAsync());
        before.Kind.Should().Be(PriceLevelKind.Resistance, "a pivot high forms a resistance zone");
        Guid id = before.Id;

        // The final bar closes far above the zone's top: the resistance breaks and reverses to support.
        await ExecuteDbAsync(async db =>
        {
            BarRecord last = await db.Bars.SingleAsync(bar => bar.Instrument == Symbol && bar.BucketStart == Bucket(BarCount - 1));
            last.High = 300m;
            last.Close = 300m;
            await db.SaveChangesAsync();
        });
        await ProjectAsync();

        PriceLevel after = await QueryDbAsync(db => db.PriceLevels.SingleAsync());
        after.Id.Should().Be(id, "a role reversal reconciles onto the SAME row, never a second one");
        after.Kind.Should().Be(PriceLevelKind.Support, "price closed through the top, breaking resistance into support");
        after.Active.Should().BeTrue();
        (await QueryDbAsync(db => db.PriceLevels.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_ShouldRaiseTouchCount_InPlace_WhenAnOverlappingPivotMerges()
    {
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 200m, atr: 3m);
        await ProjectAsync();

        PriceLevel before = await QueryDbAsync(db => db.PriceLevels.SingleAsync());
        before.TouchCount.Should().Be(1);
        Guid id = before.Id;

        // A second, later spike at the same price forms an overlapping zone that folds into the first, raising its
        // touch count -- the merged level still dates from the EARLIER pivot, so it still matches the stored row.
        await ExecuteDbAsync(async db =>
        {
            BarRecord bar = await db.Bars.SingleAsync(record => record.Instrument == Symbol && record.BucketStart == Bucket(42));
            bar.High = 200m;
            db.IndicatorValues.Add(AtrRow(Symbol, 42, 3m));
            await db.SaveChangesAsync();
        });
        await ProjectAsync();

        PriceLevel after = await QueryDbAsync(db => db.PriceLevels.SingleAsync());
        after.Id.Should().Be(id, "an overlap merge reconciles onto the SAME row");
        after.TouchCount.Should().Be(2, "the second aligned pivot must raise the stored touch count");
        (await QueryDbAsync(db => db.PriceLevels.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_ShouldRetireALevel_KeepingTheRowForTheJournal_AndExcludeItFromActiveReads()
    {
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 200m, atr: 3m);
        await ProjectAsync();
        Guid id = (await QueryDbAsync(db => db.PriceLevels.SingleAsync())).Id;

        // Flatten the spike: the pivot no longer forms, so the recompute reproduces nothing for it. The stored row
        // must be RETIRED (Active=false), never deleted -- the journal keeps it.
        await ExecuteDbAsync(async db =>
        {
            BarRecord spike = await db.Bars.SingleAsync(bar => bar.Instrument == Symbol && bar.BucketStart == Bucket(SpikeMinute));
            spike.High = 100m;
            await db.SaveChangesAsync();
        });
        await ProjectAsync();

        PriceLevel retired = await QueryDbAsync(db => db.PriceLevels.SingleAsync());
        retired.Id.Should().Be(id, "the row is retired in place, never deleted -- the journal keeps it");
        retired.Active.Should().BeFalse();

        // The read surface every chart overlay and confluence scan consults must exclude it.
        IReadOnlyList<PriceLevel> active = await ActiveLevelsAsync(VenueKey, Symbol, [1]);
        active.Should().BeEmpty(
            "IPriceLevelSource.GetActiveLevelsAsync must exclude a retired row -- otherwise a level the market no "
            + "longer respects keeps citing itself into confluence and chart overlays forever");
    }

    // -----------------------------------------------------------------------------------------------------------
    // ATR reuse end-to-end: gh#311's real projection feeds gh#597's real zone width, per series
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectAsync_ShouldSizeEachSeriesZone_ByItsOwnAtr_ReadBackFromTheRealIndicatorProjection()
    {
        // Two series, same pivot shape, DIFFERENT spike magnitudes -- so each has its own genuinely different true
        // range and, once the REAL gh#311 AtrIndicator smooths it, its own genuinely different ATR. Neither ATR is
        // hand-fed: both are read back from IndicatorValues after the real IndicatorProjectionService ran, so a
        // defect that reused one series' ATR for the other, or fell back to a fixed/stale value, shows up as the
        // two zone widths failing to track their own series' ATR.
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 150m, atr: null);       // small true range -> small ATR
        await SeedFlatWithSpikeAsync(OtherSymbol, spikeHigh: 400m, atr: null);  // large true range -> large ATR

        await ProjectIndicatorsAsync();
        await ProjectAsync();

        decimal smallAtr = await AtrAtBucketAsync(Symbol, SpikeMinute);
        decimal largeAtr = await AtrAtBucketAsync(OtherSymbol, SpikeMinute);
        largeAtr.Should().BeGreaterThan(smallAtr, "the bigger spike must produce a genuinely bigger real ATR");

        PriceLevel smallZone = await QueryDbAsync(db => db.PriceLevels.SingleAsync(level => level.Instrument == Symbol));
        PriceLevel largeZone = await QueryDbAsync(db => db.PriceLevels.SingleAsync(level => level.Instrument == OtherSymbol));

        decimal smallWidth = smallZone.Top - smallZone.Bottom;
        decimal largeWidth = largeZone.Top - largeZone.Bottom;
        largeWidth.Should().BeGreaterThan(smallWidth,
            "the zone sized off the larger real ATR must come out wider than the one sized off the smaller real "
            + "ATR -- proving the width was actually driven by EACH series' own reused ATR, not a shared value");
    }

    [Fact]
    public async Task ProjectAsync_ShouldWriteNoZones_WhenTheSeriesHasNoAtr_NoPartialWrite()
    {
        // No ATR at the pivot's own bucket (the indicator projection never ran for this series) => the pivot is
        // skipped by KeyLevels.Detect => no zone => no row. A partial write here would be a level the detector
        // could never actually have measured.
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 200m, atr: null);

        int written = await ProjectAsync();

        written.Should().Be(0);
        (await QueryDbAsync(db => db.PriceLevels.CountAsync())).Should().Be(0);
    }

    // -----------------------------------------------------------------------------------------------------------
    // Per-series isolation
    // -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectAsync_ShouldIsolateACorruptSeries_FromASiblingAlreadyCommittedToRealPostgres()
    {
        // A negative ATR makes KeyLevels.Detect throw mid-pass for its own series (ZoneFor refuses a negative ATR).
        // The service saves once PER series, so the healthy series' SaveChangesAsync is a real, durable commit to
        // Postgres before the corrupt series ever runs -- only a real backend's commit semantics can prove that
        // commit survives the later series' fault and the ChangeTracker.Clear() that follows it, rather than both
        // series sharing one transaction an InMemory context has no equivalent for.
        await ClearAsync();
        await SeedFlatWithSpikeAsync(Symbol, spikeHigh: 200m, atr: 3m);
        await SeedFlatWithSpikeAsync(OtherSymbol, spikeHigh: 200m, atr: -5m);

        await ProjectAsync();

        (await QueryDbAsync(db => db.PriceLevels.CountAsync(level => level.Instrument == Symbol && level.Active)))
            .Should().Be(1, "the healthy series must keep its committed level despite the sibling's fault");
        (await QueryDbAsync(db => db.PriceLevels.CountAsync(level => level.Instrument == OtherSymbol)))
            .Should().Be(0, "the corrupt series must leave nothing behind -- no partial row from before the fault");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private async Task<int> ProjectAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        KeyLevelProjectionService projection = scope.ServiceProvider.GetRequiredService<KeyLevelProjectionService>();
        return await projection.ProjectAsync(CancellationToken.None);
    }

    private async Task<int> ProjectIndicatorsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IndicatorProjectionService projection = scope.ServiceProvider.GetRequiredService<IndicatorProjectionService>();
        return await projection.ProjectAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<PriceLevel>> ActiveLevelsAsync(string venue, string instrument, IReadOnlyCollection<int> timeframeMinutes)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IPriceLevelSource source = scope.ServiceProvider.GetRequiredService<IPriceLevelSource>();
        return await source.GetActiveLevelsAsync(venue, instrument, timeframeMinutes, CancellationToken.None);
    }

    private async Task<decimal> AtrAtBucketAsync(string instrument, int minute) =>
        await QueryDbAsync(db => db.IndicatorValues
            .Where(value => value.Instrument == instrument
                && value.Indicator == AtrIndicator.IndicatorName
                && value.Period == AtrPeriod
                && value.BucketStart == Bucket(minute))
            .Select(value => value.Value)
            .SingleAsync());

    /// <summary>
    /// A flat 60-bar, 1-minute series (o = h = l = c = 100) with one high spike at <see cref="SpikeMinute"/> — the
    /// smallest shape that forms exactly one confirmed pivot high under the shipped 20/15 Heikin-Ashi window. When
    /// <paramref name="atr"/> is supplied it is written directly to <c>IndicatorValues</c> at the pivot's own
    /// bucket (an adversarial, hand-fed ATR); when it is <see langword="null"/> no ATR row is seeded at all, so the
    /// real chain (<see cref="ProjectIndicatorsAsync"/>) or the "no ATR" control is exercised instead.
    /// </summary>
    private Task SeedFlatWithSpikeAsync(string instrument, decimal spikeHigh, decimal? atr) =>
        ExecuteDbAsync(async db =>
        {
            for (int minute = 0; minute < BarCount; minute++)
            {
                bool spike = minute == SpikeMinute;
                db.Bars.Add(new BarRecord
                {
                    Venue = VenueKey,
                    Instrument = instrument,
                    ResolutionMinutes = 1,
                    BucketStart = Bucket(minute),
                    Open = 100m,
                    High = spike ? spikeHigh : 100m,
                    Low = 100m,
                    Close = 100m,
                    Volume = 100,
                    RecordedAt = Origin,
                });
            }

            if (atr is not null)
            {
                db.IndicatorValues.Add(AtrRow(instrument, SpikeMinute, atr.Value));
            }

            await db.SaveChangesAsync();
        });

    private static IndicatorValueRecord AtrRow(string instrument, int minute, decimal value) => new()
    {
        Venue = VenueKey,
        Instrument = instrument,
        ResolutionMinutes = 1,
        Indicator = AtrIndicator.IndicatorName,
        Period = AtrPeriod,
        BucketStart = Bucket(minute),
        Value = value,
        RecordedAt = Origin,
    };

    private static PriceLevel NewLevel(
        decimal top = 110m,
        decimal bottom = 100m,
        PriceLevelKind kind = PriceLevelKind.Resistance,
        int timeframeMinutes = 1) => new()
        {
            Id = Guid.NewGuid(),
            Venue = VenueKey,
            Instrument = Symbol,
            TimeframeMinutes = timeframeMinutes,
            Top = top,
            Bottom = bottom,
            Kind = kind,
            Significance = 1m,
            FormedAtBucket = Origin,
            TouchCount = 1,
            Active = true,
            UpdatedAt = Origin,
        };

    /// <summary>Inserts <paramref name="row"/> in its own scope and asserts the save fails -- proof the named CHECK is live.</summary>
    private async Task AssertViolatesCheckAsync(PriceLevel row, string because)
    {
        Func<Task> insert = () => ExecuteDbAsync(async db =>
        {
            db.PriceLevels.Add(row);
            await db.SaveChangesAsync();
        });

        await insert.Should().ThrowAsync<DbUpdateException>(because);
    }

    private Task ClearAsync() => ExecuteDbAsync(async db =>
    {
        await db.PriceLevels.ExecuteDeleteAsync();
        await db.IndicatorValues.ExecuteDeleteAsync();
        await db.Bars.ExecuteDeleteAsync();
    });

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(database);
    }
}
