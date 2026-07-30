using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// Reads the deep tier's <see cref="ReviewEnrichment"/> from the projection stores (gh#476, R-4 / ADR-0008): a short
/// window of recent OHLCV bars and recent values of the fired indicator's own series, as of the fire.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="MarketData.StoredIndicatorSource"/>: it reads the <b>global</b> bar and indicator-value stores
/// (derived market data sits on the shared side of R-20's line, so no tenant filter applies; and, like that seam, it
/// reads <b>venue-agnostically</b> over the covering <c>(instrument, resolution, …, bucket)</c> index — a
/// single-venue-per-instrument assumption; two venues feeding the same instrument/resolution would interleave the
/// window, exactly as they would for the execution-path indicator read) at or <b>before</b>
/// <see cref="TriggerReviewContext.FiredAt"/> — never after. Taking the most-recent rows descending and reversing to
/// oldest-first gives the deep tier a chronological, <b>bar-close-aligned</b> window on the same cutoff the fired
/// reading itself came through — so the enrichment is consistent with the decision. On a live fire that is strictly
/// no-look-ahead; on a <b>replay</b> the boundary bucket's completed values reflect its full bar (finalized on close,
/// exactly as the fired indicator value is), not tick-exact knowledge at the instant of the fire.
/// </para>
/// <para>
/// Bounded by a constant, not config: the window is a fixed <see cref="RecentBarCount"/> / <see cref="RecentIndicatorValueCount"/>
/// so the deep prompt's input size is predictable and the added spend is capped by construction (the escalated deep
/// call is already the expensive tier). Returns <see langword="null"/> when both series are empty, so the caller
/// passes an un-enriched context rather than an empty block.
/// </para>
/// </remarks>
public sealed class ReviewEnrichmentSource : IReviewEnrichmentSource
{
    // Fixed windows, deliberately not configurable: a predictable deep-prompt size and a spend cap by construction.
    private const int RecentBarCount = 20;
    private const int RecentIndicatorValueCount = 20;

    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the source over the scoped database.</summary>
    /// <param name="database">The scoped database (global reads; no tenant scope needed for shared market data).</param>
    public ReviewEnrichmentSource(TradingCopilotDbContext database) => _database = database;

    /// <inheritdoc />
    public async Task<ReviewEnrichment?> BuildAsync(TriggerReviewContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string symbol = context.Instrument.ToString();

        // Most-recent-first AS OF the fire, capped, then reversed to oldest-first for a chronological read. The
        // `BucketStart <= FiredAt` cutoff is the no-look-ahead guarantee -- the same honesty StoredIndicatorSource keeps.
        List<BarSnapshot> bars = await _database.Bars
            .Where(bar => bar.Instrument == symbol
                && bar.ResolutionMinutes == context.ResolutionMinutes
                && bar.BucketStart <= context.FiredAt)
            .OrderByDescending(bar => bar.BucketStart)
            .Take(RecentBarCount)
            .Select(bar => new BarSnapshot(bar.BucketStart, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume))
            .ToListAsync(cancellationToken);
        bars.Reverse();

        List<IndicatorValueSnapshot> indicatorValues = await _database.IndicatorValues
            .Where(value => value.Instrument == symbol
                && value.ResolutionMinutes == context.ResolutionMinutes
                && value.Indicator == context.Indicator
                && value.Period == context.Period
                && value.BucketStart <= context.FiredAt)
            .OrderByDescending(value => value.BucketStart)
            .Take(RecentIndicatorValueCount)
            .Select(value => new IndicatorValueSnapshot(value.BucketStart, value.Value))
            .ToListAsync(cancellationToken);
        indicatorValues.Reverse();

        // Nothing to add -> null, so the caller sends an un-enriched deep call rather than an empty <market-context> block.
        if (bars.Count == 0 && indicatorValues.Count == 0)
        {
            return null;
        }

        return new ReviewEnrichment(bars, indicatorValues);
    }
}
