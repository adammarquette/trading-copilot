namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The <b>read-only</b> seam onto venue-truth position reconciliation (gh#193, gh#929): read an account's positions
/// from the venue, tagged with their <see cref="MarqSpec.TradingCopilot.Domain.Flatten.PositionMarkBasis"/>. It
/// exposes only the read — no exit / close / order path — so a consumer that injects <i>this</i> (rather than the
/// concrete <see cref="PositionReconciliationService"/>, which also drives the safety-critical flatten) is read-only
/// by construction. The chat co-pilot's <c>read_positions</c> tool depends on this so the model can never reach an
/// order path through it (enforcement lives below the model).
/// </summary>
public interface IPositionReconciler
{
    /// <summary>
    /// Reconciles the account's positions from venue truth at <paramref name="now"/> (R-20-scoped to the caller).
    /// </summary>
    /// <param name="accountId">The account to reconcile.</param>
    /// <param name="now">The current instant — the settlement window is judged against it in market time.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The reconciliation, or <see langword="null"/> if the account is not found / not owned by the caller. A venue
    /// that cannot be reached yields a <see cref="MarqSpec.TradingCopilot.Domain.Flatten.PositionMarkBasis.Unknown"/>
    /// reconciliation with no positions — declared-unknown, never a stale live-looking view.
    /// </returns>
    Task<PositionReconciliation?> ReconcileAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken);
}
