using MarqSpec.TradingCopilot.Domain.Triggers;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Triggers;

/// <summary>
/// The suggestion validity policy (gh#544, R-4): the configured window, clamped so a suggestion can never outlive the
/// session's pre-close auto-flatten deadline (R-13).
/// </summary>
/// <remarks>
/// The property that matters is the <b>clamp</b>: a suggestion still live past the flatten deadline invites the
/// operator into a position the auto-flatten is about to close. The rest is boundary discipline — a disabled or
/// missing schedule has no deadline to respect, a deadline already behind us cannot bind, and the result is always
/// strictly after issuance so the DB CHECK can never be violated.
/// </remarks>
public class SuggestionValidityTests
{
    // 14:30 CT is the shipped equity-index deadline. The policy takes the deadline itself rather than a
    // FlattenSchedule: the agent-review path must not depend on flatten machinery (gh#402's structural guard), and a
    // deadline is all the arithmetic needs.
    private static TimeOnly? Deadline(int hour = 14, int minute = 30) => new TimeOnly(hour, minute);

    // A Central-time instant expressed as the UTC it corresponds to in July (CDT = UTC-5).
    private static DateTimeOffset CentralSummer(int hour, int minute) =>
        new(2026, 7, 30, hour + 5, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ExpiresAt_ShouldUseTheConfiguredWindow_WhenTheDeadlineIsFarAway()
    {
        // 09:00 CT, deadline 14:30 CT -- 5h30m away, so a 60-minute window is untouched.
        DateTimeOffset issued = CentralSummer(9, 0);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(60), Deadline());

        expiry.Should().Be(issued.AddMinutes(60));
    }

    [Fact]
    public void ExpiresAt_ShouldClampToTheFlattenDeadline_WhenTheWindowWouldOutliveIt()
    {
        // 14:00 CT with a 60-minute window would run to 15:00 -- past the 14:30 flatten. Clamp to 14:30.
        DateTimeOffset issued = CentralSummer(14, 0);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(60), Deadline());

        expiry.Should().Be(issued.AddMinutes(30));
    }

    [Fact]
    public void ExpiresAt_ShouldNotClamp_WhenTheMarketHasNoDeadline_BecauseFlattenIsDisabled()
    {
        // Disabling auto-flatten is a deliberate, warned override (R-13) -- there is then no deadline to respect.
        DateTimeOffset issued = CentralSummer(14, 0);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(60), marketDeadline: null);

        expiry.Should().Be(issued.AddMinutes(60));
    }

    [Fact]
    public void ExpiresAt_ShouldNotClamp_WhenTheInstrumentIsUnknown()
    {
        DateTimeOffset issued = CentralSummer(14, 0);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(60), marketDeadline: null);

        expiry.Should().Be(issued.AddMinutes(60));
    }

    [Fact]
    public void ExpiresAt_ShouldFallBackToTheConfiguredWindow_WhenTheDeadlineHasAlreadyPassed()
    {
        // 15:00 CT, after the 14:30 deadline. Today's flatten has already run; the binding constraint is the NEXT
        // session's deadline, which needs a trading calendar this domain does not have. Clamping to a negative span
        // would produce an expiry at or before issuance and violate the DB CHECK.
        DateTimeOffset issued = CentralSummer(15, 0);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(60), Deadline());

        expiry.Should().Be(issued.AddMinutes(60));
    }

    [Fact]
    public void ExpiresAt_ShouldReturnTheDeadline_WhenTheWindowLandsExactlyOnIt()
    {
        // Exactly 30 minutes out with a 30-minute window: the two agree, and the boundary must not overshoot.
        DateTimeOffset issued = CentralSummer(14, 0);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(30), Deadline());

        expiry.Should().Be(issued.AddMinutes(30));
    }

    [Fact]
    public void ExpiresAt_ShouldAlwaysBeAfterIssuance_EvenOneSecondBeforeTheDeadline()
    {
        // The DB CHECK is ExpiresAt > CreatedAt. A hair before the deadline must still clear it.
        DateTimeOffset issued = CentralSummer(14, 29).AddSeconds(59);

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(issued, TimeSpan.FromMinutes(60), Deadline());

        expiry.Should().BeAfter(issued);
        expiry.Should().Be(issued.AddSeconds(1));
    }

    [Fact]
    public void ExpiresAt_ShouldMeasureTheDeadlineInCentralWallClock_NotAFixedOffset()
    {
        // The same wall-clock 14:00 CT in WINTER (CST = UTC-6) must clamp identically to the summer case above.
        // Pinning the deadline to an offset would drift it by an hour twice a year -- exactly what MarketClock exists
        // to prevent, and a drift that would let a window outlive the flatten it is clamped to.
        DateTimeOffset winterIssued = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero); // 14:00 CT in January

        DateTimeOffset expiry = SuggestionValidity.ExpiresAt(winterIssued, TimeSpan.FromMinutes(60), Deadline());

        expiry.Should().Be(winterIssued.AddMinutes(30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExpiresAt_ShouldRefuseANonPositiveWindow(int minutes)
    {
        // The DB CHECK depends on this being positive; state the precondition rather than emit a refused row.
        Action act = () => SuggestionValidity.ExpiresAt(
            CentralSummer(9, 0), TimeSpan.FromMinutes(minutes), Deadline());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
