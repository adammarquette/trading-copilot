namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// Something the gate wants the operator to know that did <b>not</b> change the decision (gh#380).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="GateOutcome"/>. The outcome answers "does this order go, and at what
/// size" — three states, exhaustively handled by every caller — and adding a fourth "allowed but concerned"
/// state would silently change the meaning of every existing <c>switch</c> on a safety-critical path. An advisory
/// rides alongside the decision instead, so a caller that ignores it still behaves exactly as before.
/// </para>
/// <para>
/// It exists because a consistency target must be visible <i>before</i> it binds: an operator who only learns
/// about it at the moment an order is refused has already lost the evaluation the rule was protecting.
/// </para>
/// </remarks>
/// <param name="Layer">The risk layer raising it.</param>
/// <param name="ConsumedFraction">How much of the allowance is used, as a fraction.</param>
/// <param name="Threshold">The configured limit, as a fraction.</param>
/// <param name="Reason">Operator-facing explanation.</param>
public sealed record GateAdvisory(
    RiskLayer Layer,
    decimal ConsumedFraction,
    decimal Threshold,
    string Reason)
{
    /// <summary>Whether the allowance is already used up.</summary>
    public bool IsBreached => ConsumedFraction > Threshold;
}
