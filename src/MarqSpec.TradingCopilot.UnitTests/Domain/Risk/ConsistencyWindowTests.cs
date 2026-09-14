using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Risk;

/// <summary>
/// The profit arithmetic a prop-firm consistency target is measured against (gh#380, R-5).
/// </summary>
/// <remarks>
/// The interesting cases are all degenerate windows rather than the happy path: a fresh account, and the two
/// ways a losing window makes the ratio meaningless. Each one is a division or a sign that would otherwise
/// misreport a perfectly good day as a breach — on a rule that can refuse an order.
/// </remarks>
public class ConsistencyWindowTests
{
    [Fact]
    public void ConsumedFraction_ShouldBeTheBestDaysShareOfCumulativeProfit_WhenTheWindowIsInProfit()
    {
        new ConsistencyWindow(CumulativeProfit: 10_000m, BestDayProfit: 4_000m)
            .ConsumedFraction.Should().Be(0.4m);
    }

    [Fact]
    public void ConsumedFraction_ShouldBeOne_WhenEveryPennyOfProfitCameFromOneDay()
    {
        // The first profitable day of an evaluation always consumes 100% -- worth pinning, because it means a
        // target can be "breached" on day one by arithmetic rather than by misbehaviour.
        new ConsistencyWindow(CumulativeProfit: 2_500m, BestDayProfit: 2_500m)
            .ConsumedFraction.Should().Be(1m);
    }

    [Fact]
    public void ConsumedFraction_ShouldBeZero_WhenThereIsNoProfitYet()
    {
        // A fresh account: the denominator is zero, and dividing by it is the obvious bug.
        ConsistencyWindow.Empty.ConsumedFraction.Should().Be(0m);
    }

    [Fact]
    public void ConsumedFraction_ShouldBeZero_WhenTheWindowIsAtALoss()
    {
        // A NEGATIVE denominator silently flips the comparison: 2000 / -5000 = -0.4, which is "under" any target
        // -- but a naive absolute-value fix would instead report a losing window as 40% consumed. Neither is
        // meaningful, because a window that is not in profit cannot disqualify a payout that will not happen.
        new ConsistencyWindow(CumulativeProfit: -5_000m, BestDayProfit: 2_000m)
            .ConsumedFraction.Should().Be(0m);
    }

    [Fact]
    public void ConsumedFraction_ShouldBeZero_WhenTheBestDayStillLostMoney()
    {
        // A losing day contributes nothing to the profit being apportioned; a negative share is not a thing.
        new ConsistencyWindow(CumulativeProfit: 10_000m, BestDayProfit: -1_500m)
            .ConsumedFraction.Should().Be(0m);
    }

    [Fact]
    public void Breaches_ShouldBeFalse_WhenTheShareExactlyEqualsTheTarget()
    {
        // The boundary is inclusive: "no more than 40%" permits exactly 40%. Off by one here either refuses a
        // compliant day or permits a non-compliant one.
        new ConsistencyWindow(CumulativeProfit: 10_000m, BestDayProfit: 4_000m)
            .Breaches(0.4m).Should().BeFalse();
    }

    [Fact]
    public void Breaches_ShouldBeTrue_WhenTheShareExceedsTheTarget()
    {
        new ConsistencyWindow(CumulativeProfit: 10_000m, BestDayProfit: 4_001m)
            .Breaches(0.4m).Should().BeTrue();
    }

    [Fact]
    public void Breaches_ShouldBeFalse_WhenTheWindowIsAtALoss()
    {
        new ConsistencyWindow(CumulativeProfit: -5_000m, BestDayProfit: 2_000m)
            .Breaches(0.4m).Should().BeFalse();
    }
}
