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
/// Recomputes key-level zones over the clean-historical bar store (gh#597, R-10 / R-22, ADR-0001) and reconciles
/// them into the <see cref="PriceLevel"/> table (gh#596) — the persisting half of key-level detection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rebuild = replay.</b> Each pass recomputes every series' levels from the stored bars via
/// <see cref="KeyLevels.Detect"/> and reconciles the result against the stored rows, so the level set is a pure
/// function of the underlying bars: a restated bar corrects the levels that depended on it, and a rebuild restores
/// history rather than appending a second copy of it.
/// </para>
/// <para>
/// The <b>work list comes from the store</b> — whatever instrument × resolution has bars gets levels — exactly as
/// <see cref="IndicatorProjectionService"/> does, and the ATR each zone is sized by is <b>read back</b> from the
/// indicator projection (gh#311), never recomputed here.
/// </para>
/// <para>
/// Reconciliation is keyed on <see cref="KeyLevelZone.FormedAtBucket"/>: a matched level is updated in place (so a
/// role reversal is a <see cref="PriceLevel.Kind"/> flip, and a merge a <see cref="PriceLevel.TouchCount"/> raise,
/// on the same row), a newly-formed level is inserted, and a stored level the recompute no longer produces is
/// retired (<see cref="PriceLevel.Active"/> = false) — never deleted, so the journal keeps it. The band and score
/// are quantised to the column's <c>numeric(18,8)</c> scale before both the change-compare and the write, or an
/// un-quantised significance would rewrite every row every pass.
/// </para>
/// </remarks>
public sealed class KeyLevelProjectionService
{
    private readonly TradingCopilotDbContext _database;
    private readonly KeyLevelDetectorOptions _options;
    private readonly IndicatorOptions _indicatorOptions;
    private readonly ILogger<KeyLevelProjectionService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="options">The host's own knobs — cap per side and cadence.</param>
    /// <param name="indicatorOptions">The indicator options, read for the ATR period the zones are sized by.</param>
    /// <param name="logger">The logger.</param>
    public KeyLevelProjectionService(
        TradingCopilotDbContext database,
        IOptions<KeyLevelDetectorOptions> options,
        IOptions<IndicatorOptions> indicatorOptions,
        ILogger<KeyLevelProjectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(indicatorOptions);

        _database = database;
        _options = options.Value;
        _indicatorOptions = indicatorOptions.Value;
        _logger = logger;
    }

    /// <summary>Recomputes every series that has bars, and reconciles the result into the level store.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many rows were inserted, updated or retired.</returns>
    public async Task<int> ProjectAsync(CancellationToken cancellationToken)
    {
        List<SeriesKey> series = await _database.Bars
            .Select(bar => new SeriesKey(bar.Venue, bar.Instrument, bar.ResolutionMinutes))
            .Distinct()
            .ToListAsync(cancellationToken);

        // One timestamp per pass stamps every UpdatedAt written below.
        DateTimeOffset computedAt = DateTimeOffset.UtcNow;

        int written = 0;
        foreach (SeriesKey key in series)
        {
            // One series failing must not cost the others their levels.
            try
            {
                written += await ProjectSeriesAsync(key, computedAt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogError(
                    error, "Key-level projection failed for {Instrument} {Resolution}m; the next pass retries.",
                    key.Instrument, key.ResolutionMinutes);

                // Abandon the failed series atomically: the save is once-per-series, so any rows staged before the
                // fault would otherwise be flushed by the NEXT series' commit -- a partial series leaking into an
                // unrelated one. Clearing discards them; rebuild = replay recomputes next pass.
                _database.ChangeTracker.Clear();
            }
        }

        return written;
    }

    private async Task<int> ProjectSeriesAsync(SeriesKey key, DateTimeOffset computedAt, CancellationToken cancellationToken)
    {
        List<BarRecord> stored = await _database.Bars
            .Where(bar => bar.Venue == key.Venue
                && bar.Instrument == key.Instrument
                && bar.ResolutionMinutes == key.ResolutionMinutes)
            .OrderBy(bar => bar.BucketStart)
            .ToListAsync(cancellationToken);

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

        // Reuse gh#311's stored ATR at THIS series' own resolution -- never recompute, and never the band
        // resolution, which is a different series entirely. A pivot whose bucket has no ATR forms no zone.
        Dictionary<DateTimeOffset, decimal> atrByBucket = await _database.IndicatorValues
            .Where(value => value.Venue == key.Venue
                && value.Instrument == key.Instrument
                && value.ResolutionMinutes == key.ResolutionMinutes
                && value.Indicator == AtrIndicator.IndicatorName
                && value.Period == _indicatorOptions.AtrPeriod)
            .ToDictionaryAsync(value => value.BucketStart, value => value.Value, cancellationToken);

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            bars, atrByBucket, KeyLevelOptions.Default, _options.MaxLevelsPerKind);

        List<PriceLevel> active = await _database.PriceLevels
            .Where(level => level.Venue == key.Venue
                && level.Instrument == key.Instrument
                && level.TimeframeMinutes == key.ResolutionMinutes
                && level.Active)
            .ToListAsync(cancellationToken);

        int written = Reconcile(key, zones, active, computedAt);
        if (written > 0)
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        return written;
    }

    /// <summary>Folds the recomputed zones onto the stored-active rows: update in place, insert, or retire.</summary>
    private int Reconcile(
        SeriesKey key, IReadOnlyList<KeyLevelZone> zones, List<PriceLevel> active, DateTimeOffset computedAt)
    {
        // Stored-active rows grouped by formation bucket, in mutable lists so a match removes the row from contention.
        Dictionary<DateTimeOffset, List<PriceLevel>> byBucket = active
            .GroupBy(level => level.FormedAtBucket)
            .ToDictionary(group => group.Key, group => group.ToList());

        HashSet<PriceLevel> matched = [];
        int written = 0;

        foreach (KeyLevelZone zone in zones)
        {
            // Quantise to the column scale BEFORE both the compare and the write: significance = prominence / ATR
            // can carry more than eight places, and comparing that against the truncated stored value would rewrite
            // every row every pass forever.
            decimal bottom = Quantise(zone.Bottom);
            decimal top = Quantise(zone.Top);
            decimal significance = Quantise(zone.Significance);
            PriceLevelKind kind = (PriceLevelKind)(int)zone.Kind;

            if (byBucket.TryGetValue(zone.FormedAtBucket, out List<PriceLevel>? candidates) && candidates.Count > 0)
            {
                // Identity is the formation bucket. Two zones sharing one bucket is rare -- an outside bar that is
                // both a pivot high and a pivot low -- so when it happens, bind to the nearest stored Bottom, and the
                // pair stays paired across passes rather than swapping roles.
                PriceLevel chosen = candidates.MinBy(level => Math.Abs(level.Bottom - bottom))!;
                candidates.Remove(chosen);
                matched.Add(chosen);

                // Update in place ONLY when a persisted field changed, so a steady state writes nothing (idempotence).
                if (chosen.Top != top
                    || chosen.Bottom != bottom
                    || chosen.Kind != kind
                    || chosen.Significance != significance
                    || chosen.TouchCount != zone.TouchCount)
                {
                    chosen.Top = top;
                    chosen.Bottom = bottom;
                    chosen.Kind = kind;
                    chosen.Significance = significance;
                    chosen.TouchCount = zone.TouchCount;
                    chosen.UpdatedAt = computedAt;
                    written++;
                }

                continue;
            }

            _database.PriceLevels.Add(new PriceLevel
            {
                Id = Guid.NewGuid(),
                Venue = key.Venue,
                Instrument = key.Instrument,
                TimeframeMinutes = key.ResolutionMinutes,
                Top = top,
                Bottom = bottom,
                Kind = kind,
                Significance = significance,
                FormedAtBucket = zone.FormedAtBucket,
                TouchCount = zone.TouchCount,
                Active = true,
                UpdatedAt = computedAt,
            });
            written++;
        }

        // Any stored-active row the recompute did not reproduce is retired -- kept for the journal, read out by default.
        foreach (PriceLevel level in active)
        {
            if (!matched.Contains(level))
            {
                level.Active = false;
                level.UpdatedAt = computedAt;
                written++;
            }
        }

        return written;
    }

    /// <summary>Rounds a price or score to the <c>PriceLevel</c> column scale of eight, away from zero.</summary>
    private static decimal Quantise(decimal value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

    private sealed record SeriesKey(string Venue, string Instrument, int ResolutionMinutes);
}
