using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.MarketData;

/// <summary>
/// Integration coverage for the <b>indicator projection</b> and the ATR it feeds the stop band (gh#513 ⇒ gh#310,
/// gh#311, R-1, R-22) against real Timescale.
/// </summary>
/// <remarks>
/// <para>
/// The chain <b>bars → projection → <c>StoredIndicatorSource</c></b> had never run at any tier. Every StopPlan-
/// touching suite seeds a tick-based band, and the only ATR in the integration tier is a literal handed to
/// <c>ResolveDistance</c> — the value has never come from stored bars.
/// </para>
/// <para>
/// That matters because the failure is silent. An ATR the store cannot produce resolves to <see langword="null"/>,
/// the band is unresolvable, and an ATR-banded stop simply never promotes — indistinguishable from "price never
/// came close". These cases make the chain observable, and the projection's own relational reads — a
/// projection-then-<c>Distinct()</c> into a private nested record over <c>Bars</c>, and the composite key that
/// lives in a hand-written migration rather than the EF model — meet Npgsql here for the first time.
/// </para>
/// </remarks>
public class IndicatorProjectionIntegrationTests : IClassFixture<OcoExitTestPostgresFactory>
{
    private readonly OcoExitTestPostgresFactory _factory;

    public IndicatorProjectionIntegrationTests(OcoExitTestPostgresFactory factory)
    {
        _factory = factory;
    }

    private const string VenueKey = "projectx";
    private const string Symbol = "MES";
    private const string OtherSymbol = "MNQ";
    private const int AtrPeriod = 14;

    private static DateTimeOffset Origin { get; } = new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Project_ShouldWriteAtr_ForEverySeriesTheBarStoreHolds()
    {
        // The projection discovers its work by SELECTing a private nested record over Bars and calling Distinct()
        // on it — a shape the unit tier evaluates client-side and so has never translated. Four series here (two
        // instruments across two resolutions) so the Distinct() has something real to collapse and the per-series
        // separation is observable rather than assumed.
        await ClearAsync();
        await SeedBarsAsync(Symbol, resolutionMinutes: 1, count: 40);
        await SeedBarsAsync(Symbol, resolutionMinutes: 5, count: 40);
        await SeedBarsAsync(OtherSymbol, resolutionMinutes: 1, count: 40);
        await SeedBarsAsync(OtherSymbol, resolutionMinutes: 5, count: 40);

        int written = await ProjectAsync();

        written.Should().BeGreaterThan(0);

        List<IndicatorValueRecord> values = await QueryDbAsync(db => db.IndicatorValues
            .Where(value => value.Indicator == AtrIndicator.IndicatorName)
            .ToListAsync());

        values.Select(value => (value.Instrument, value.ResolutionMinutes))
            .Distinct()
            .Should().BeEquivalentTo([(Symbol, 1), (Symbol, 5), (OtherSymbol, 1), (OtherSymbol, 5)],
                "every series the bar store holds must be projected — a Distinct() that failed to translate, or a "
                + "series key that collapsed two instruments together, would show up here as a missing pair");

        values.Where(value => value.Instrument == Symbol && value.ResolutionMinutes == 1)
            .Should().OnlyContain(value => value.Period == AtrPeriod);
    }

    [Fact]
    public async Task Project_ShouldWriteNothingOnASecondPass_WhenTheBarsAreUnchanged()
    {
        // Idempotence over REAL Postgres, which is the only place it can be judged: the stored column is
        // numeric(18,8), so a recomputed value that carries more scale than that round-trips to something the
        // in-memory comparison `row.Value != value` may consider different. If it does, the projection rewrites
        // every indicator row it has ever written on every poll — invisible in the unit tier, where nothing is
        // ever narrowed on the way to storage.
        await ClearAsync();
        await SeedBarsAsync(Symbol, resolutionMinutes: 1, count: 40);

        int first = await ProjectAsync();
        int second = await ProjectAsync();

        first.Should().BeGreaterThan(0, "the first pass writes the series");
        second.Should().Be(0,
            "unchanged bars must produce no writes at all. A non-zero count here means every pass rewrites the "
            + "whole indicator history — at the default sixty-second poll, continuously and forever");
    }

    [Fact]
    public async Task IndicatorValues_ShouldRejectADuplicateBucket_ByTheCompositeKey()
    {
        // The composite key lives in a hand-written migration, and it must include the time dimension because a
        // hypertable's unique constraints have to. Nothing in the unit tier can witness it: EF's in-memory provider
        // enforces the model's key, not the table's.
        await ClearAsync();
        IndicatorValueRecord row = NewIndicatorValue(AtrIndicator.IndicatorName, Origin, 4.5m);
        await ExecuteDbAsync(async db =>
        {
            db.IndicatorValues.Add(row);
            await db.SaveChangesAsync();
        });

        Func<Task> duplicate = () => ExecuteDbAsync(async db =>
        {
            db.IndicatorValues.Add(NewIndicatorValue(AtrIndicator.IndicatorName, Origin, 9.9m));
            await db.SaveChangesAsync();
        });

        await duplicate.Should().ThrowAsync<DbUpdateException>(
            "one series, one indicator, one period, one bucket is one value — a second row for the same bucket "
            + "would make which ATR sets a live stop a matter of read order");

        // A DIFFERENT indicator on the SAME bucket is not a duplicate, and must still be accepted — otherwise the
        // key is too tight and RSI could never share a series with ATR (R-22).
        Func<Task> otherIndicator = () => ExecuteDbAsync(async db =>
        {
            db.IndicatorValues.Add(NewIndicatorValue("rsi", Origin, 55m));
            await db.SaveChangesAsync();
        });

        await otherIndicator.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StoredIndicatorSource_ShouldServeTheProjectedAtr_ToTheExecutionPath()
    {
        // The end of the chain, and the reason the rest matters: the execution path resolves its ATR band through
        // IIndicatorSource. If the projection wrote nothing the source returns null, the band is unresolvable, and
        // an ATR-banded stop never promotes — silently, and identically to "price never came close".
        await ClearAsync();
        await SeedBarsAsync(Symbol, resolutionMinutes: 1, count: 40);
        await ProjectAsync();

        decimal? atr = await AverageTrueRangeAsync(Symbol, resolutionMinutes: 1, asOf: Origin.AddMinutes(40));

        atr.Should().NotBeNull(
            "the execution path must be able to read back what the projection wrote — this is the whole "
            + "bars → projection → StoredIndicatorSource chain, and it had never run end to end");
        atr!.Value.Should().BeGreaterThan(0m, "a zero or negative ATR resolves to no band at all");
    }

    [Fact]
    public async Task StoredIndicatorSource_ShouldReturnNothing_ForASeriesTheProjectionNeverCovered()
    {
        // The control for the case above. Without it, a source that returned some fixed value for everything would
        // satisfy the positive case just as well.
        await ClearAsync();
        await SeedBarsAsync(Symbol, resolutionMinutes: 1, count: 40);
        await ProjectAsync();

        decimal? atr = await AverageTrueRangeAsync(OtherSymbol, resolutionMinutes: 1, asOf: Origin.AddMinutes(40));

        atr.Should().BeNull(
            "an instrument the store holds no bars for has no ATR — and the band must be UNRESOLVABLE rather "
            + "than silently zero, which would put a stop at the entry price");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private async Task<int> ProjectAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IndicatorProjectionService projection = scope.ServiceProvider.GetRequiredService<IndicatorProjectionService>();
        return await projection.ProjectAsync(CancellationToken.None);
    }

    private async Task<decimal?> AverageTrueRangeAsync(string instrument, int resolutionMinutes, DateTimeOffset asOf)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IIndicatorSource source = scope.ServiceProvider.GetRequiredService<IIndicatorSource>();
        return await source.GetAverageTrueRangeAsync(InstrumentId.Parse(instrument), resolutionMinutes, asOf, CancellationToken.None);
    }

    /// <summary>
    /// A rising series with a genuine true range on every bar, so ATR resolves to something greater than zero once
    /// the period is satisfied. Flat bars would produce an ATR of zero, which reads as "no band" and would make
    /// every positive assertion here pass for the wrong reason.
    /// </summary>
    private Task SeedBarsAsync(string instrument, int resolutionMinutes, int count) =>
        ExecuteDbAsync(async db =>
        {
            for (int index = 0; index < count; index++)
            {
                decimal open = 5_000m + (index * 2m);
                db.Bars.Add(new BarRecord
                {
                    Venue = VenueKey,
                    Instrument = instrument,
                    ResolutionMinutes = resolutionMinutes,
                    BucketStart = Origin.AddMinutes(index * resolutionMinutes),
                    Open = open,
                    High = open + 3m,
                    Low = open - 1m,
                    Close = open + 1m,
                    Volume = 100,
                    RecordedAt = Origin,
                });
            }

            await db.SaveChangesAsync();
        });

    private static IndicatorValueRecord NewIndicatorValue(string indicator, DateTimeOffset bucketStart, decimal value) =>
        new()
        {
            Venue = VenueKey,
            Instrument = Symbol,
            ResolutionMinutes = 1,
            Indicator = indicator,
            Period = AtrPeriod,
            BucketStart = bucketStart,
            Value = value,
            RecordedAt = Origin,
        };

    private Task ClearAsync() => ExecuteDbAsync(async db =>
    {
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
