using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Flatten;

/// <summary>
/// <see cref="MarketClock"/> — the market's own wall-clock (US Central). The safety-relevant property extracted here
/// (gh#741): the daily reset boundary the governor, the throttle and the auto-flatten share is Central-calendar-day
/// midnight in UTC, and it tracks DST rather than a fixed offset — a UTC date would split a live CME session.
/// </summary>
public class MarketClockTests
{
    // 2026-07-15 03:00Z is 2026-07-14 22:00 CDT (UTC-5): the Central day is the 14th, whose midnight is 05:00Z.
    [Fact]
    public void CentralDayStartUtc_ShouldReturnCentralMidnightInUtc_InSummer()
    {
        DateTimeOffset start = MarketClock.CentralDayStartUtc(new DateTimeOffset(2026, 7, 15, 3, 0, 0, TimeSpan.Zero));

        start.Should().Be(new DateTimeOffset(2026, 7, 14, 5, 0, 0, TimeSpan.Zero));
    }

    // 2026-01-15 03:00Z is 2026-01-14 21:00 CST (UTC-6): the Central day is the 14th, whose midnight is 06:00Z.
    // The one-hour difference from the summer case is the whole point — the boundary is wall-clock, not an offset.
    [Fact]
    public void CentralDayStartUtc_ShouldReturnCentralMidnightInUtc_InWinter()
    {
        DateTimeOffset start = MarketClock.CentralDayStartUtc(new DateTimeOffset(2026, 1, 15, 3, 0, 0, TimeSpan.Zero));

        start.Should().Be(new DateTimeOffset(2026, 1, 14, 6, 0, 0, TimeSpan.Zero));
    }

    // A Central-evening instant still resolves to that same Central day's midnight, not the next — the UTC date has
    // already rolled over (22:00 CDT on the 14th is 03:00Z on the 15th) but the Central day has not.
    [Fact]
    public void CentralDayStartUtc_ShouldUseTheCentralDay_NotTheUtcDate()
    {
        DateTimeOffset lateCentralEvening = new(2026, 7, 15, 3, 0, 0, TimeSpan.Zero); // 22:00 CDT on the 14th

        MarketClock.CentralDayStartUtc(lateCentralEvening).Should().Be(new DateTimeOffset(2026, 7, 14, 5, 0, 0, TimeSpan.Zero));
    }
}
