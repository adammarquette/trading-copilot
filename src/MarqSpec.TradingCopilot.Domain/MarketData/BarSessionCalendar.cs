using System.Globalization;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// The session calendar behind bar gap-detection (gh#696, R-1): decides whether a bucket start is one the
/// venue is expected to produce a bar for, so a session boundary, a maintenance window, a weekend, or a
/// declared holiday is never reported as a hole in the stored series.
/// </summary>
/// <remarks>
/// <para>
/// The model mirrors the flatten path's session logic rather than inventing a second calendar: times are
/// <b>wall-clock US Central</b> via <see cref="MarketClock"/> (the CME's timezone), the daily maintenance
/// window is <see cref="MarketSession.MaintenanceWindow"/> from the session close, and the next session
/// reopens at close + maintenance. CME equity index runs Sunday-evening → Friday-close, so Saturday carries
/// no session, Sunday buckets before the reopen belong to the weekend gap, and Sunday-evening buckets belong
/// to Monday's session. A declared holiday closes its <b>own</b> session outright — and suppresses the
/// Sunday-evening session that would belong to it — but its evening reopen still belongs to the next day's
/// session and opens when that day trades.
/// </para>
/// <para>
/// <b>Known bound:</b> a bucket longer than the remaining session (one that would straddle a close or a
/// midnight boundary) is reported not-expected, which is what the venue's <c>includePartialBar: false</c>
/// produces; sub-hour resolutions never approach that bound.
/// </para>
/// </remarks>
public sealed class BarSessionCalendar
{
    private readonly TimeOnly _sessionClose;
    private readonly HashSet<DateOnly> _holidays;

    /// <summary>Creates the calendar for a product's session.</summary>
    /// <param name="sessionClose">The product's daily session close, in market (Central) wall-clock time.</param>
    /// <param name="holidays">Declared non-trading days; empty when none are declared.</param>
    public BarSessionCalendar(TimeOnly sessionClose, IReadOnlyCollection<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(holidays);

        _sessionClose = sessionClose;
        _holidays = [.. holidays];
    }

    /// <summary>The product's daily session close, in market (Central) wall-clock time.</summary>
    public TimeOnly SessionClose => _sessionClose;

    /// <summary>
    /// Parses an operator-facing calendar — a session close in <c>HH:mm</c> Central time and holidays in
    /// <c>yyyy-MM-dd</c> — failing loudly on a malformed value rather than guessing on a path that decides
    /// what counts as missing data.
    /// </summary>
    /// <param name="sessionClose">The session close, <c>HH:mm</c> market time.</param>
    /// <param name="holidays">The declared holidays, each <c>yyyy-MM-dd</c>.</param>
    /// <returns>The calendar.</returns>
    /// <exception cref="FormatException">A value is not in the expected shape.</exception>
    public static BarSessionCalendar Parse(string sessionClose, IReadOnlyCollection<string> holidays)
    {
        ArgumentNullException.ThrowIfNull(sessionClose);
        ArgumentNullException.ThrowIfNull(holidays);

        if (!TimeOnly.TryParseExact(sessionClose.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly close))
        {
            throw new FormatException($"Backfill 'SessionClose' must be a 24-hour HH:mm market (Central) time, not '{sessionClose}'.");
        }

        List<DateOnly> parsed = [];
        foreach (string holiday in holidays)
        {
            if (!DateOnly.TryParseExact(holiday.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
            {
                throw new FormatException($"Backfill 'SessionHolidays' entries must be yyyy-MM-dd, not '{holiday}'.");
            }

            parsed.Add(day);
        }

        return new BarSessionCalendar(close, parsed);
    }

    /// <summary>
    /// Whether the venue is expected to produce a bar for the bucket opening at <paramref name="bucketStart"/>
    /// — the bucket must lie entirely inside a trading session.
    /// </summary>
    /// <param name="bucketStart">When the bucket opens, in any offset — judged in market wall-clock time.</param>
    /// <param name="barSize">The bucket's duration.</param>
    /// <returns><see langword="true"/> when an absent bar for this bucket is a genuine gap.</returns>
    public bool IsExpectedSlot(DateTimeOffset bucketStart, TimeSpan barSize)
    {
        DateTime marketStart = MarketClock.ToMarketTime(bucketStart);
        DateTime marketEnd = MarketClock.ToMarketTime(bucketStart + barSize);
        DateOnly day = DateOnly.FromDateTime(marketStart);

        if (day.DayOfWeek == DayOfWeek.Saturday)
        {
            return false;
        }

        TimeSpan close = _sessionClose.ToTimeSpan();
        TimeSpan reopen = close + MarketSession.MaintenanceWindow;
        TimeSpan startTime = marketStart.TimeOfDay;

        if (day.DayOfWeek == DayOfWeek.Sunday)
        {
            // The weekend runs Friday close -> Sunday reopen; a Sunday-evening session belongs to Monday, so a
            // holiday Monday suppresses it too.
            return startTime >= reopen && !_holidays.Contains(day.AddDays(1));
        }

        if (_holidays.Contains(day))
        {
            // A declared holiday closes its OWN session -- the daytime buckets and the maintenance window are
            // non-session. But the evening reopen belongs to the NEXT day's session (CME equity index trades
            // Thursday-evening-into-Friday after a Thursday holiday), so it opens exactly when that day trades --
            // the same "!holidays.Contains(tomorrow)" shape the Sunday and Friday paths apply. Suppressing the
            // whole day here would report zero gaps over buckets the venue really produces, and the heal would
            // never fill them.
            return startTime >= reopen && day.DayOfWeek != DayOfWeek.Friday && !_holidays.Contains(day.AddDays(1));
        }

        if (startTime < close)
        {
            // Daytime: the bucket must close by the session close -- one straddling it would be a partial bar
            // the venue does not produce.
            return marketEnd.Date == marketStart.Date && marketEnd.TimeOfDay <= close;
        }

        if (startTime < reopen)
        {
            return false; // the daily maintenance window
        }

        // After the reopen: the NEXT session, except on Friday (the weekend follows) or ahead of a holiday.
        return day.DayOfWeek != DayOfWeek.Friday && !_holidays.Contains(day.AddDays(1));
    }
}
