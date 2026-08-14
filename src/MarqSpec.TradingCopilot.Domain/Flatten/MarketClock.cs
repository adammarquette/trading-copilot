namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// The market's own clock. Every session time in this domain — closes, settlements, the flatten deadline — is a
/// <b>wall-clock time in US Central</b>, the CME's timezone, not a fixed UTC offset.
/// </summary>
/// <remarks>
/// The distinction is load-bearing. Pinning a deadline to an offset makes it drift by an hour twice a year, and a
/// flatten that drifts an hour lands after the close it exists to beat.
/// </remarks>
public static class MarketClock
{
    /// <summary>US Central time, with daylight saving applied.</summary>
    public static TimeZoneInfo CentralTime { get; } = ResolveCentral();

    /// <summary>Converts an instant to the market's wall-clock time.</summary>
    /// <param name="instant">Any instant, in any offset.</param>
    /// <returns>The corresponding Central wall-clock time.</returns>
    public static DateTime ToMarketTime(DateTimeOffset instant)
    {
        return TimeZoneInfo.ConvertTime(instant, CentralTime).DateTime;
    }

    /// <summary>
    /// The UTC instant at which the market's Central <b>calendar day</b> containing <paramref name="instant"/> began —
    /// central midnight, converted to UTC. The daily AI-spend governor, the R-5 suggestion throttle and the
    /// auto-flatten all reset on this boundary (a UTC date would split a live CME session), so the definition lives
    /// here, once, rather than in each of them (gh#741).
    /// </summary>
    /// <param name="instant">Any instant, in any offset.</param>
    /// <returns>Central-day midnight for that instant, in UTC.</returns>
    public static DateTimeOffset CentralDayStartUtc(DateTimeOffset instant)
    {
        // Midnight is never inside the DST spring-forward gap, so no invalid-local-time guard is needed.
        DateTime centralMidnight = DateTime.SpecifyKind(ToMarketTime(instant).Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(centralMidnight, CentralTime.GetUtcOffset(centralMidnight)).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveCentral()
    {
        // .NET resolves IANA ids on Windows too, but fall back to the Windows id rather than fail at startup on
        // a host without ICU.
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }
}
