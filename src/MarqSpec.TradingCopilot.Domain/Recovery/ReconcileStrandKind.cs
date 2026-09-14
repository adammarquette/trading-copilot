namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// Which kind of stranded pre-transmit intent the runtime reconcile sweep found (gh#722) — the runtime sibling of
/// the two <see cref="DecisionInconsistencyKind"/> strands the restart rehydrator flags (<see
/// cref="DecisionInconsistencyKind.OrderMidTaking"/> / <see cref="DecisionInconsistencyKind.ConditionalMidFiring"/>).
/// </summary>
/// <remarks>
/// Never persisted and never read from the database — the sweep constructs it from what its cross-owner discovery
/// found, so unlike <see cref="Execution.OrderStatus"/> it carries no refusable zero. It exists only to route the
/// operator alert and the strand-detected metric to the right kind (a <c>Taking</c> order vs a <c>Firing</c>
/// conditional), and to name the reconcile endpoint the operator resolves it through.
/// </remarks>
public enum ReconcileStrandKind
{
    /// <summary>An order stranded mid-take (<see cref="Execution.OrderStatus.Taking"/>, gh#530) — resolved via <c>POST /orders/{id}/reconcile</c>.</summary>
    OrderTaking,

    /// <summary>A conditional stranded mid-fire (<see cref="Execution.ConditionalStatus.Firing"/>, gh#577) — resolved via <c>POST /conditionals/{id}/reconcile</c>.</summary>
    ConditionalFiring,

    /// <summary>
    /// A position-action intent left <see cref="PositionActionIntentStatus.Open"/> (gh#1161) — the reduce or
    /// exit was asked, the venue may have been touched, and the #1160 outcome journal never landed. Surfaced
    /// only; there is no dedicated reconcile endpoint — resolve against venue truth using the intent row.
    /// </summary>
    PositionActionIntent,
}
