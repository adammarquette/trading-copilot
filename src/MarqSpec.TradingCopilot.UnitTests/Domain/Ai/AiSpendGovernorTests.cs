using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Ai;

/// <summary>
/// The platform-level AI-spend governor (gh#448, ADR-0008): a PURE, deterministic budget check mirroring the R-5
/// daily risk governor (<c>remaining &lt;= 0 =&gt; Block</c>). It caps WHETHER an AI call is made against the
/// operator's declared daily budget — never what the model proposes. Dependency-free, so these are mocks-free unit
/// tests. The design-defining behaviours: it allows under budget, BLOCKS at the exact boundary (spent == budget) and
/// beyond, reports the pre-alert threshold, computes the consumed fraction, always populates a Reason (the blocked
/// advisory quotes it), and clamps a negative spend to zero. <see cref="AiSpendBudget.Declare"/> enforces its
/// invariants up front so a divide-by-zero or nonsensical budget never reaches the gate.
/// </summary>
public class AiSpendGovernorTests
{
    private static readonly AiSpendGovernor _governor = new();

    private static AiSpendBudget Budget(decimal daily = 10m, decimal fraction = 0.8m) =>
        AiSpendBudget.Declare(daily, fraction);

    [Fact]
    public void Evaluate_ShouldAllow_WhenSpentIsBelowBudget()
    {
        AiSpendDecision decision = _governor.Evaluate(Budget(10m), 5m);

        decision.IsBlocked.Should().BeFalse();
        decision.Verdict.Should().Be(AiSpendVerdict.Allow);
    }

    // LOAD-BEARING boundary: spent == budget must BLOCK, the exact mirror of RiskGate's `remaining <= 0 => Block`.
    // A `>` here instead of `>=` would let one more call through at the cap -- the whole point of the governor.
    [Fact]
    public void Evaluate_ShouldBlockAtTheExactBoundary_WhenSpentEqualsBudget()
    {
        AiSpendDecision decision = _governor.Evaluate(Budget(10m), 10m);

        decision.IsBlocked.Should().BeTrue();
        decision.Verdict.Should().Be(AiSpendVerdict.Block);
        decision.ThresholdReached.Should().BeTrue(); // a blocked decision always reports the threshold reached
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenSpentExceedsBudget()
    {
        _governor.Evaluate(Budget(10m), 12m).IsBlocked.Should().BeTrue();
    }

    // budget 10, fraction 0.8 => threshold 8: at or above 8 the pre-alert is due; below it is not.
    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    public void Evaluate_ShouldReportThresholdReached_WhenSpentIsAtOrAboveTheThreshold(int spent, bool expected)
    {
        _governor.Evaluate(Budget(10m, 0.8m), spent).ThresholdReached.Should().Be(expected);
    }

    [Fact]
    public void Evaluate_ShouldReportConsumedFraction_AsSpentOverBudget()
    {
        _governor.Evaluate(Budget(10m), 5m).ConsumedFraction.Should().Be(0.5m);
    }

    // The Reason is used as the Suppress.Detail on a budget-exhausted block, which must be non-null / non-empty.
    [Fact]
    public void Evaluate_ShouldPopulateAReason_OnABlockedDecision()
    {
        _governor.Evaluate(Budget(10m), 10m).Reason.Should().NotBeNullOrWhiteSpace();
    }

    // A bad caller can never make the fraction negative: a negative spend is clamped to zero.
    [Fact]
    public void Evaluate_ShouldClampNegativeSpendToZero()
    {
        AiSpendDecision decision = _governor.Evaluate(Budget(10m), -5m);

        decision.SpentUsd.Should().Be(0m);
        decision.ConsumedFraction.Should().Be(0m);
        decision.IsBlocked.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Declare_ShouldThrow_WhenDailyBudgetIsNotPositive(int daily)
    {
        Action act = () => AiSpendBudget.Declare(daily, 0.8m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Declare_ShouldThrow_WhenAlertThresholdFractionIsZero()
    {
        Action act = () => AiSpendBudget.Declare(10m, 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Declare_ShouldThrow_WhenAlertThresholdFractionIsNegative()
    {
        Action act = () => AiSpendBudget.Declare(10m, -0.1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Declare_ShouldThrow_WhenAlertThresholdFractionExceedsOne()
    {
        Action act = () => AiSpendBudget.Declare(10m, 1.5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // The boundary is inclusive: a fraction of exactly 1 (alert at 100% of budget) is valid.
    [Fact]
    public void Declare_ShouldAccept_WhenAlertThresholdFractionIsExactlyOne()
    {
        AiSpendBudget budget = AiSpendBudget.Declare(10m, 1m);

        budget.AlertThresholdFraction.Should().Be(1m);
        budget.ThresholdUsd.Should().Be(10m);
    }
}
