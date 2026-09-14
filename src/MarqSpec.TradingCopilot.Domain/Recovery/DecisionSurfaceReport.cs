namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// What a restart's decision surface looks like (gh#221, ADR-0013): the inert counts — for the audited "this came
/// back" summary — and any <see cref="DecisionInconsistency"/> a crash mid-write left. The counts are observations,
/// not actions: nothing here is resumed. <see cref="IsConsistent"/> gates the fail-safe.
/// </summary>
/// <param name="Orders">Every rehydrated order.</param>
/// <param name="StagedOrders">Orders armed server-side, awaiting an explicit take (re-validated at take, R-12).</param>
/// <param name="Conditionals">Every rehydrated conditional-entry order.</param>
/// <param name="PendingConditionals">Conditionals still waiting for a genuine trigger (re-gated at fire, R-12).</param>
/// <param name="HiddenStops">Synthetic stops held server-side, promoting only as price nears them.</param>
/// <param name="NativeStops">Stops promoted to exchange-held working orders.</param>
/// <param name="OrphanedStops">Hidden stops orphaned by a connection loss, re-armed on reconnect (gh#209).</param>
/// <param name="ActiveSuggestions">Suggestions still active — inert data until taken, which re-gates (R-12).</param>
/// <param name="Inconsistencies">Impossible combinations found; empty when the surface is coherent.</param>
public sealed record DecisionSurfaceReport(
    int Orders,
    int StagedOrders,
    int Conditionals,
    int PendingConditionals,
    int HiddenStops,
    int NativeStops,
    int OrphanedStops,
    int ActiveSuggestions,
    IReadOnlyList<DecisionInconsistency> Inconsistencies)
{
    /// <summary>Whether the surface is coherent — no impossible combination survived the restart.</summary>
    public bool IsConsistent => Inconsistencies.Count == 0;

    /// <summary>
    /// How many suggestions the <b>recovery expire pass</b> voided just before this snapshot (gh#545) — those whose
    /// validity window passed while the process was down, so the <see cref="ActiveSuggestions"/> count above is
    /// truthful rather than reporting a dead suggestion as active. A <b>no-risk maintenance</b> count: it is never an
    /// inconsistency and cannot trip <see cref="IsConsistent"/> (a suggestion carries no risk, ADR-0013).
    /// </summary>
    public int ExpiredOnRecovery { get; init; }
}
