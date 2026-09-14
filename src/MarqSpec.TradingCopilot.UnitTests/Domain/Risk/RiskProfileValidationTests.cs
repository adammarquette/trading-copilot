using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Risk;

/// <summary>
/// <see cref="RiskProfile.Declare"/> owns the declaration invariants (engineering §4, the
/// <c>FirmConventions.For</c> pattern): a profile that would size nothing — or everything — is refused at the
/// boundary, so the gate never sees it (gh#10).
/// </summary>
public class RiskProfileValidationTests
{
    private static RiskProfile Declare(
        decimal fraction = 0.15m,
        decimal ratio = 1.5m,
        decimal perTradeCap = 300m,
        decimal governor = 600m,
        decimal? dailyTarget = 1_500m)
    {
        return RiskProfile.Declare(
            fraction, ratio, perTradeCap, governor, dailyTarget,
            stopForDayAtProfitTarget: true,
            SizingBasis.SafetyStop);
    }

    [Fact]
    public void Declare_ShouldConstruct_ASensibleProfile()
    {
        RiskProfile profile = Declare();

        profile.PerTradeRiskFraction.Should().Be(0.15m);
        profile.DailyProfitTarget.Should().Be(1_500m);
        profile.SizingBasis.Should().Be(SizingBasis.SafetyStop);
    }

    [Fact]
    public void Declare_ShouldAllowNoDailyProfitTarget()
    {
        Declare(dailyTarget: null).DailyProfitTarget.Should().BeNull(); // null = no target, not zero
    }

    [Theory]
    [InlineData(0)]      // zero risk sizes nothing
    [InlineData(-0.1)]   // negative is nonsense
    [InlineData(1.01)]   // more than the whole headroom is not a fraction
    public void Declare_ShouldRefuse_APerTradeRiskFractionOutsideZeroToOne(decimal fraction)
    {
        Func<RiskProfile> act = () => Declare(fraction: fraction);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void Declare_ShouldRefuse_ANonPositiveTargetRewardRatio(decimal ratio)
    {
        Func<RiskProfile> act = () => Declare(ratio: ratio);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    public void Declare_ShouldRefuse_ANonPositiveMaxDrawdownPerTrade(decimal cap)
    {
        Func<RiskProfile> act = () => Declare(perTradeCap: cap);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-600)]
    public void Declare_ShouldRefuse_ANonPositiveDailyDrawdownGovernor(decimal governor)
    {
        Func<RiskProfile> act = () => Declare(governor: governor);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1500)]
    public void Declare_ShouldRefuse_ANonPositiveDailyProfitTarget_WhenOneIsSet(decimal target)
    {
        Func<RiskProfile> act = () => Declare(dailyTarget: target);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
