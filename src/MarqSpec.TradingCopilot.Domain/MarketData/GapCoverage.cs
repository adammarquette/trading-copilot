namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// How much of a blind window the clean-historical bar store could actually cover (gh#306, R-1, ADR-0001) — and,
/// just as importantly, how much it could <b>not</b>.
/// </summary>
/// <remarks>
/// <para>
/// gh#227 replaced a silent retention hole with a loud signal and stopped there, handing recovery to R-1's
/// gap-detection-and-backfill path. This type is the honesty of that recovery: a backfill that covered half a
/// window and reported "recovered" would be <b>worse than the silence it replaced</b>, because it would be
/// believed. So coverage is measured against what was asked for, and the shortfall is carried alongside it rather
/// than rounded away.
/// </para>
/// <para>
/// <b>What it deliberately does not measure:</b> an interior hole. Coverage runs from the first bucket to the
/// end of the last, so a bar missing in the middle is not counted. Bars arrive from a single periodic REST poll
/// over a contiguous lookback window (gh#302), which makes an interior hole a venue omission rather than the
/// ordinary partial-coverage case this measures. Stated here so it is a known bound rather than a surprise.
/// </para>
/// </remarks>
/// <param name="RequestedFrom">The start of the window that needed covering.</param>
/// <param name="RequestedTo">The end of the window that needed covering.</param>
/// <param name="CoveredFrom">Where cover actually begins, or <see langword="null"/> when nothing was covered.</param>
/// <param name="CoveredTo">Where cover actually ends, or <see langword="null"/> when nothing was covered.</param>
public readonly record struct GapCoverage(
    DateTimeOffset RequestedFrom,
    DateTimeOffset RequestedTo,
    DateTimeOffset? CoveredFrom,
    DateTimeOffset? CoveredTo)
{
    /// <summary>How much of the requested window no bar covers. Never negative.</summary>
    public TimeSpan Shortfall
    {
        get
        {
            TimeSpan requested = RequestedTo - RequestedFrom;
            if (requested <= TimeSpan.Zero)
            {
                // An inverted or empty window asks for nothing, so nothing can be missing. Guarded because a
                // negative shortfall would read as "over-recovered", which is not a state that exists.
                return TimeSpan.Zero;
            }

            return CoveredFrom is null || CoveredTo is null
                ? requested
                : CoveredFrom.Value - RequestedFrom + (RequestedTo - CoveredTo.Value);
        }
    }

    /// <summary>Whether the whole requested window was covered.</summary>
    public bool IsComplete => Shortfall <= TimeSpan.Zero;

    /// <summary>
    /// Measures what <paramref name="bucketStarts"/> cover of the window <paramref name="from"/> →
    /// <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The start of the window that needed covering.</param>
    /// <param name="to">The end of the window that needed covering.</param>
    /// <param name="bucketStarts">The opening times of the bars found in the store; may be empty or unordered.</param>
    /// <param name="barSize">The bar size — the last bucket covers up to its start plus this.</param>
    /// <returns>The coverage, including any shortfall.</returns>
    public static GapCoverage Of(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<DateTimeOffset> bucketStarts,
        TimeSpan barSize)
    {
        ArgumentNullException.ThrowIfNull(bucketStarts);

        if (bucketStarts.Count == 0 || to <= from)
        {
            return new GapCoverage(from, to, null, null);
        }

        DateTimeOffset first = bucketStarts.Min();
        DateTimeOffset last = bucketStarts.Max() + barSize;

        // Clamped to the window on both ends: the store legitimately holds bars either side of it, and crediting
        // those would let a wide store mask a window it did not actually cover.
        DateTimeOffset coveredFrom = first < from ? from : first;
        DateTimeOffset coveredTo = last > to ? to : last;

        return coveredTo <= coveredFrom
            ? new GapCoverage(from, to, null, null) // every bar sat outside the window: nothing covered
            : new GapCoverage(from, to, coveredFrom, coveredTo);
    }
}
