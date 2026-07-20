using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Risk;

/// <summary>
/// The single enforcing checkpoint (R-5, ADR-0007). Every case here is a real order that must be sized, resized,
/// or refused — the LLM proposes, this decides.
/// </summary>
public class RiskGateTests
{
    private static InstrumentSpec Es =>
        InstrumentSpec.Create(InstrumentId.Parse("ES"), tickSize: 0.25m, pointValue: 50m);

    private readonly IRiskGate _gate = new RiskGate();

    /// <summary>Entry 5000, stop 4995 ($250/contract), safety stop 4990 ($500/contract worst case).</summary>
    private static OrderProposal Proposal(int quantity = 2, decimal? safetyStop = null)
    {
        return new OrderProposal(
            Es,
            OrderSide.Buy,
            quantity,
            Entry: new Price(5_000m),
            Stop: new Price(4_995m),
            SafetyStop: new Price(safetyStop ?? 4_990m),
            ReferencePrice: new Price(5_000m));
    }

    /// <summary>
    /// A healthy account with room to spare: $5,000 of floor headroom, $3,000 daily limit, $2,000 governor.
    /// The tightest layer allows 3 contracts, so a 2-lot passes untouched.
    /// </summary>
    private static RiskContext Context(
        decimal unrealizedPnL = 0m,
        decimal dayRealizedPnL = 0m,
        decimal maxDrawdownPerTrade = 1_500m,
        decimal perTradeRiskFraction = 0.20m,
        decimal? dailyLossLimit = 3_000m,
        decimal dailyGovernor = 2_000m,
        decimal? dailyProfitTarget = null,
        bool stopForDay = false,
        SizingBasis sizingBasis = SizingBasis.ActualStop,
        int manualMaxContracts = 5,
        int sanityMaxContracts = 10,
        decimal sanityMaxNotional = 10_000_000m,
        TrailingMode trailingMode = TrailingMode.EndOfDay)
    {
        return new RiskContext(
            // The realized balance already carries the day's result.
            new AccountRiskState(Balance: 55_000m + dayRealizedPnL, unrealizedPnL, dayRealizedPnL),
            TrailingDrawdown.Start(trailingMode, trailingAmount: 5_000m, startingBalance: 55_000m, locksAt: null),
            new AccountRiskRules(dailyLossLimit, ProfitTarget: null, FloorSource.FirmImposed),
            new RiskProfile(
                perTradeRiskFraction,
                TargetRewardRatio: 1.5m,
                maxDrawdownPerTrade,
                dailyGovernor,
                dailyProfitTarget,
                stopForDay,
                sizingBasis),
            ManualCaps.Create(manualMaxContracts),
            new SanityCaps(sanityMaxContracts, sanityMaxNotional, FatFingerBandTicks: 40));
    }

    // --- The happy path ---

    [Fact]
    public void Evaluate_ShouldAllowRequestedQuantity_WhenNoLayerBinds()
    {
        GateDecision decision = _gate.Evaluate(Proposal(quantity: 2), Context());

        decision.Outcome.Should().Be(GateOutcome.Allowed);
        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ShouldAlwaysExplainItself()
    {
        _gate.Evaluate(Proposal(), Context()).Reason.Should().NotBeNullOrWhiteSpace();
    }

    // --- Most restrictive wins: each layer, made binding in turn ---

    [Fact]
    public void Evaluate_ShouldResizeAndNameTheBindingLayer_WhenRequestExceedsTheTightestLayer()
    {
        // Worst case is $500/contract; max-DD-per-trade of $1,500 allows 3.
        GateDecision decision = _gate.Evaluate(Proposal(quantity: 10), Context());

        decision.Outcome.Should().Be(GateOutcome.Resized);
        decision.ApprovedQuantity.Should().Be(3);
        decision.BindingLayer.Should().Be(RiskLayer.MaxDrawdownPerTrade);
    }

    [Fact]
    public void Evaluate_ShouldBindOnDrawdownFloor_WhenFloorHeadroomIsTheTightestLayer()
    {
        // Down $4,000 on the day leaves $1,000 of floor headroom -> 2 contracts at the $500 worst case.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(
                dayRealizedPnL: -4_000m,
                maxDrawdownPerTrade: 100_000m,
                perTradeRiskFraction: 1m,
                dailyLossLimit: null,
                dailyGovernor: 100_000m));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.DrawdownFloor);
    }

    [Fact]
    public void Evaluate_ShouldBindOnDailyLossLimit_WhenTheHardDailyLimitIsTheTightestLayer()
    {
        // $3,000 limit less $2,000 already lost leaves $1,000 -> 2 contracts.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(
                dayRealizedPnL: -2_000m,
                maxDrawdownPerTrade: 100_000m,
                perTradeRiskFraction: 1m,
                dailyGovernor: 100_000m));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.DailyLossLimit);
    }

    [Fact]
    public void Evaluate_ShouldBindOnDailyGovernor_WhenThePersonalGovernorIsTighterThanTheHardLimit()
    {
        // The governor sits inside the hard limit and bites first -- risk managed before the wall.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(dailyGovernor: 1_000m, maxDrawdownPerTrade: 100_000m));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.DailyGovernor);
    }

    [Fact]
    public void Evaluate_ShouldBindOnPerTradeRisk_WhenThePercentageBudgetIsTheTightestLayer()
    {
        // 10% of $5,000 headroom is $500, against a $250 actual stop -> 2 contracts.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(perTradeRiskFraction: 0.10m, maxDrawdownPerTrade: 100_000m, dailyGovernor: 100_000m, dailyLossLimit: null));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.PerTradeRisk);
    }

    [Fact]
    public void Evaluate_ShouldBindOnManualCap_WhenTheOperatorsContractCapIsTheTightestLayer()
    {
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(manualMaxContracts: 1, maxDrawdownPerTrade: 100_000m, dailyGovernor: 100_000m, dailyLossLimit: null));

        decision.ApprovedQuantity.Should().Be(1);
        decision.BindingLayer.Should().Be(RiskLayer.ManualCap);
    }

    [Fact]
    public void Evaluate_ShouldBindOnSanityCap_WhenTheContractCeilingIsTheTightestLayer()
    {
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(
                sanityMaxContracts: 2,
                manualMaxContracts: 100,
                maxDrawdownPerTrade: 100_000m,
                dailyGovernor: 100_000m,
                dailyLossLimit: null));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.SanityCap);
    }

    [Fact]
    public void Evaluate_ShouldBindOnSanityCap_WhenNotionalWouldExceedTheCeiling()
    {
        // One ES contract at 5000 is $250,000 of notional; a $600,000 ceiling allows two.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(
                sanityMaxNotional: 600_000m,
                manualMaxContracts: 100,
                sanityMaxContracts: 100,
                maxDrawdownPerTrade: 100_000m,
                dailyGovernor: 100_000m,
                dailyLossLimit: null));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.SanityCap);
    }

    // --- Hard blocks: no size can rescue these ---

    [Fact]
    public void Evaluate_ShouldBlock_WhenTheDrawdownFloorIsAlreadyBreached()
    {
        GateDecision decision = _gate.Evaluate(Proposal(), Context(unrealizedPnL: -5_000m));

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.ApprovedQuantity.Should().Be(0);
        decision.BindingLayer.Should().Be(RiskLayer.DrawdownFloor);
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenTheDailyLossLimitIsAlreadyReached()
    {
        GateDecision decision = _gate.Evaluate(Proposal(), Context(dayRealizedPnL: -3_000m));

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.BindingLayer.Should().Be(RiskLayer.DailyLossLimit);
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenThePersonalGovernorIsExhausted()
    {
        GateDecision decision = _gate.Evaluate(Proposal(), Context(dayRealizedPnL: -2_000m, dailyGovernor: 2_000m));

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.BindingLayer.Should().Be(RiskLayer.DailyGovernor);
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenTheDailyProfitTargetIsReachedAndStopForDayIsOn()
    {
        // "Hit $1,500 and stop" as an enforced behavior, not a reminder.
        GateDecision decision = _gate.Evaluate(
            Proposal(),
            Context(dayRealizedPnL: 1_500m, dailyProfitTarget: 1_500m, stopForDay: true));

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.BindingLayer.Should().Be(RiskLayer.DailyProfitTarget);
    }

    [Fact]
    public void Evaluate_ShouldAllow_WhenTheDailyProfitTargetIsReachedButStopForDayIsOff()
    {
        GateDecision decision = _gate.Evaluate(
            Proposal(),
            Context(dayRealizedPnL: 1_500m, dailyProfitTarget: 1_500m, stopForDay: false));

        decision.Outcome.Should().Be(GateOutcome.Allowed);
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenEntryIsOutsideTheFatFingerBand()
    {
        // Entry 5000 against a 5100 market is 400 ticks away -- far outside a 40-tick band.
        OrderProposal fatFinger = Proposal() with { ReferencePrice = new Price(5_100m) };

        GateDecision decision = _gate.Evaluate(fatFinger, Context());

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.BindingLayer.Should().Be(RiskLayer.SanityCap);
    }

    [Fact]
    public void Evaluate_ShouldBlock_WhenTheStopSitsOnTheProfitableSideOfEntry()
    {
        // A long with its stop above entry is not a stop -- refuse rather than size it.
        OrderProposal inverted = Proposal() with { Stop = new Price(5_010m) };

        GateDecision decision = _gate.Evaluate(inverted, Context());

        decision.Outcome.Should().Be(GateOutcome.Blocked);
    }

    [Fact]
    public void Evaluate_ShouldEmitNoTrade_WhenConstraintsLeaveNoViableSize()
    {
        // A $100 per-trade cap cannot cover one contract's $500 worst case.
        GateDecision decision = _gate.Evaluate(Proposal(), Context(maxDrawdownPerTrade: 100m));

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.ApprovedQuantity.Should().Be(0);
        decision.BindingLayer.Should().Be(RiskLayer.MaxDrawdownPerTrade);
    }

    [Fact]
    public void Evaluate_ShouldCountOpenLossAgainstTheDailyGovernor_WhenAPositionIsUnderwater()
    {
        // The governor is *live* headroom: an open $1,500 loss has already spent it, realized or not.
        GateDecision decision = _gate.Evaluate(Proposal(), Context(unrealizedPnL: -1_500m, dailyGovernor: 1_500m));

        decision.Outcome.Should().Be(GateOutcome.Blocked);
        decision.BindingLayer.Should().Be(RiskLayer.DailyGovernor);
    }

    // --- The worst case is the safety stop, not the working stop ---

    [Fact]
    public void Evaluate_ShouldSizeOnTheSafetyStop_WhenSizingBasisIsSafetyStop()
    {
        // Same budget, wider stop: 20% of $5,000 is $1,000, against $500 rather than $250 -> half the size.
        GateDecision onActual = _gate.Evaluate(Proposal(quantity: 10), Context(maxDrawdownPerTrade: 100_000m, dailyGovernor: 100_000m, dailyLossLimit: null));
        GateDecision onSafety = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(sizingBasis: SizingBasis.SafetyStop, maxDrawdownPerTrade: 100_000m, dailyGovernor: 100_000m, dailyLossLimit: null));

        onActual.ApprovedQuantity.Should().Be(4);
        onSafety.ApprovedQuantity.Should().Be(2);
    }

    [Fact]
    public void Evaluate_ShouldBoundTheCatastrophicCase_WhenTheSafetyStopIsWide()
    {
        // A 40-point safety stop is $2,000/contract; $5,000 of floor headroom permits 2, never the 10 asked for.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10, safetyStop: 4_960m),
            Context(maxDrawdownPerTrade: 100_000m, dailyGovernor: 100_000m, dailyLossLimit: null));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.DrawdownFloor);
    }

    // --- Live state feeds the floor ---

    [Fact]
    public void Evaluate_ShouldCountUnrealisedLossAgainstHeadroom_WhenAPositionIsAlreadyOpen()
    {
        // Equity, not realized balance, is what the floor is measured against.
        GateDecision decision = _gate.Evaluate(
            Proposal(quantity: 10),
            Context(
                unrealizedPnL: -4_000m,
                maxDrawdownPerTrade: 100_000m,
                perTradeRiskFraction: 1m,
                dailyGovernor: 100_000m,
                dailyLossLimit: null));

        decision.ApprovedQuantity.Should().Be(2);
        decision.BindingLayer.Should().Be(RiskLayer.DrawdownFloor);
    }
}
