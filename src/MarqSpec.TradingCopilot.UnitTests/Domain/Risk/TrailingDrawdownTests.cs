using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Risk;

/// <summary>
/// The account-ending rule. A $50K Topstep account with a $2,000 trailing max-loss fails below a ~$48,000 floor —
/// so the risk budget is headroom to that floor, never the account size.
/// </summary>
public class TrailingDrawdownTests
{
    private const decimal StartingBalance = 50_000m;
    private const decimal TrailingAmount = 2_000m;

    private static TrailingDrawdown EndOfDay(decimal? locksAt = null)
    {
        return TrailingDrawdown.Start(TrailingMode.EndOfDay, TrailingAmount, StartingBalance, locksAt);
    }

    private static TrailingDrawdown Intraday(decimal? locksAt = null)
    {
        return TrailingDrawdown.Start(TrailingMode.Intraday, TrailingAmount, StartingBalance, locksAt);
    }

    [Fact]
    public void Start_ShouldPlaceFloorATrailingAmountBelowStartingBalance()
    {
        EndOfDay().Floor.Should().Be(48_000m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Start_ShouldThrow_WhenTrailingAmountIsNotPositive(decimal amount)
    {
        Action act = () => TrailingDrawdown.Start(TrailingMode.EndOfDay, amount, StartingBalance, locksAt: null);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- End-of-day trailing: moves only at the close, fixed during the session ---

    [Fact]
    public void AdvanceAtClose_ShouldRaiseFloor_WhenClosingBalanceIsHigher()
    {
        TrailingDrawdown advanced = EndOfDay().AdvanceAtClose(51_000m);

        advanced.Floor.Should().Be(49_000m);
    }

    [Fact]
    public void AdvanceAtClose_ShouldNotLowerFloor_WhenClosingBalanceIsLower()
    {
        // The floor never trails back down -- a losing day does not hand back headroom.
        TrailingDrawdown advanced = EndOfDay().AdvanceAtClose(51_000m).AdvanceAtClose(49_500m);

        advanced.Floor.Should().Be(49_000m);
    }

    [Fact]
    public void AdvanceIntraday_ShouldNotMoveFloor_WhenModeIsEndOfDay()
    {
        // An EOD floor is known at the open and does not budge, however good the session gets.
        TrailingDrawdown advanced = EndOfDay().AdvanceIntraday(55_000m);

        advanced.Floor.Should().Be(48_000m);
    }

    // --- Intraday trailing: follows the real-time peak, unrealized included ---

    [Fact]
    public void AdvanceIntraday_ShouldRaiseFloor_WhenEquityMakesANewPeak()
    {
        TrailingDrawdown advanced = Intraday().AdvanceIntraday(51_000m);

        advanced.Floor.Should().Be(49_000m);
    }

    [Fact]
    public void AdvanceIntraday_ShouldRaiseFloor_WhenGainIsStillUnrealized()
    {
        // Being +$1,000 unrealized raises the floor by $1,000 -- give it back and you are nearer the floor
        // at an unchanged realized balance. This is the trap intraday accounts spring.
        TrailingDrawdown peak = Intraday().AdvanceIntraday(51_000m);

        peak.HeadroomFrom(50_000m).Should().Be(1_000m);
    }

    [Fact]
    public void AdvanceIntraday_ShouldNotLowerFloor_WhenEquityFallsBackFromThePeak()
    {
        TrailingDrawdown advanced = Intraday().AdvanceIntraday(51_000m).AdvanceIntraday(50_200m);

        advanced.Floor.Should().Be(49_000m);
    }

    [Fact]
    public void AdvanceAtClose_ShouldNotMoveFloor_WhenModeIsIntraday()
    {
        TrailingDrawdown advanced = Intraday().AdvanceIntraday(51_000m).AdvanceAtClose(50_500m);

        advanced.Floor.Should().Be(49_000m);
    }

    // --- The lock: the floor stops trailing once it reaches a firm-defined ceiling ---

    [Fact]
    public void AdvanceAtClose_ShouldStopTrailing_WhenFloorReachesTheLock()
    {
        // Topstep locks the trailing floor at the starting balance.
        TrailingDrawdown advanced = EndOfDay(locksAt: StartingBalance).AdvanceAtClose(53_000m);

        advanced.Floor.Should().Be(50_000m);
    }

    [Fact]
    public void AdvanceIntraday_ShouldStopTrailing_WhenFloorReachesTheLock()
    {
        // Apex intraday accounts lock at start + $100.
        TrailingDrawdown advanced = Intraday(locksAt: StartingBalance + 100m).AdvanceIntraday(56_000m);

        advanced.Floor.Should().Be(50_100m);
    }

    // --- Headroom and breach ---

    [Fact]
    public void HeadroomFrom_ShouldReturnDistanceToTheFloor()
    {
        EndOfDay().HeadroomFrom(49_250m).Should().Be(1_250m);
    }

    [Fact]
    public void HeadroomFrom_ShouldNeverBeNegative_WhenEquityIsBelowTheFloor()
    {
        EndOfDay().HeadroomFrom(47_000m).Should().Be(0m);
    }

    [Fact]
    public void IsBreachedBy_ShouldBeTrue_WhenEquityTouchesTheFloor()
    {
        // Touching the threshold is a breach -- these accounts auto-liquidate on touch, not below it.
        EndOfDay().IsBreachedBy(48_000m).Should().BeTrue();
    }

    [Fact]
    public void IsBreachedBy_ShouldBeFalse_WhenEquityIsAboveTheFloor()
    {
        EndOfDay().IsBreachedBy(48_000.01m).Should().BeFalse();
    }
}
