namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>What happened to an order on its way to the broker.</summary>
public enum ExecutionOutcome
{
    /// <summary>The venue accepted it — at the size the gate approved, which may be smaller than asked for.</summary>
    Placed,

    /// <summary>
    /// Refused by the R-14 mode guard: this account may not be traded from this environment. Nothing was sized
    /// and nothing was sent.
    /// </summary>
    RefusedByMode,

    /// <summary>
    /// Refused by the risk gate (R-5 / R-16) — blocked outright, or resized to nothing, which is the gate's
    /// "no trade". Nothing was sent.
    /// </summary>
    RefusedByRisk,

    /// <summary>
    /// Refused because the request contradicts itself: the risk snapshot describes a different account than the
    /// one being traded, or the contract was resolved for a different instrument than the proposal is sized for.
    /// Refused <b>before</b> the gate runs — evaluating an incoherent request would produce an authorization for
    /// something other than what would be sent.
    /// </summary>
    RefusedByMismatch,

    /// <summary>
    /// Refused because the ticket cannot express this order type. Distinct from a venue lacking the capability:
    /// the request itself is unrepresentable, so no venue could receive it correctly.
    /// </summary>
    RefusedByUnsupportedType,
}
