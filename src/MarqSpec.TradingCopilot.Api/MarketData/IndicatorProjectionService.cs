using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Recomputes indicator values over the clean-historical bar store (gh#310, R-1, ADR-0001) — the projection
/// ADR-0001 has always described, now that gh#302 gave it a series to project over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rebuild = replay, literally.</b> Each pass recomputes from the stored bars and upserts, so the derived
/// series is a pure function of the underlying one: a restated bar corrects the values that depended on it, and
/// a rebuild restores history rather than appending a second version of it.
/// </para>
/// <para>
/// The <b>work list comes from the store</b>, not from configuration — whatever instrument × resolution has bars
/// gets indicators. A second symbol list would be a second thing to keep in sync, and its drift would show up as
/// an instrument whose ATR is silently absent: the execution path meets that as "no value", i.e. a stop that
/// never promotes.
/// </para>
/// </remarks>
public sealed class IndicatorProjectionService
{
    /// <summary>The indicator name stored for average true range.</summary>
    public const string Atr = "atr";

    private readonly TradingCopilotDbContext _database;
    private readonly IndicatorOptions _options;
    private readonly ILogger<IndicatorProjectionService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="options">The indicator parameters and cadence.</param>
    /// <param name="logger">The logger.</param>
    public IndicatorProjectionService(
        TradingCopilotDbContext database,
        IOptions<IndicatorOptions> options,
        ILogger<IndicatorProjectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Recomputes every series that has bars, and upserts the results.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many values were written or updated.</returns>
    public async Task<int> ProjectAsync(CancellationToken cancellationToken)
    {
        List<SeriesKey> series = await _database.Bars
            .Select(bar => new SeriesKey(bar.Venue, bar.Instrument, bar.ResolutionMinutes))
            .Distinct()
            .ToListAsync(cancellationToken);

        int written = 0;
        foreach (SeriesKey key in series)
        {
            // One series failing must not cost the others their indicators.
            try
            {
                written += await ProjectSeriesAsync(key, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogError(
                    error, "Indicator projection failed for {Instrument} {Resolution}m; the next pass retries.",
                    key.Instrument, key.ResolutionMinutes);
            }
        }

        return written;
    }

    private async Task<int> ProjectSeriesAsync(SeriesKey key, CancellationToken cancellationToken)
    {
        List<BarRecord> stored = await _database.Bars
            .Where(bar => bar.Venue == key.Venue
                && bar.Instrument == key.Instrument
                && bar.ResolutionMinutes == key.ResolutionMinutes)
            .OrderBy(bar => bar.BucketStart)
            .ToListAsync(cancellationToken);

        // Recompute from the START of the stored series, always. That is what makes the result a pure function
        // of the store: seeding from a moving window would make the value depend on when it happened to run.
        List<Bar> bars =
        [
            .. stored.Select(bar => new Bar(
                bar.BucketStart,
                new Price(bar.Open),
                new Price(bar.High),
                new Price(bar.Low),
                new Price(bar.Close),
                bar.Volume)),
        ];

        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(bars, _options.AtrPeriod);

        Dictionary<DateTimeOffset, IndicatorValueRecord> existing = await _database.IndicatorValues
            .Where(value => value.Venue == key.Venue
                && value.Instrument == key.Instrument
                && value.ResolutionMinutes == key.ResolutionMinutes
                && value.Indicator == Atr
                && value.Period == _options.AtrPeriod)
            .ToDictionaryAsync(value => value.BucketStart, cancellationToken);

        DateTimeOffset computedAt = DateTimeOffset.UtcNow;
        int written = 0;

        for (int i = 0; i < bars.Count; i++)
        {
            if (atr[i] is not decimal value)
            {
                continue; // period not satisfied -- no value is deliberate, and better than a partial one
            }

            DateTimeOffset bucket = bars[i].OpenTime;
            if (existing.TryGetValue(bucket, out IndicatorValueRecord? row))
            {
                if (row.Value != value)
                {
                    row.Value = value;
                    row.RecordedAt = computedAt;
                    written++;
                }

                continue;
            }

            _database.IndicatorValues.Add(new IndicatorValueRecord
            {
                Venue = key.Venue,
                Instrument = key.Instrument,
                ResolutionMinutes = key.ResolutionMinutes,
                Indicator = Atr,
                Period = _options.AtrPeriod,
                BucketStart = bucket,
                Value = value,
                RecordedAt = computedAt,
            });
            written++;
        }

        if (written > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        return written;
    }

    private sealed record SeriesKey(string Venue, string Instrument, int ResolutionMinutes);
}
