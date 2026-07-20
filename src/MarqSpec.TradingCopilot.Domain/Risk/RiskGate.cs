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

        decimal? dailyLossRemaining = context.Rules.DailyLossLimit is { } limit ? limit - state.DayLoss : null;
        if (dailyLossRemaining <= 0m)
        {
            return GateDecision.Block(
                RiskLayer.DailyLossLimit,
                FormattableString.Invariant($"The account's daily loss limit of {context.Rules.DailyLossLimit} is spent."));
        }

        decimal governorRemaining = profile.DailyDrawdownGovernor - state.DayLoss;
        if (governorRemaining <= 0m)
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

        // --- Size every layer; the most restrictive wins ---
        // The hard account limits are measured at the safety stop: the catastrophic case, not the expected one,
        // is what may never breach the account.

        decimal headroom = context.Drawdown.HeadroomFrom(equity);

        (RiskLayer Layer, int MaxQuantity)[] layers =
        [
            (RiskLayer.PerTradeRisk, PositionSizer.MaxContracts(profile.PerTradeRiskFraction * headroom, sizingPerContract)),
            (RiskLayer.MaxDrawdownPerTrade, PositionSizer.MaxContracts(profile.MaxDrawdownPerTrade, worstCasePerContract)),
            (RiskLayer.DrawdownFloor, PositionSizer.MaxContracts(headroom, worstCasePerContract)),
            (RiskLayer.DailyLossLimit, dailyLossRemaining is { } remaining
                ? PositionSizer.MaxContracts(remaining, worstCasePerContract)
                : int.MaxValue),
            (RiskLayer.DailyGovernor, PositionSizer.MaxContracts(governorRemaining, worstCasePerContract)),
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
