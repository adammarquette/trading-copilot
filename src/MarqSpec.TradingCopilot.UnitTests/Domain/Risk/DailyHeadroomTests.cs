using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Risk;

/// <summary>
/// The pure daily-loss-budget arithmetic (R-5, gh#627), extracted from the risk gate: given the firm's optional
/// hard daily loss limit, the operator's daily drawdown governor, and the day's realized loss, how much room
/// remains before either refuses more risk. Read-only and advisory — the gate consumes it — so a wrong number is a
/// wrong limit. These pin the edges: an absent limit stays absent (never a permissive default), the "spent"
/// checks fire exactly where the gate's do, and the subtraction never rounds headroom upward.
/// </summary>
public class DailyHeadroomTests
{
    [Fact]
    public void Remaining_ShouldSubtractDayLossFromTheHardLimit_WhenTheFirmImposesOne()
    {
        DailyHeadroom headroom = DailyHeadroom.Remaining(dailyLossLimit: 1_000m, dailyDrawdownGovernor: 5_000m, dayLoss: 300m);

        headroom.UnderDailyLossLimit.Should().Be(700m);
    }

    [Fact]
    public void Remaining_ShouldLeaveTheHardLimitAbsent_WhenTheFirmImposesNone()
    {
        // Apex intraday-trail accounts carry no daily loss limit. Absence stays ABSENT (null) — never widened into
        // a permissive number the way a fabricated default would (R-5: absence is never a licence). It is the
        // governor and the drawdown floor that bind these accounts, not this layer.
        DailyHeadroom headroom = DailyHeadroom.Remaining(dailyLossLimit: null, dailyDrawdownGovernor: 5_000m, dayLoss: 300m);

        headroom.UnderDailyLossLimit.Should().BeNull();
    }

    [Fact]
    public void Remaining_ShouldSubtractDayLossFromTheGovernor()
    {
        DailyHeadroom headroom = DailyHeadroom.Remaining(dailyLossLimit: null, dailyDrawdownGovernor: 2_000m, dayLoss: 800m);

        headroom.UnderGovernor.Should().Be(1_200m);
    }

    [Fact]
    public void Remaining_ShouldGiveTheFullBudget_OnAGreenDay()
    {
        DailyHeadroom headroom = DailyHeadroom.Remaining(dailyLossLimit: 1_000m, dailyDrawdownGovernor: 2_000m, dayLoss: 0m);

        headroom.UnderDailyLossLimit.Should().Be(1_000m);
        headroom.UnderGovernor.Should().Be(2_000m);
    }

    [Theory]
    [InlineData(1_000, 1_000)] // exactly spent
    [InlineData(1_000, 1_200)] // overspent — already past the limit
    public void DailyLossLimitSpent_ShouldBeTrue_WhenNoRoomRemains(double limit, double dayLoss)
    {
        DailyHeadroom.Remaining((decimal)limit, dailyDrawdownGovernor: 100_000m, dayLoss: (decimal)dayLoss)
            .DailyLossLimitSpent.Should().BeTrue();
    }

    [Fact]
    public void DailyLossLimitSpent_ShouldBeFalse_WhenAnyRoomRemains()
    {
        DailyHeadroom.Remaining(1_000m, 100_000m, 999.99m).DailyLossLimitSpent.Should().BeFalse();
    }

    [Fact]
    public void DailyLossLimitSpent_ShouldBeFalse_WhenTheFirmImposesNoLimit()
    {
        // Matches the gate, where a null remaining never triggers the daily-loss block (null <= 0 is false). A
        // missing limit does not bind this layer even at a large loss — the governor and floor still do.
        DailyHeadroom.Remaining(dailyLossLimit: null, dailyDrawdownGovernor: 100_000m, dayLoss: 50_000m)
            .DailyLossLimitSpent.Should().BeFalse();
    }

    [Theory]
    [InlineData(2_000, 2_000)]
    [InlineData(2_000, 2_500)]
    public void GovernorSpent_ShouldBeTrue_WhenNoRoomRemains(double governor, double dayLoss)
    {
        DailyHeadroom.Remaining(dailyLossLimit: null, dailyDrawdownGovernor: (decimal)governor, dayLoss: (decimal)dayLoss)
            .GovernorSpent.Should().BeTrue();
    }

    [Fact]
    public void GovernorSpent_ShouldBeFalse_WhenAnyRoomRemains()
    {
        DailyHeadroom.Remaining(null, 2_000m, 1_999.99m).GovernorSpent.Should().BeFalse();
    }

    [Fact]
    public void Remaining_ShouldSubtractExactly_NeverRoundingHeadroomUpward()
    {
        // decimal, not double: a rounding direction that ever INCREASES headroom is a safety defect (gh#627). The
        // fractional cents must survive the subtraction intact.
        DailyHeadroom headroom = DailyHeadroom.Remaining(dailyLossLimit: 100.005m, dailyDrawdownGovernor: 100.005m, dayLoss: 0.001m);

        headroom.UnderDailyLossLimit.Should().Be(100.004m);
        headroom.UnderGovernor.Should().Be(100.004m);
    }

    [Fact]
    public void Remaining_ShouldShrinkHeadroom_AsTheDayLossGrows()
    {
        decimal atSmallLoss = DailyHeadroom.Remaining(1_000m, 1_000m, 100m).UnderGovernor;
        decimal atLargerLoss = DailyHeadroom.Remaining(1_000m, 1_000m, 400m).UnderGovernor;

        atLargerLoss.Should().BeLessThan(atSmallLoss);
    }
}
