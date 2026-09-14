namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// One impossible decision-state combination found at rehydration (gh#221) — its kind, the owner and entity it
/// concerns, and a human-readable detail for the loud, audited alert. Never silently repaired.
/// </summary>
/// <param name="Kind">Which invariant was violated.</param>
/// <param name="Owner">The owning operator of the offending entity (R-20) — carried, never dropped.</param>
/// <param name="EntityId">The offending entity's id (the order, conditional, or stop plan).</param>
/// <param name="Detail">A human-readable description for the operator alert / audit.</param>
public sealed record DecisionInconsistency(DecisionInconsistencyKind Kind, Guid Owner, Guid EntityId, string Detail);
