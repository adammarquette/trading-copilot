using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    /// <summary>The indicator name stored for average true range (aliases <see cref="AtrIndicator.IndicatorName"/>).</summary>
    public const string Atr = AtrIndicator.IndicatorName;

    private readonly TradingCopilotDbContext _database;
    private readonly IReadOnlyList<IIndicator> _indicators;
    private readonly ILogger<IndicatorProjectionService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="indicators">The bar-derived indicators to project — each carries its own name and period.</param>
    /// <param name="logger">The logger.</param>
    public IndicatorProjectionService(
        TradingCopilotDbContext database,
        IReadOnlyList<IIndicator> indicators,
        ILogger<IndicatorProjectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(indicators);

        _database = database;
        _indicators = indicators;
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

        DateTimeOffset computedAt = DateTimeOffset.UtcNow;
        int written = 0;

        // Bars are loaded once; every configured indicator projects over the same series into its own
        // (Indicator, Period) family of rows. A third indicator later is one more entry in the collection.
        foreach (IIndicator indicator in _indicators)
        {
            written += await UpsertSeriesAsync(
                key, indicator.Name, indicator.Period, indicator.Compute(bars), bars, computedAt, cancellationToken);
        }

        if (written > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        return written;
    }

    private async Task<int> UpsertSeriesAsync(
        SeriesKey key,
        string indicator,
        int period,
        IReadOnlyList<decimal?> values,
        IReadOnlyList<Bar> bars,
        DateTimeOffset computedAt,
        CancellationToken cancellationToken)
    {
        Dictionary<DateTimeOffset, IndicatorValueRecord> existing = await _database.IndicatorValues
            .Where(value => value.Venue == key.Venue
                && value.Instrument == key.Instrument
                && value.ResolutionMinutes == key.ResolutionMinutes
                && value.Indicator == indicator
                && value.Period == period)
            .ToDictionaryAsync(value => value.BucketStart, cancellationToken);

        int written = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            if (values[i] is not decimal value)
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
                Indicator = indicator,
                Period = period,
                BucketStart = bucket,
                Value = value,
                RecordedAt = computedAt,
            });
            written++;
        }

        return written;
    }

    private sealed record SeriesKey(string Venue, string Instrument, int ResolutionMinutes);
}
