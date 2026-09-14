namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// The market's daily <b>settlement / maintenance window</b> (R-13, ADR-0013, gh#193): the CME re-marks any
/// position carried through it at the settlement price, so during it a mark is not a live price. Derived <b>per
/// instrument</b> from its session rules (products settle at different times) via the same DST-aware
/// <see cref="MarketClock"/> the flatten uses — never a hard-coded clock.
/// </summary>
public static class MarketSession
{
    /// <summary>
    /// How long the settlement / maintenance window runs from the session close (the CME's ~1-hour daily halt,
    /// roughly 4:00–5:00 pm CT before the reopen). The exact close is the instrument's own; this is its length.
    /// </summary>
    public static readonly TimeSpan MaintenanceWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the market is in its settlement / maintenance window at <paramref name="now"/> — the interval from
    /// the instrument's <see cref="FlattenSchedule.SessionClose"/> for <see cref="MaintenanceWindow"/>.
    /// </summary>
    /// <param name="schedule">The instrument's session schedule (supplies the session close).</param>
    /// <param name="now">The current instant, in any offset — compared in market wall-clock time.</param>
    /// <returns><see langword="true"/> when a carried position would be settlement-marked, not live-marked.</returns>
    public static bool IsInSettlementWindow(FlattenSchedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        DateTime marketNow = MarketClock.ToMarketTime(now);
        DateTime close = marketNow.Date + schedule.SessionClose.ToTimeSpan();
        return marketNow >= close && marketNow < close + MaintenanceWindow;
    }

    /// <summary>
    /// What a position's mark rests on right now: <see cref="PositionMarkBasis.Unknown"/> when venue truth cannot
    /// be reached (fail-safe — never present a stale number as live), <see cref="PositionMarkBasis.Settlement"/>
    /// inside the maintenance window, else <see cref="PositionMarkBasis.Live"/>.
    /// </summary>
    /// <param name="schedule">The instrument's session schedule.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="venueReachable">Whether the venue's position truth was obtained this reconcile.</param>
    /// <returns>The mark basis.</returns>
    public static PositionMarkBasis MarkBasisAt(FlattenSchedule schedule, DateTimeOffset now, bool venueReachable)
    {
        if (!venueReachable)
        {
            return PositionMarkBasis.Unknown; // truth unobtainable -> declared-unknown, never a stale live-looking mark
        }

        return IsInSettlementWindow(schedule, now) ? PositionMarkBasis.Settlement : PositionMarkBasis.Live;
    }
}
