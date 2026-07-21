using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

public class TradingModePolicyTests
{
    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    [InlineData(DeploymentEnvironment.Production)]
    public void IsAllowed_ShouldReturnTrue_WhenModeIsPractice(DeploymentEnvironment environment)
    {
        TradingModePolicy.IsAllowed(TradingMode.Practice, environment).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenModeIsLiveInProduction()
    {
        TradingModePolicy.IsAllowed(TradingMode.Live, DeploymentEnvironment.Production).Should().BeTrue();
    }

    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    public void IsAllowed_ShouldReturnFalse_WhenModeIsLiveOutsideProduction(DeploymentEnvironment environment)
    {
        TradingModePolicy.IsAllowed(TradingMode.Live, environment).Should().BeFalse();
    }

    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    public void EnsureAllowed_ShouldThrow_WhenModeIsLiveOutsideProduction(DeploymentEnvironment environment)
    {
        Action act = () => TradingModePolicy.EnsureAllowed(TradingMode.Live, environment);

        act.Should().Throw<TradingModeNotAllowedException>()
            .Which.Message.Should().Contain("Live").And.Contain(environment.ToString());
    }

    [Fact]
    public void EnsureAllowed_ShouldNotThrow_WhenModeIsLiveInProduction()
    {
        Action act = () => TradingModePolicy.EnsureAllowed(TradingMode.Live, DeploymentEnvironment.Production);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    [InlineData(DeploymentEnvironment.Production)]
    public void EnsureAllowed_ShouldNotThrow_WhenModeIsPractice(DeploymentEnvironment environment)
    {
        Action act = () => TradingModePolicy.EnsureAllowed(TradingMode.Practice, environment);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureAccountAllowed_ShouldThrow_WhenAccountIsLiveOutsideProduction()
    {
        VenueAccount account = new(
            VenueAccountId.Create(VenueId.Parse("projectx"), "9001"),
            "50KTC-V2-DLL-0000",
            Balance: 50_000m,
            CanTrade: true,
            IsVisible: true,
            Mode: TradingMode.Live);

        Action act = () => TradingModePolicy.EnsureAllowed(account, DeploymentEnvironment.Staging);

        act.Should().Throw<TradingModeNotAllowedException>()
            .Which.Message.Should().Contain("50KTC-V2-DLL-0000");
    }

    [Fact]
    public void EnsureAccountAllowed_ShouldNotThrow_WhenAccountIsPracticeOutsideProduction()
    {
        VenueAccount account = new(
            VenueAccountId.Create(VenueId.Parse("projectx"), "9002"),
            "PRAC-50K-1234",
            Balance: 50_000m,
            CanTrade: true,
            IsVisible: true,
            Mode: TradingMode.Practice);

        Action act = () => TradingModePolicy.EnsureAllowed(account, DeploymentEnvironment.Development);

        act.Should().NotThrow();
    }

    // --- Undeclared: stricter than Live, because we do not know what it is ---

    [Theory]
    [InlineData(DeploymentEnvironment.Development)]
    [InlineData(DeploymentEnvironment.Staging)]
    [InlineData(DeploymentEnvironment.Production)]
    public void IsAllowed_ShouldRefuseAnUndeclaredMode_InEveryEnvironmentIncludingProduction(
        DeploymentEnvironment environment)
    {
        // Live is permitted in production; Undeclared is permitted nowhere. "We have not established whether
        // capital is at stake" is not a state to trade from, and production is exactly where guessing costs
        // the most.
        TradingModePolicy.IsAllowed(TradingMode.Undeclared, environment).Should().BeFalse();
    }

    [Fact]
    public void EnsureAllowed_ShouldThrowForUndeclared_EvenInProduction()
    {
        Action act = () => TradingModePolicy.EnsureAllowed(TradingMode.Undeclared, DeploymentEnvironment.Production);

        act.Should().Throw<TradingModeNotAllowedException>()
            .Which.Message.Should().Contain("Undeclared");
    }

    [Fact]
    public void EnsureAllowed_ShouldNotBlameTheEnvironment_WhenTheModeIsUndeclared()
    {
        // "practice accounts only outside production" is the wrong reason and points at the wrong fix: it reads
        // as though production would have been fine. The operator has to classify the account, not change
        // environment.
        Action act = () => TradingModePolicy.EnsureAllowed(TradingMode.Undeclared, DeploymentEnvironment.Production);

        string message = act.Should().Throw<TradingModeNotAllowedException>().Which.Message;

        message.Should().NotContain("practice accounts only outside production");
        message.Should().Contain("Classify the account's stage");
    }

    [Fact]
    public void EnsureAccountAllowed_ShouldNameTheAccountAndAskForClassification_WhenItIsUndeclared()
    {
        VenueAccount account = new(
            VenueAccountId.Create(VenueId.Parse("projectx"), "9003"),
            "EXPRESS-50K-9003",
            Balance: 50_000m,
            CanTrade: true,
            IsVisible: true,
            Mode: TradingMode.Undeclared);

        Action act = () => TradingModePolicy.EnsureAllowed(account, DeploymentEnvironment.Production);

        string message = act.Should().Throw<TradingModeNotAllowedException>().Which.Message;

        message.Should().Contain("EXPRESS-50K-9003").And.Contain("Classify the account's stage");
        message.Should().NotContain("practice accounts only outside production");
    }
}
