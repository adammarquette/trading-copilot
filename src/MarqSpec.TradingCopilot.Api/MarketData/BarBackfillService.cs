using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.MarketData;

/// <summary>
/// Fills the clean-historical bar store from the venue's REST history (gh#302, R-1, ADR-0001) — the
/// <b>system of record</b> the live stream deliberately is not.
/// </summary>
/// <remarks>
/// <para>
/// Each pass re-fetches a window wider than the poll interval, so overlapping reads are the normal case rather
/// than an edge one. That is what makes a venue's later <b>revision</b> of a bar land, and a missed pass heal
/// itself — and it is why the write has to be an upsert keyed on the bucket rather than an append.
/// </para>
/// <para>
/// The venue side of this was already built and tested: <c>GetBarsAsync</c> and
/// <see cref="VenueCapability.HistoricalBars"/> have existed since the adapter landed, with no caller anywhere
/// in the repo. This gives them one.
/// </para>
/// </remarks>
public sealed class BarBackfillService
{
    private readonly TradingCopilotDbContext _database;
    private readonly BarBackfillOptions _options;
    private readonly ILogger<BarBackfillService> _logger;

    /// <summary>Creates the service over the scoped database.</summary>
    /// <param name="database">The scoped database.</param>
    /// <param name="options">What to archive, and how far back each pass reaches.</param>
    /// <param name="logger">The logger.</param>
    public BarBackfillService(
        TradingCopilotDbContext database,
        IOptions<BarBackfillOptions> options,
        ILogger<BarBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs one backfill pass over every configured instrument × resolution.
    /// </summary>
    /// <param name="venue">The venue to read history from.</param>
    /// <param name="now">The instant the pass evaluates against.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many bars were written or updated.</returns>
    public async Task<int> BackfillAsync(ITradingVenue venue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);

        if (!_options.IsConfigured)
        {
            return 0; // idle, never "fetch everything"
        }

        int written = 0;
        foreach (string symbol in _options.Instruments)
        {
            foreach (int resolution in _options.ResolutionMinutes)
            {
                // One instrument's failure must not cost the rest of the watchlist its history, so the guard is
                // per pair rather than per pass.
                try
                {
                    written += await BackfillOneAsync(venue, symbol, resolution, now, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (VenueCapabilityNotSupportedException capability)
                {
                    // A configuration truth, not a crash: this venue simply has no history to give (R-17).
                    _logger.LogWarning(
                        capability,
                        "Backfill skipped {Symbol} {Resolution}m — the venue does not provide historical bars.",
                        symbol, resolution);
                }
                catch (Exception error)
                {
                    _logger.LogError(
                        error, "Backfill failed for {Symbol} {Resolution}m; the next pass retries.", symbol, resolution);
                }
            }
        }

        return written;
    }

    private async Task<int> BackfillOneAsync(
        ITradingVenue venue,
        string symbol,
        int resolutionMinutes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = InstrumentId.Parse(symbol);
        ResolvedContract resolved = await venue.ResolveContractAsync(instrument, cancellationToken);
        TimeSpan barSize = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset from = now - TimeSpan.FromMinutes(_options.LookbackMinutes);

        IReadOnlyList<Bar> fetched = await venue.GetBarsAsync(resolved.Contract, from, now, barSize, cancellationToken);

        // Drop anything whose bucket has not closed. The ProjectX adapter already asks for includePartialBar:
        // false, but a half-formed bar stored as final is indistinguishable from data and silently corrupts
        // everything derived from it -- so this does not depend on a venue behaving.
        List<Bar> closed = [.. fetched.Where(bar => bar.OpenTime + barSize <= now)];
        if (closed.Count == 0)
        {
            return 0;
        }

        string venueKey = venue.Id.ToString();
        List<DateTimeOffset> buckets = [.. closed.Select(bar => bar.OpenTime.ToUniversalTime())];

        // Load the overlap once, then merge in memory. EF-first per the coding contract; the window is a couple
        // of hundred rows at most, so a round trip per bar would be the wrong trade.
        Dictionary<DateTimeOffset, BarRecord> existing = await _database.Bars
            .Where(bar => bar.Venue == venueKey
                && bar.Instrument == symbol
                && bar.ResolutionMinutes == resolutionMinutes
                && buckets.Contains(bar.BucketStart))
            .ToDictionaryAsync(bar => bar.BucketStart, cancellationToken);

        foreach (Bar bar in closed)
        {
            DateTimeOffset bucket = bar.OpenTime.ToUniversalTime();
            if (existing.TryGetValue(bucket, out BarRecord? stored))
            {
                // A revision: the later value wins, in place. Appending would leave two rows claiming the same
                // minute and make every aggregate over them wrong.
                stored.Open = bar.Open.Value;
                stored.High = bar.High.Value;
                stored.Low = bar.Low.Value;
                stored.Close = bar.Close.Value;
                stored.Volume = bar.Volume;
                stored.RecordedAt = now;
                continue;
            }

            _database.Bars.Add(new BarRecord
            {
                Venue = venueKey,
                Instrument = symbol,
                ResolutionMinutes = resolutionMinutes,
                BucketStart = bucket,
                Open = bar.Open.Value,
                High = bar.High.Value,
                Low = bar.Low.Value,
                Close = bar.Close.Value,
                Volume = bar.Volume,
                RecordedAt = now,
            });
        }

        await _database.SaveChangesAsync(cancellationToken);
        return closed.Count;
    }
}
