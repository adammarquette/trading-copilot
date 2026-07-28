using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.UnitTests.Data;

/// <summary>
/// Bridging the persisted risk declaration to the domain vocabulary the gate consumes (gh#10) — the same
/// pattern as <c>Firm.ToConventions()</c>: one bridge, so the gate can never see numbers the entity does not
/// hold.
/// </summary>
public class RiskProfileMappingTests
{
    private static RiskProfileRecord Record() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        DailyLossLimit = 1_000m,
        AccountProfitTarget = 3_000m,
        StartingBalance = 150_000m,
        FloorSource = FloorSource.FirmImposed,
        TrailingMode = TrailingMode.Intraday,
        TrailingAmount = 2_000m,
        LocksAt = 50_100m,
        PerTradeRiskFraction = 0.15m,
        TargetRewardRatio = 1.5m,
        MaxDrawdownPerTrade = 300m,
        DailyDrawdownGovernor = 600m,
        DailyProfitTarget = 1_500m,
        StopForDayAtProfitTarget = true,
        SizingBasis = SizingBasis.SafetyStop,
        MaxContractsPerOrder = 3,
        MaxBestDayFraction = 0.4m,
    };

    [Fact]
    public void ToRiskProfile_ShouldCarryTheTolerance()
    {
        RiskProfile profile = Record().ToRiskProfile();

        profile.PerTradeRiskFraction.Should().Be(0.15m);
        profile.TargetRewardRatio.Should().Be(1.5m);
        profile.MaxDrawdownPerTrade.Should().Be(300m);
        profile.DailyDrawdownGovernor.Should().Be(600m);
        profile.DailyProfitTarget.Should().Be(1_500m);
        profile.StopForDayAtProfitTarget.Should().BeTrue();
        profile.SizingBasis.Should().Be(SizingBasis.SafetyStop);
    }

    [Fact]
    public void ToAccountRiskRules_ShouldCarryTheHardRules()
    {
        AccountRiskRules rules = Record().ToAccountRiskRules();

        rules.DailyLossLimit.Should().Be(1_000m);
        rules.ProfitTarget.Should().Be(3_000m);
        rules.FloorSource.Should().Be(FloorSource.FirmImposed);
    }

    [Fact]
    public void ToAccountRiskRules_ShouldCarryTheConsistencyTarget_FromMaxBestDayFraction()
    {
        // THE gh#380 GAP, and the whole reason the rule was configurable without being enforced:
        // MaxBestDayFraction has been persisted, validated (0,1], check-constrained and exposed on the risk API
        // since the profile landed -- but it was never mapped here, so the gate never saw it and no order was
        // ever measured against it. This is the guard on that last mile.
        AccountRiskRules rules = Record().ToAccountRiskRules();

        rules.ConsistencyTarget.Should().Be(0.4m);
    }

    [Fact]
    public void ToAccountRiskRules_ShouldCarryTheEnforcementPosture()
    {
        RiskProfileRecord record = Record();
        record.ConsistencyEnforcement = ConsistencyEnforcement.Block;

        record.ToAccountRiskRules().ConsistencyEnforcement.Should().Be(ConsistencyEnforcement.Block);
    }

    [Fact]
    public void ToAccountRiskRules_ShouldDefaultToAdvisory_WhenThePostureWasNeverDeclared()
    {
        // Existing rows migrate in as Advisory. A profile that has been storing a target nothing enforced must
        // not start refusing orders because a column appeared.
        Record().ToAccountRiskRules().ConsistencyEnforcement.Should().Be(ConsistencyEnforcement.Advisory);
    }

    [Fact]
    public void ToAccountRiskRules_ShouldCarryNoTarget_WhenTheAccountHasNoConsistencyRule()
    {
        // Null means "no such rule", not zero -- zero would mean no day may contribute any profit at all.
        RiskProfileRecord record = Record();
        record.MaxBestDayFraction = null;

        record.ToAccountRiskRules().ConsistencyTarget.Should().BeNull();
    }

    [Fact]
    public void ToTrailingDrawdown_ShouldStartTheFloor_FromTheGivenStartingBalance()
    {
        // The entity persists the CONFIG (mode, amount, lock); the floor itself is runtime state the risk
        // engine advances -- so the bridge starts it from the balance the caller supplies.
        TrailingDrawdown drawdown = Record().ToTrailingDrawdown(startingBalance: 150_000m);

        drawdown.Mode.Should().Be(TrailingMode.Intraday);
        drawdown.TrailingAmount.Should().Be(2_000m);
        drawdown.LocksAt.Should().Be(50_100m);
        drawdown.Floor.Should().Be(50_100m); // 148_000 capped by the lock ceiling
    }

    [Fact]
    public void ToManualCaps_ShouldCarryTheCap()
    {
        ManualCaps caps = Record().ToManualCaps();

        caps.MaxContractsPerOrder.Should().Be(3);
        caps.PerInstrument.Should().BeEmpty(); // per-instrument caps land with the Instrument entity
    }
}
