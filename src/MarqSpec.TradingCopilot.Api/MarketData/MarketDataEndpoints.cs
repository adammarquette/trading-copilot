using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// The read-only market-data surface (gh#644, R-10 / R-22): bars, indicator series and price levels over HTTP, for
/// the chart (ADR-0004) and any client overlay. It is a thin read over data that <b>already exists</b> — the bar
/// store (gh#155), the <c>IndicatorValue</c> projection (gh#310/#372) and the <see cref="IPriceLevelSource"/> — with
/// no new ingestion, projection or recompute: R-22's single seam means one reader, so the browser consumes the exact
/// number the suggestion engine and stop-promotion band do, never its own.
/// </summary>
/// <remarks>
/// <b>These routes are authenticated (R-18) but deliberately carry NO owner filter.</b> Derived market data is
/// <b>global / shared</b> (R-22), not <see cref="Data.Tenancy.IUserOwned"/>, so the repo's usual R-20 per-operator
/// scoping does not apply here — stated explicitly because the default is the opposite and a future reader would
/// otherwise assume an omission. Every windowed read is <b>bounded</b>: it carries a window and is capped at
/// <see cref="MarketDataReadOptions.MaxRows"/>, refusing (never truncating) a wider one. An unknown
/// venue/instrument/resolution is a <b>404</b>, never an empty <c>200</c> — a wrong symbol and an empty chart must
/// not look identical.
/// </remarks>
public static class MarketDataEndpoints
{
    /// <summary>Maps the read-only market-data routes under <c>/api/marketdata</c>.</summary>
    public static IEndpointRouteBuilder MapMarketDataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/marketdata")
            .RequireAuthorization()
            .WithTags("Market data");

        group.MapGet("/bars", GetBarsAsync)
            .WithSummary("OHLCV bars for a venue + instrument + resolution over a bounded time window (ascending).");
        group.MapGet("/indicators", GetIndicatorsAsync)
            .WithSummary("A stored indicator series (e.g. atr, rsi) over a bounded time window — never recomputed (R-22).");
        group.MapGet("/levels", GetLevelsAsync)
            .WithSummary("Active price levels for a venue + instrument across the requested timeframes.");

        return endpoints;
    }

    /// <summary>The indicators this surface can serve — the ones the projection actually computes (gh#310/#372).</summary>
    private static readonly HashSet<string> _knownIndicators =
        new(StringComparer.Ordinal) { AtrIndicator.IndicatorName, RsiIndicator.IndicatorName };

    internal static async Task<IResult> GetBarsAsync(
        string venue,
        string instrument,
        int resolution,
        DateTimeOffset from,
        DateTimeOffset to,
        TradingCopilotDbContext database,
        IOptions<MarketDataReadOptions> options,
        CancellationToken cancellationToken)
    {
        if (!TryKey(venue, instrument, resolution, from, to, out string venueKey, out string instrumentKey, out IResult? bad))
        {
            return bad!; // TryKey sets a non-null refusal whenever it returns false
        }

        int cap = options.Value.MaxRows;
        List<BarRecord> bars = await database.Bars
            .AsNoTracking()
            .Where(bar => bar.Venue == venueKey && bar.Instrument == instrumentKey
                && bar.ResolutionMinutes == resolution
                && bar.BucketStart >= from && bar.BucketStart < to)
            .OrderBy(bar => bar.BucketStart)
            .Take(cap + 1)
            .ToListAsync(cancellationToken);

        if (bars.Count > cap)
        {
            return WindowTooWide(cap);
        }

        if (bars.Count == 0)
        {
            // Empty window: distinguish an unknown series (404) from a known one with no bars in the window (200).
            bool known = await database.Bars.AnyAsync(
                bar => bar.Venue == venueKey && bar.Instrument == instrumentKey && bar.ResolutionMinutes == resolution,
                cancellationToken);
            if (!known)
            {
                return NotFoundSeries(venue, instrument, resolution);
            }
        }

        IReadOnlyList<BarPoint> points = bars
            .Select(bar => new BarPoint(bar.BucketStart, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume))
            .ToList();
        return Results.Ok(new BarSeriesResponse(venueKey, instrumentKey, resolution, points));
    }

    internal static async Task<IResult> GetIndicatorsAsync(
        string venue,
        string instrument,
        int resolution,
        string indicator,
        int period,
        DateTimeOffset from,
        DateTimeOffset to,
        TradingCopilotDbContext database,
        IOptions<MarketDataReadOptions> options,
        CancellationToken cancellationToken)
    {
        if (!TryKey(venue, instrument, resolution, from, to, out string venueKey, out string instrumentKey, out IResult? bad))
        {
            return bad!; // TryKey sets a non-null refusal whenever it returns false
        }

        if (period <= 0)
        {
            return Results.BadRequest(new { error = "period must be a positive integer." });
        }

        // The indicator name is a small closed vocabulary (the projection computes exactly these); an unknown one is a
        // client mistake worth naming, not an empty series. Stored lower-case, so match case-insensitively.
        string indicatorKey = (indicator ?? string.Empty).Trim().ToLowerInvariant();
        if (!_knownIndicators.Contains(indicatorKey))
        {
            return Results.BadRequest(new
            {
                error = $"Unknown indicator '{indicator}'. Known indicators: {string.Join(", ", _knownIndicators.Order())}.",
            });
        }

        int cap = options.Value.MaxRows;
        List<IndicatorValueRecord> values = await database.IndicatorValues
            .AsNoTracking()
            .Where(value => value.Venue == venueKey && value.Instrument == instrumentKey
                && value.ResolutionMinutes == resolution
                && value.Indicator == indicatorKey && value.Period == period
                && value.BucketStart >= from && value.BucketStart < to)
            .OrderBy(value => value.BucketStart)
            .Take(cap + 1)
            .ToListAsync(cancellationToken);

        if (values.Count > cap)
        {
            return WindowTooWide(cap);
        }

        if (values.Count == 0)
        {
            // "Known series" is anchored on the bar store (the canonical presence of an instrument at a resolution),
            // so a wrong symbol/resolution 404s while a known one whose indicator simply is not computed yet 200s empty.
            bool known = await database.Bars.AnyAsync(
                bar => bar.Venue == venueKey && bar.Instrument == instrumentKey && bar.ResolutionMinutes == resolution,
                cancellationToken);
            if (!known)
            {
                return NotFoundSeries(venue, instrument, resolution);
            }
        }

        IReadOnlyList<IndicatorPoint> points = values
            .Select(value => new IndicatorPoint(value.BucketStart, value.Value))
            .ToList();
        return Results.Ok(new IndicatorSeriesResponse(venueKey, instrumentKey, resolution, indicatorKey, period, points));
    }

    internal static async Task<IResult> GetLevelsAsync(
        string venue,
        string instrument,
        int[] timeframes,
        IPriceLevelSource priceLevels,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!VenueId.TryParse(venue, out VenueId venueId))
        {
            return Results.BadRequest(new { error = "venue is required and must be a valid venue id." });
        }

        if (!InstrumentId.TryParse(instrument, out InstrumentId instrumentId))
        {
            return Results.BadRequest(new { error = "instrument is required and must be a valid instrument symbol." });
        }

        if (timeframes is null || timeframes.Length == 0 || timeframes.Any(timeframe => timeframe <= 0))
        {
            return Results.BadRequest(new { error = "specify at least one positive timeframe (minutes), e.g. ?timeframes=1&timeframes=5." });
        }

        string venueKey = venueId.ToString();
        string instrumentKey = instrumentId.ToString();

        IReadOnlyList<PriceLevel> levels = await priceLevels.GetActiveLevelsAsync(
            venueKey, instrumentKey, timeframes, cancellationToken);

        if (levels.Count == 0)
        {
            // No active levels: 404 only if the instrument is unknown to the market-data store at all (no bars);
            // a known instrument with no levels formed yet is a legitimate empty 200.
            bool known = await database.Bars.AnyAsync(
                bar => bar.Venue == venueKey && bar.Instrument == instrumentKey, cancellationToken);
            if (!known)
            {
                return Results.NotFound(new
                {
                    error = $"No market data for venue '{venueKey}' instrument '{instrumentKey}'.",
                });
            }
        }

        IReadOnlyList<PriceLevelResponse> projected = levels
            .Select(level => new PriceLevelResponse(
                level.TimeframeMinutes, level.Top, level.Bottom, level.Kind.ToString(),
                level.Significance, level.FormedAtBucket, level.TouchCount))
            .ToList();
        return Results.Ok(new PriceLevelsResponse(venueKey, instrumentKey, projected));
    }

    /// <summary>
    /// Parses + normalises the shared (venue, instrument, resolution, window) key. The stored keys are normalised
    /// domain primitives (the writers key with <c>VenueId.ToString()</c> / <c>InstrumentId.ToString()</c>), so a raw
    /// query string would not match — parse to the primitive, then use its canonical form.
    /// </summary>
    private static bool TryKey(
        string venue, string instrument, int resolution, DateTimeOffset from, DateTimeOffset to,
        out string venueKey, out string instrumentKey, out IResult? bad)
    {
        venueKey = string.Empty;
        instrumentKey = string.Empty;

        if (!VenueId.TryParse(venue, out VenueId venueId))
        {
            bad = Results.BadRequest(new { error = "venue is required and must be a valid venue id." });
            return false;
        }

        if (!InstrumentId.TryParse(instrument, out InstrumentId instrumentId))
        {
            bad = Results.BadRequest(new { error = "instrument is required and must be a valid instrument symbol." });
            return false;
        }

        if (resolution <= 0)
        {
            bad = Results.BadRequest(new { error = "resolution must be a positive number of minutes." });
            return false;
        }

        if (from >= to)
        {
            bad = Results.BadRequest(new { error = "the time window is empty: 'from' must be strictly before 'to'." });
            return false;
        }

        venueKey = venueId.ToString();
        instrumentKey = instrumentId.ToString();
        bad = null;
        return true;
    }

    private static IResult WindowTooWide(int cap) => Results.BadRequest(new
    {
        error = $"the requested window returns more than {cap} rows; narrow the window or use a coarser resolution.",
    });

    private static IResult NotFoundSeries(string venue, string instrument, int resolution) => Results.NotFound(new
    {
        error = $"No market data for venue '{venue}' instrument '{instrument}' at resolution {resolution}m.",
    });
}

/// <summary>One OHLCV bar (gh#644). The series it belongs to is tagged once on <see cref="BarSeriesResponse"/>.</summary>
public sealed record BarPoint(
    DateTimeOffset BucketStart, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>A window of OHLCV bars for one venue + instrument + resolution (gh#644), ascending by bucket.</summary>
public sealed record BarSeriesResponse(
    string Venue, string Instrument, int ResolutionMinutes, IReadOnlyList<BarPoint> Bars);

/// <summary>One stored indicator value at a bucket (gh#644).</summary>
public sealed record IndicatorPoint(DateTimeOffset BucketStart, decimal Value);

/// <summary>A window of a stored indicator series for one venue + instrument + resolution + indicator + period (gh#644).</summary>
public sealed record IndicatorSeriesResponse(
    string Venue, string Instrument, int ResolutionMinutes, string Indicator, int Period, IReadOnlyList<IndicatorPoint> Points);

/// <summary>One active price level for the chart's price lines (gh#644).</summary>
public sealed record PriceLevelResponse(
    int TimeframeMinutes, decimal Top, decimal Bottom, string Kind, decimal Significance,
    DateTimeOffset FormedAtBucket, int TouchCount);

/// <summary>The active price levels for one venue + instrument across the requested timeframes (gh#644).</summary>
public sealed record PriceLevelsResponse(string Venue, string Instrument, IReadOnlyList<PriceLevelResponse> Levels);
