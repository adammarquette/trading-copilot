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
/// The restart heal pass for the clean-historical bar store (gh#696, R-1): on ingestion start, recovery reads
/// the <b>durable tables</b> rather than trusting a volatile ingestion position — interior holes are backfilled
/// from the venue's retained history and the tail resumes forward, bounded by the configured window and
/// idempotent by construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <see cref="GapBackfillService"/></b> (gh#306): that one recovers <i>consumers</i> of the
/// event log from a retention gap, measuring coverage with <see cref="GapCoverage"/>. Here the missing history
/// <b>is</b> state to rebuild in the store itself — the hub's event-log cursor is durable and presentation-only,
/// so it needs no equivalent of this pass.
/// </para>
/// <para>
/// Health is <b>holes in the history, not a cursor position</b>: <see cref="BarGapDetector"/> enumerates the
/// expected-but-absent buckets against the <see cref="BarSessionCalendar"/>, so a maintenance window, a weekend,
/// or a declared holiday never counts as a gap. The detector also short-circuits the fetch — a contiguous series
/// costs one read, no venue round trips.
/// </para>
/// <para>
/// <b>Residual honesty.</b> After the upsert, detection runs again: whatever is still missing is reported, not
/// silenced. A heal that claimed success over a hole the venue's retention no longer covers would repeat the
/// exact failure gh#227's loud signal replaced — the quiet one that gets believed.
/// </para>
/// </remarks>
public sealed class BarStoreHealService
{
    /// <summary>
    /// The ProjectX adapter caps one history fetch at 1000 bars (<c>ProjectXVenue.GetBarsAsync</c>), so a heal
    /// window wider than that must be walked in pages or the excess is silently truncated — which is precisely
    /// the kind of partial recovery this service exists to prevent.
    /// </summary>
    private const int VenuePageSizeBars = 1000;

    private readonly TradingCopilotDbContext _database;
    private readonly BarBackfillOptions _options;
    private readonly ILogger<BarStoreHealService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="options">What to heal, the session calendar, and how far back the pass reaches.</param>
    /// <param name="logger">The logger.</param>
    public BarStoreHealService(
        TradingCopilotDbContext database,
        IOptions<BarBackfillOptions> options,
        ILogger<BarStoreHealService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs one heal pass over every configured instrument × resolution: anchors on the durable store, fills
    /// detected interior holes and resumes forward from the latest stored bucket.
    /// </summary>
    /// <param name="venue">The venue to read retained history from.</param>
    /// <param name="now">The instant the pass evaluates against.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Per-series results, including what could not be healed — the honest residual.</returns>
    public async Task<BarStoreHealOutcome> HealAsync(
        ITradingVenue venue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (!_options.IsConfigured)
        {
            return new BarStoreHealOutcome([]); // idle, never "heal everything"
        }

        BarSessionCalendar calendar = BarSessionCalendar.Parse(_options.SessionClose, _options.SessionHolidays);
        List<BarStoreHealSeriesOutcome> series = [];

        foreach (string symbol in _options.Instruments)
        {
            foreach (int resolution in _options.ResolutionMinutes)
            {
                series.Add(await HealOneAsync(venue, symbol, resolution, now, calendar, cancellationToken));
            }
        }

        return new BarStoreHealOutcome(series);
    }

    private async Task<BarStoreHealSeriesOutcome> HealOneAsync(
        ITradingVenue venue,
        string symbol,
        int resolutionMinutes,
        DateTimeOffset now,
        BarSessionCalendar calendar,
        CancellationToken cancellationToken)
    {
        // Keyed exactly like the periodic pass (gh#311): the NORMALIZED instrument, never the raw config string.
        InstrumentId instrument = InstrumentId.Parse(symbol);
        string venueKey = venue.Id.ToString();
        string instrumentKey = instrument.ToString();
        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);

        try
        {
            List<DateTimeOffset> stored = await _database.Bars
                .Where(bar => bar.Venue == venueKey
                    && bar.Instrument == instrumentKey
                    && bar.ResolutionMinutes == resolutionMinutes)
                .Select(bar => bar.BucketStart)
                .ToListAsync(cancellationToken);

            if (stored.Count == 0)
            {
                // No anchor: a fresh series is seeded by the periodic lookback pass, never by a heal that would
                // have to fetch "everything" — the same opt-in idiom the backfill itself was written around.
                return new BarStoreHealSeriesOutcome(instrumentKey, resolutionMinutes, 0, [], Failed: false);
            }

            DateTimeOffset from = stored.Min();
            DateTimeOffset windowFloor = now - TimeSpan.FromMinutes(_options.MaxHealWindowMinutes);
            if (from < windowFloor)
            {
                from = windowFloor; // bounded by venue retention, not by how far the store reaches
            }

            IReadOnlyList<DateTimeOffset> missing = BarGapDetector.MissingBuckets(stored, from, now, barSize, calendar);
            if (missing.Count == 0)
            {
                return new BarStoreHealSeriesOutcome(instrumentKey, resolutionMinutes, 0, [], Failed: false);
            }

            int written = await FetchAndUpsertAsync(
                venue, venueKey, instrumentKey, resolutionMinutes, barSize, from, now, cancellationToken);

            // Detect again AFTER the write: the residual is what still cannot be covered — retention, a venue
            // omission — and it is reported rather than rounded away.
            List<DateTimeOffset> healed = await _database.Bars
                .Where(bar => bar.Venue == venueKey
                    && bar.Instrument == instrumentKey
                    && bar.ResolutionMinutes == resolutionMinutes
                    && bar.BucketStart >= from && bar.BucketStart < now)
                .Select(bar => bar.BucketStart)
                .ToListAsync(cancellationToken);

            IReadOnlyList<DateTimeOffset> residual = BarGapDetector.MissingBuckets(healed, from, now, barSize, calendar);
            if (residual.Count > 0)
            {
                _logger.LogWarning(
                    "Heal left {Count} uncovered bucket(s) for {Instrument} {Resolution}m between {From} and {To} "
                    + "— likely beyond the venue's retention, or a venue omission.",
                    residual.Count, instrumentKey, resolutionMinutes, from, now);
            }

            _logger.LogInformation(
                "Heal pass for {Instrument} {Resolution}m: {Written} bar(s) written, {Residual} residual gap(s).",
                instrumentKey, resolutionMinutes, written, residual.Count);

            return new BarStoreHealSeriesOutcome(instrumentKey, resolutionMinutes, written, residual, Failed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (VenueCapabilityNotSupportedException capability)
        {
            // A configuration truth, not a crash: this venue has no history to heal from (R-17).
            _logger.LogWarning(
                capability,
                "Heal skipped {Symbol} {Resolution}m — the venue does not provide historical bars.",
                symbol, resolutionMinutes);
            return new BarStoreHealSeriesOutcome(instrumentKey, resolutionMinutes, 0, [], Failed: true);
        }
        catch (Exception error)
        {
            // One series' failure must not cost the rest: the next pass retries, and the poll loop covers new
            // data regardless. Failed (not silent) — completeness must not be claimed for a series never fetched.
            _logger.LogError(error, "Heal failed for {Symbol} {Resolution}m.", symbol, resolutionMinutes);
            return new BarStoreHealSeriesOutcome(instrumentKey, resolutionMinutes, 0, [], Failed: true);
        }
    }

    /// <summary>
    /// Walks the heal window in venue-page-sized chunks and upserts every closed bar — the same merge semantics
    /// as the periodic pass: revisions win in place, the bucket is the key, and a still-forming bar is never
    /// stored as final.
    /// </summary>
    private async Task<int> FetchAndUpsertAsync(
        ITradingVenue venue,
        string venueKey,
        string instrumentKey,
        int resolutionMinutes,
        TimeSpan barSize,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ResolvedContract resolved = await venue.ResolveContractAsync(
            InstrumentId.Parse(instrumentKey), cancellationToken);

        TimeSpan chunk = TimeSpan.FromTicks(VenuePageSizeBars * barSize.Ticks);
        int written = 0;

        for (DateTimeOffset chunkFrom = from; chunkFrom < to; chunkFrom += chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset chunkTo = chunkFrom + chunk;
            if (chunkTo > to)
            {
                chunkTo = to;
            }

            IReadOnlyList<Bar> fetched = await venue.GetBarsAsync(
                resolved.Contract, chunkFrom, chunkTo, barSize, cancellationToken);

            // Drop anything whose bucket has not closed — the same guard as the periodic pass, kept here rather
            // than trusted to the venue (includePartialBar: false is a request, not a contract).
            List<Bar> closed = [.. fetched.Where(bar => bar.OpenTime + barSize <= to)];
            if (closed.Count == 0)
            {
                continue;
            }

            List<DateTimeOffset> buckets = [.. closed.Select(bar => bar.OpenTime.ToUniversalTime())];

            Dictionary<DateTimeOffset, BarRecord> existing = await _database.Bars
                .Where(bar => bar.Venue == venueKey
                    && bar.Instrument == instrumentKey
                    && bar.ResolutionMinutes == resolutionMinutes
                    && buckets.Contains(bar.BucketStart))
                .ToDictionaryAsync(bar => bar.BucketStart, cancellationToken);

            foreach (Bar bar in closed)
            {
                DateTimeOffset bucket = bar.OpenTime.ToUniversalTime();
                if (existing.TryGetValue(bucket, out BarRecord? stored))
                {
                    // A revision: the later value wins, in place — appending would leave two rows claiming the
                    // same bucket and make every aggregate over them wrong.
                    stored.Open = bar.Open.Value;
                    stored.High = bar.High.Value;
                    stored.Low = bar.Low.Value;
                    stored.Close = bar.Close.Value;
                    stored.Volume = bar.Volume;
                    stored.RecordedAt = to;
                    written++;
                    continue;
                }

                _database.Bars.Add(new BarRecord
                {
                    Venue = venueKey,
                    Instrument = instrumentKey,
                    ResolutionMinutes = resolutionMinutes,
                    BucketStart = bucket,
                    Open = bar.Open.Value,
                    High = bar.High.Value,
                    Low = bar.Low.Value,
                    Close = bar.Close.Value,
                    Volume = bar.Volume,
                    RecordedAt = to,
                });
                written++;
            }
        }

        if (_database.ChangeTracker.HasChanges())
        {
            await _database.SaveChangesAsync(cancellationToken);
        }

        return written;
    }
}

/// <summary>What one heal pass recovered for one stored series, and what it could not (gh#696).</summary>
/// <param name="Instrument">The venue-neutral instrument the series is keyed on.</param>
/// <param name="ResolutionMinutes">The bar size of the series.</param>
/// <param name="Written">How many bars the pass wrote or updated.</param>
/// <param name="ResidualMissing">
/// The expected buckets still absent after the pass — the honest shortfall. Empty when the series healed to
/// contiguous (within the window the pass evaluates).
/// </param>
/// <param name="Failed">Whether the series could not be fetched at all; completeness is never claimed for it.</param>
public sealed record BarStoreHealSeriesOutcome(
    string Instrument,
    int ResolutionMinutes,
    int Written,
    IReadOnlyList<DateTimeOffset> ResidualMissing,
    bool Failed);

/// <summary>The outcome of one startup heal pass across every configured series (gh#696).</summary>
/// <param name="Series">The per-series outcomes.</param>
public sealed record BarStoreHealOutcome(IReadOnlyList<BarStoreHealSeriesOutcome> Series)
{
    /// <summary>
    /// Whether every series healed without shortfall. <b>False is the answer that matters</b> — a partial heal
    /// reported as complete would repeat the quiet failure this pass exists to remove.
    /// </summary>
    public bool IsComplete => Series.All(series => !series.Failed && series.ResidualMissing.Count == 0);
}
