using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Risk;

public class PositionSizerTests
{
    [Fact]
    public void MaxContracts_ShouldDivideBudgetByRiskPerContract()
    {
        // $1,000 of budget against a $250 stop is 4 contracts.
        PositionSizer.MaxContracts(riskBudget: 1_000m, lossPerContract: 250m).Should().Be(4);
    }

    [Fact]
    public void MaxContracts_ShouldRoundDown_WhenBudgetDoesNotDivideEvenly()
    {
        // Never round up: the extra contract would put the loss past the budget.
        PositionSizer.MaxContracts(riskBudget: 999m, lossPerContract: 250m).Should().Be(3);
    }

    [Fact]
    public void MaxContracts_ShouldBeZero_WhenBudgetCannotCoverOneContract()
    {
        PositionSizer.MaxContracts(riskBudget: 100m, lossPerContract: 250m).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void MaxContracts_ShouldBeZero_WhenBudgetIsExhausted(decimal riskBudget)
    {
        PositionSizer.MaxContracts(riskBudget, lossPerContract: 250m).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-250)]
    public void MaxContracts_ShouldThrow_WhenRiskPerContractIsNotPositive(decimal lossPerContract)
    {
        // A zero-distance stop would imply unbounded size -- refuse rather than size against it.
        Action act = () => PositionSizer.MaxContracts(riskBudget: 1_000m, lossPerContract);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
