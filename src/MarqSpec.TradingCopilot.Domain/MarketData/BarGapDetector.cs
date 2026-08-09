namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// Finds the gaps in a stored bar series (gh#696, R-1): the expected-but-absent bucket starts in a window,
/// where "expected" is keyed to the bar size <b>and</b> the session calendar — a maintenance window, a
/// weekend, or a declared holiday is not a gap.
/// </summary>
/// <remarks>
/// <para>
/// Detection is a pure read over the durable tables (the issue's "health = holes in the history"): given what
/// the store holds, enumerate every bucket the calendar says should exist in the window and report the absent
/// ones. No cursor position is trusted — a volatile ingestion position lost across a restart is precisely what
/// this must not depend on.
/// </para>
/// <para>
/// A bucket is only counted as expected when it <b>closes inside the window</b>: one closing past the window
/// end may still be forming, and reporting it missing would race the venue's own production of it — the same
/// still-forming guard the periodic backfill applies to writes.
/// </para>
/// </remarks>
public static class BarGapDetector
{
    /// <summary>
    /// The expected-but-absent bucket starts in the window <paramref name="from"/> → <paramref name="to"/>.
    /// </summary>
    /// <param name="storedBuckets">The bucket starts the store holds; may be unordered and may extend outside the window.</param>
    /// <param name="from">The start of the window to evaluate.</param>
    /// <param name="to">The end of the window to evaluate.</param>
    /// <param name="barSize">The bar duration — the enumeration cadence.</param>
    /// <param name="calendar">The session calendar that decides which buckets are expected at all.</param>
    /// <returns>The missing bucket starts, ascending; empty when the window's series is contiguous.</returns>
    public static IReadOnlyList<DateTimeOffset> MissingBuckets(
        IReadOnlyList<DateTimeOffset> storedBuckets,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan barSize,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(storedBuckets);
        ArgumentNullException.ThrowIfNull(calendar);

        if (to <= from)
        {
            return [];
        }

        HashSet<DateTimeOffset> stored = [.. storedBuckets.Select(bucket => bucket.ToUniversalTime())];
        List<DateTimeOffset> missing = [];

        // Snap the window start onto the bar grid: a bucket opening before `from` is not this window's concern,
        // and starting mid-bucket would invent a bucket opening there.
        long tickSize = barSize.Ticks;
        long startTicks = from.ToUniversalTime().Ticks;
        long aligned = (startTicks / tickSize) * tickSize;
        if (aligned < startTicks)
        {
            aligned += tickSize;
        }

        for (long ticks = aligned; ticks + tickSize <= to.ToUniversalTime().Ticks; ticks += tickSize)
        {
            DateTimeOffset bucket = new(ticks, TimeSpan.Zero);
            if (calendar.IsExpectedSlot(bucket, barSize) && !stored.Contains(bucket))
            {
                missing.Add(bucket);
            }
        }

        return missing;
    }
}
