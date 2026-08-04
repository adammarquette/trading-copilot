using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The read-only market-data surface (gh#644, R-10 / R-22): bars, indicator series and price levels over HTTP. The
/// behaviours that matter — a bounded window is refused (never truncated) when too wide, an unknown series is a 404
/// (not an empty 200), the keys are matched after normalisation (so a lower-case venue / mixed-case symbol still
/// resolves), and the data is global (no owner filter, R-22).
/// </summary>
public class MarketDataEndpointsTests
{
    private const string Venue = "projectx";   // VenueId normalises to lower-case
    private const string Instrument = "ES";    // InstrumentId normalises to UPPER-case
    private const int Resolution = 1;
    private static readonly DateTimeOffset _t0 = DateTimeOffset.UnixEpoch.AddYears(56);

    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
        new FixedUser(Guid.NewGuid()));

    private static IOptions<MarketDataReadOptions> Options(int maxRows = 5000) =>
        Microsoft.Extensions.Options.Options.Create(new MarketDataReadOptions { MaxRows = maxRows });

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;
    private static T ValueOf<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    private async Task SeedBarsAsync(int count, DateTimeOffset start, string venue = Venue, string instrument = Instrument, int resolution = Resolution)
    {
        await using TradingCopilotDbContext context = Context();
        for (int i = 0; i < count; i++)
        {
            context.Bars.Add(new BarRecord
            {
                Venue = venue,
                Instrument = instrument,
                ResolutionMinutes = resolution,
                BucketStart = start.AddMinutes(i * resolution),
                Open = 5300m + i,
                High = 5305m + i,
                Low = 5295m + i,
                Close = 5302m + i,
                Volume = 100 + i,
                RecordedAt = start,
            });
        }

        await context.SaveChangesAsync();
    }

    // ---- bars ----

    [Fact]
    public async Task GetBars_ShouldReturnTheWindowAscending_ForAKnownSeries()
    {
        await SeedBarsAsync(3, _t0);

        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, Instrument, Resolution, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        BarSeriesResponse series = ValueOf<BarSeriesResponse>(result);
        series.Venue.Should().Be(Venue);
        series.Instrument.Should().Be(Instrument);
        series.ResolutionMinutes.Should().Be(Resolution);
        series.Bars.Should().HaveCount(3);
        series.Bars.Should().BeInAscendingOrder(bar => bar.BucketStart);
        series.Bars[0].Open.Should().Be(5300m);
    }

    [Fact]
    public async Task GetBars_ShouldReturn404_ForAnUnknownSeries()
    {
        await SeedBarsAsync(3, _t0); // a DIFFERENT instrument exists...

        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, "CL", Resolution, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound, "a wrong symbol must not look like an empty chart");
    }

    [Fact]
    public async Task GetBars_ShouldReturnAnEmpty200_ForAKnownSeriesWithNoBarsInTheWindow()
    {
        await SeedBarsAsync(3, _t0); // bars exist at _t0..

        // ..but the window is a year later — the series is known, it is simply empty here.
        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, Instrument, Resolution, from: _t0.AddYears(1), to: _t0.AddYears(1).AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        ValueOf<BarSeriesResponse>(result).Bars.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBars_ShouldRefuseAnOverWideWindow_RatherThanTruncate()
    {
        await SeedBarsAsync(4, _t0); // 4 bars in the window, cap is 3

        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, Instrument, Resolution, from: _t0, to: _t0.AddHours(1), Context(), Options(maxRows: 3), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest, "an over-wide window is refused, never silently truncated");
    }

    [Fact]
    public async Task GetBars_ShouldMatchAfterNormalisation_WhenTheQueryCasingDiffers()
    {
        await SeedBarsAsync(2, _t0); // stored as venue "projectx" / instrument "ES"

        IResult result = await MarketDataEndpoints.GetBarsAsync(
            "ProjectX", "es", Resolution, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK, "the stored keys are normalised, so the query must be too");
        BarSeriesResponse series = ValueOf<BarSeriesResponse>(result);
        series.Bars.Should().HaveCount(2);
        series.Venue.Should().Be(Venue);      // the canonical, normalised key is echoed back
        series.Instrument.Should().Be(Instrument);
    }

    [Fact]
    public async Task GetBars_ShouldBadRequest_ForAnEmptyWindow()
    {
        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, Instrument, Resolution, from: _t0.AddHours(1), to: _t0, Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetBars_ShouldBadRequest_ForABlankInstrument()
    {
        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, "  ", Resolution, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetBars_ShouldBadRequest_ForANonPositiveResolution()
    {
        IResult result = await MarketDataEndpoints.GetBarsAsync(
            Venue, Instrument, resolution: 0, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ---- indicators ----

    private async Task SeedIndicatorAsync(int count, string indicator = "atr", int period = 14)
    {
        await using TradingCopilotDbContext context = Context();
        for (int i = 0; i < count; i++)
        {
            context.IndicatorValues.Add(new IndicatorValueRecord
            {
                Venue = Venue,
                Instrument = Instrument,
                ResolutionMinutes = Resolution,
                Indicator = indicator,
                Period = period,
                BucketStart = _t0.AddMinutes(i),
                Value = 1.25m + i,
                RecordedAt = _t0,
            });
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetIndicators_ShouldReturnTheStoredSeries()
    {
        await SeedBarsAsync(3, _t0);        // the series is known
        await SeedIndicatorAsync(3);       // and its ATR is computed

        IResult result = await MarketDataEndpoints.GetIndicatorsAsync(
            Venue, Instrument, Resolution, "atr", period: 14, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        IndicatorSeriesResponse series = ValueOf<IndicatorSeriesResponse>(result);
        series.Indicator.Should().Be("atr");
        series.Period.Should().Be(14);
        series.Points.Should().HaveCount(3);
        series.Points.Should().BeInAscendingOrder(point => point.BucketStart);
    }

    [Fact]
    public async Task GetIndicators_ShouldMatchCaseInsensitively_OnTheIndicatorName()
    {
        await SeedBarsAsync(1, _t0);
        await SeedIndicatorAsync(2, indicator: "rsi");

        IResult result = await MarketDataEndpoints.GetIndicatorsAsync(
            Venue, Instrument, Resolution, "RSI", period: 14, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        ValueOf<IndicatorSeriesResponse>(result).Points.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetIndicators_ShouldBadRequest_ForAnUnknownIndicator()
    {
        await SeedBarsAsync(1, _t0);

        IResult result = await MarketDataEndpoints.GetIndicatorsAsync(
            Venue, Instrument, Resolution, "macd", period: 14, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest, "the indicator vocabulary is closed (atr, rsi)");
    }

    [Fact]
    public async Task GetIndicators_ShouldReturn404_ForAnUnknownSeries()
    {
        // No bars for this instrument at all -> the series is unknown, so an empty indicator read is a 404.
        IResult result = await MarketDataEndpoints.GetIndicatorsAsync(
            Venue, Instrument, Resolution, "atr", period: 14, from: _t0, to: _t0.AddHours(1), Context(), Options(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ---- levels ----

    private async Task SeedLevelAsync(int timeframeMinutes = 5, bool active = true, string instrument = Instrument)
    {
        await using TradingCopilotDbContext context = Context();
        context.PriceLevels.Add(new PriceLevel
        {
            Id = Guid.NewGuid(),
            Venue = Venue,
            Instrument = instrument,
            TimeframeMinutes = timeframeMinutes,
            Top = 5310m,
            Bottom = 5305m,
            Kind = PriceLevelKind.Resistance,
            Significance = 0.8m,
            FormedAtBucket = _t0,
            TouchCount = 3,
            Active = active,
            UpdatedAt = _t0,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetLevels_ShouldReturnActiveLevels_ForTheRequestedTimeframes()
    {
        await SeedBarsAsync(1, _t0);
        await SeedLevelAsync(timeframeMinutes: 5);

        IResult result = await MarketDataEndpoints.GetLevelsAsync(
            Venue, Instrument, timeframes: [5], new StoredPriceLevelSource(Context()), Context(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        PriceLevelsResponse response = ValueOf<PriceLevelsResponse>(result);
        response.Levels.Should().HaveCount(1);
        response.Levels[0].TimeframeMinutes.Should().Be(5);
        response.Levels[0].Kind.Should().Be("Resistance");
    }

    [Fact]
    public async Task GetLevels_ShouldBadRequest_WhenNoTimeframeIsGiven()
    {
        IResult result = await MarketDataEndpoints.GetLevelsAsync(
            Venue, Instrument, timeframes: [], new StoredPriceLevelSource(Context()), Context(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetLevels_ShouldReturn404_ForAnUnknownInstrument()
    {
        // No bars and no levels for this instrument -> unknown to the market-data store.
        IResult result = await MarketDataEndpoints.GetLevelsAsync(
            Venue, "CL", timeframes: [5], new StoredPriceLevelSource(Context()), Context(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetLevels_ShouldReturnAnEmpty200_ForAKnownInstrumentWithNoLevels()
    {
        await SeedBarsAsync(1, _t0); // the instrument is known (has bars) but no levels formed

        IResult result = await MarketDataEndpoints.GetLevelsAsync(
            Venue, Instrument, timeframes: [5], new StoredPriceLevelSource(Context()), Context(), CancellationToken.None);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        ValueOf<PriceLevelsResponse>(result).Levels.Should().BeEmpty();
    }
}
