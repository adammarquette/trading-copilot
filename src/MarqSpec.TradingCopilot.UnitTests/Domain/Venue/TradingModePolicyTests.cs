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
}
