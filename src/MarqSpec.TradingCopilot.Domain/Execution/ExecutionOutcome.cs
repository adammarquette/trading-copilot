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
}
