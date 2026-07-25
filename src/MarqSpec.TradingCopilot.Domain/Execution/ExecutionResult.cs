using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// What happened to a send, and why. Every order action is journaled (R-8), so the reason and the gate's
/// decision travel with the result rather than being reconstructed afterwards.
/// </summary>
/// <param name="Outcome">Placed, or refused — and by which guard.</param>
/// <param name="Order">The venue's acknowledgement when placed; otherwise <see langword="null"/>.</param>
/// <param name="Decision">
/// The gate's decision, including the binding layer. <see langword="null"/> for <b>any</b> pre-gate refusal —
/// mode, account state, mismatch, or an unrepresentable order type — in which case the order was never sized.
/// Do not read a null decision as "the mode guard refused"; read <see cref="Outcome"/> for that.
/// </param>
/// <param name="Reason">A human-readable explanation, always populated.</param>
public sealed record ExecutionResult(
    ExecutionOutcome Outcome,
    PlacedOrder? Order,
    GateDecision? Decision,
    string Reason)
{
    /// <summary>The venue accepted the order.</summary>
    /// <param name="order">The venue's acknowledgement.</param>
    /// <param name="decision">The gate decision that authorized it.</param>
    /// <returns>A placed result.</returns>
    public static ExecutionResult Placed(PlacedOrder order, GateDecision decision)
    {
        return new ExecutionResult(ExecutionOutcome.Placed, order, decision, decision.Reason);
    }

    /// <summary>The ladder ran and the gate decided; nothing was transmitted (arm/edit, gh#11).</summary>
    /// <param name="decision">The gate's decision — allowing, resizing, or blocking.</param>
    /// <returns>An evaluated, untransmitted result.</returns>
    public static ExecutionResult Evaluated(GateDecision decision)
    {
        return new ExecutionResult(ExecutionOutcome.Evaluated, Order: null, decision, decision.Reason);
    }

    /// <summary>The R-14 mode guard refused before anything was sized.</summary>
    /// <param name="reason">Why the account may not be traded here.</param>
    /// <returns>A refused result carrying no gate decision.</returns>
    public static ExecutionResult RefusedByMode(string reason)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByMode, Order: null, Decision: null, reason);
    }

    /// <summary>The risk gate refused — blocked, or resized to nothing.</summary>
    /// <param name="decision">The gate's decision, naming the binding layer.</param>
    /// <returns>A refused result.</returns>
    public static ExecutionResult RefusedByRisk(GateDecision decision)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByRisk, Order: null, decision, decision.Reason);
    }

    /// <summary>The venue reports the account as not tradable.</summary>
    /// <param name="reason">Which account, and what the venue said about it.</param>
    /// <returns>A refused result carrying no gate decision — nothing was sized.</returns>
    public static ExecutionResult RefusedByAccountState(string reason)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByAccountState, Order: null, Decision: null, reason);
    }

    /// <summary>The request contradicted itself, so it was refused before the gate ran.</summary>
    /// <param name="reason">Which part disagreed with which.</param>
    /// <returns>A refused result carrying no gate decision — nothing was sized.</returns>
    public static ExecutionResult RefusedByMismatch(string reason)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByMismatch, Order: null, Decision: null, reason);
    }

    /// <summary>The ticket cannot express this order type.</summary>
    /// <param name="reason">Why the type is unrepresentable.</param>
    /// <returns>A refused result carrying no gate decision — nothing was sized.</returns>
    public static ExecutionResult RefusedByUnsupportedType(string reason)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByUnsupportedType, Order: null, Decision: null, reason);
    }

    /// <summary>The venue cannot hold an exchange-native protective stop; the entry was not sent (ADR-0007, gh#11).</summary>
    /// <param name="reason">Why the position could not be protected.</param>
    /// <returns>A refused result carrying no gate decision.</returns>
    public static ExecutionResult RefusedByUnprotectableStop(string reason)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByUnprotectableStop, Order: null, Decision: null, reason);
    }

    /// <summary>A take-profit target sat on the wrong side of entry; refused before the gate (ADR-0007, gh#170).</summary>
    /// <param name="reason">Why the target contradicts the position's direction.</param>
    /// <returns>A refused result carrying no gate decision — nothing was sized.</returns>
    public static ExecutionResult RefusedByInvalidTarget(string reason)
    {
        return new ExecutionResult(ExecutionOutcome.RefusedByInvalidTarget, Order: null, Decision: null, reason);
    }
}
