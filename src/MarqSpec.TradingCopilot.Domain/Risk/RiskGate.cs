using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The layered, enforcing risk model (R-5). Size is the <b>most restrictive</b> of every stacked layer, the
/// catastrophic case is measured at the safety stop rather than the working stop, and a breach is resized or
/// refused outright. Deterministic and dependency-free by design: this is the limit, so it must be trivially
/// testable and impossible for a model to talk around.
/// </summary>
public sealed class RiskGate : IRiskGate
{
    /// <inheritdoc />
    public GateDecision Evaluate(OrderProposal proposal, RiskContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The consistency target is evaluated around the rest of the gate rather than inside it (gh#380),
        // because it is the one layer that can bind WITHOUT changing the decision. When it only advises, the
        // order must be sized by every other layer exactly as before and simply carry the warning out.
        GateDecision decision = EvaluateCore(proposal, context);
        GateAdvisory? consistency = ConsistencyAdvisory(context);

        return consistency is null ? decision : decision.With(consistency);
    }

    /// <summary>
    /// Builds the consistency advisory when the account has a target, whether or not it is breached.
    /// </summary>
    /// <remarks>
    /// Raised even well below the threshold on purpose: gh#380 asks for the constraint to be visible <i>before</i>
    /// it binds. An operator who first hears about a consistency target when an order is refused has already
    /// spent the evaluation it was protecting.
    /// </remarks>
    private static GateAdvisory? ConsistencyAdvisory(RiskContext context)
    {
        if (context.Rules.ConsistencyTarget is not { } target)
        {
            return null;
        }

        ConsistencyWindow window = context.Consistency ?? ConsistencyWindow.Empty;
        decimal consumed = window.ConsumedFraction;

        return new GateAdvisory(
            RiskLayer.ConsistencyTarget,
            consumed,
            target,
            window.Breaches(target)
                ? $"The best day is {consumed:P0} of cumulative profit, over the {target:P0} consistency target — a payout would be disqualified."
                : $"The best day is {consumed:P0} of cumulative profit, against a {target:P0} consistency target.");
    }

    private static GateDecision EvaluateCore(OrderProposal proposal, RiskContext context)
    {
        InstrumentSpec instrument = proposal.Instrument;
        AccountRiskState state = context.State;
        RiskProfile profile = context.Profile;

        // --- Obviously wrong tickets, refused before any sizing math (R-16) ---

        if (!IsStopProtective(proposal))
        {
            return GateDecision.Block(
                RiskLayer.SanityCap,
                $"A {proposal.Side} order's stops must sit on the losing side of the entry.");
        }

        decimal ticksFromMarket = instrument.TicksBetween(proposal.Entry, proposal.ReferencePrice);
        if (ticksFromMarket > context.Sanity.FatFingerBandTicks)
        {
            return GateDecision.Block(
                RiskLayer.SanityCap,
                FormattableString.Invariant(
                    $"Entry {proposal.Entry} is {ticksFromMarket:0} ticks from the market, outside the {context.Sanity.FatFingerBandTicks}-tick band."));
        }

        decimal worstCasePerContract = instrument.LossPerContract(proposal.Entry, proposal.SafetyStop);
        decimal sizingPerContract = profile.SizingBasis == SizingBasis.SafetyStop
            ? worstCasePerContract
            : instrument.LossPerContract(proposal.Entry, proposal.Stop);

        if (worstCasePerContract <= 0m || sizingPerContract <= 0m)
        {
            return GateDecision.Block(
                RiskLayer.SanityCap,
                "A stop sits at the entry price, so the order cannot be sized against it.");
        }

        // --- Account-level refusals: no size rescues these ---

        decimal equity = state.Equity;
        if (context.Drawdown.IsBreachedBy(equity))
        {
            return GateDecision.Block(
                RiskLayer.DrawdownFloor,
                FormattableString.Invariant(
                    $"Equity {equity} is at or below the drawdown floor {context.Drawdown.Floor}."));
        }

        // The day's remaining loss budget under both daily limits (R-5), computed once and reused for the refusals
        // and the sizing below. One implementation, shared with the gh#587 projection (gh#627).
        DailyHeadroom daily = DailyHeadroom.Remaining(
            context.Rules.DailyLossLimit, profile.DailyDrawdownGovernor, state.DayLoss);

        if (daily.DailyLossLimitSpent)
        {
            return GateDecision.Block(
                RiskLayer.DailyLossLimit,
                FormattableString.Invariant($"The account's daily loss limit of {context.Rules.DailyLossLimit} is spent."));
        }

        if (daily.GovernorSpent)
        {
            return GateDecision.Block(
                RiskLayer.DailyGovernor,
                FormattableString.Invariant($"The daily drawdown governor of {profile.DailyDrawdownGovernor} is spent."));
        }

        if (profile.StopForDayAtProfitTarget
            && profile.DailyProfitTarget is { } target
            && state.DayRealizedPnL >= target)
        {
            return GateDecision.Block(
                RiskLayer.DailyProfitTarget,
                FormattableString.Invariant($"The daily profit target of {target} is reached and stop-for-the-day is on."));
        }

        // The consistency target refuses only when the account is configured for it (gh#380). Unlike every other
        // block above, this one protects payout ELIGIBILITY rather than capital -- breaching it does not blow the
        // account, it disqualifies an otherwise-passing evaluation -- so whether that is worth refusing a trade
        // for belongs to the account, not to this gate. Advisory accounts fall through and carry the warning out.
        if (context.Rules.ConsistencyTarget is { } consistencyTarget
            && context.Rules.ConsistencyEnforcement == ConsistencyEnforcement.Block
            && (context.Consistency ?? ConsistencyWindow.Empty).Breaches(consistencyTarget))
        {
            return GateDecision.Block(
                RiskLayer.ConsistencyTarget,
                $"The best day is already {(context.Consistency ?? ConsistencyWindow.Empty).ConsumedFraction:P0} of cumulative profit, over the {consistencyTarget:P0} consistency target.");
        }

        // --- Size every layer; the most restrictive wins ---
        // The hard account limits are measured at the safety stop: the catastrophic case, not the expected one,
        // is what may never breach the account.

        decimal headroom = context.Drawdown.HeadroomFrom(equity);

        (RiskLayer Layer, int MaxQuantity)[] layers =
        [
            (RiskLayer.PerTradeRisk, PositionSizer.MaxContracts(profile.PerTradeRiskFraction * headroom, sizingPerContract)),
            (RiskLayer.MaxDrawdownPerTrade, PositionSizer.MaxContracts(profile.MaxDrawdownPerTrade, worstCasePerContract)),
            (RiskLayer.DrawdownFloor, PositionSizer.MaxContracts(headroom, worstCasePerContract)),
            (RiskLayer.DailyLossLimit, daily.UnderDailyLossLimit is { } remaining
                ? PositionSizer.MaxContracts(remaining, worstCasePerContract)
                : int.MaxValue),
            (RiskLayer.DailyGovernor, PositionSizer.MaxContracts(daily.UnderGovernor, worstCasePerContract)),
            (RiskLayer.ManualCap, context.Manual.MaxContractsFor(instrument.Id)),
            (RiskLayer.SanityCap, Math.Min(
                context.Sanity.MaxContractsPerOrder,
                PositionSizer.MaxContracts(context.Sanity.MaxNotional, instrument.NotionalPerContract(proposal.Entry)))),
        ];

        (RiskLayer Layer, int MaxQuantity) tightest = layers[0];
        foreach ((RiskLayer Layer, int MaxQuantity) layer in layers)
        {
            if (layer.MaxQuantity < tightest.MaxQuantity)
            {
                tightest = layer;
            }
        }

        if (tightest.MaxQuantity <= 0)
        {
            return GateDecision.Block(
                tightest.Layer,
                $"No trade: the {tightest.Layer} layer leaves room for zero contracts.");
        }

        if (tightest.MaxQuantity < proposal.RequestedQuantity)
        {
            return GateDecision.Resize(
                tightest.MaxQuantity,
                tightest.Layer,
                $"Resized from {proposal.RequestedQuantity} to {tightest.MaxQuantity} contracts by the {tightest.Layer} layer.");
        }

        return GateDecision.Allow(
            proposal.RequestedQuantity,
            $"{proposal.RequestedQuantity} contracts are within every risk layer.");
    }

    private static bool IsStopProtective(OrderProposal proposal)
    {
        return proposal.Side switch
        {
            OrderSide.Buy => proposal.Stop.Value < proposal.Entry.Value
                && proposal.SafetyStop.Value < proposal.Entry.Value,
            OrderSide.Sell => proposal.Stop.Value > proposal.Entry.Value
                && proposal.SafetyStop.Value > proposal.Entry.Value,

            // An unrecognized side fails closed.
            _ => false,
        };
    }
}
