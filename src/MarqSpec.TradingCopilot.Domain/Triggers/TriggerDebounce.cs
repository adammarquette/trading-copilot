namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The pure debounce for an indicator trigger (gh#385): given the current <see cref="TriggerArmState"/> and a
/// fresh <see cref="ConditionEvaluation"/>, it decides the next state and whether to fire / resolve. It reads no
/// clock and no store — the evaluator threads state in and persists what comes back, which is what makes it
/// restart-safe and redelivery-safe (re-running the same evaluation from the same state is idempotent).
/// </summary>
/// <remarks>
/// The transition table:
/// <list type="table">
/// <listheader><term>State</term><description>Satisfied → · NotSatisfied → · Unmeasurable/Indeterminate →</description></listheader>
/// <item><term>Unseeded</term><description>Fired (silent seed) · Armed (silent seed) · Unseeded (hold)</description></item>
/// <item><term>Armed</term><description><b>Fired + Fire</b> (the arming edge) · Armed (hold) · Armed (hold)</description></item>
/// <item><term>Fired</term><description>Fired (debounce) · <b>Armed + Resolve</b> (re-arm) · Fired (hold)</description></item>
/// </list>
/// A missing / structurally-indeterminate measurement <b>never</b> changes state and never acts: we cannot tell
/// which side of the threshold we are on, so we neither fire nor re-arm (fail-closed).
/// </remarks>
public static class TriggerDebounce
{
    /// <summary>Decides the next state and side effect for one evaluation.</summary>
    /// <param name="state">The trigger's current arm state.</param>
    /// <param name="evaluation">The fresh condition evaluation.</param>
    /// <returns>The next state to persist and the action to perform.</returns>
    public static TriggerDecision Decide(TriggerArmState state, ConditionEvaluation evaluation)
    {
        // Can't measure -> hold everything. Never fire, never re-arm, never seed on absence.
        if (evaluation is ConditionEvaluation.Unmeasurable or ConditionEvaluation.Indeterminate)
        {
            return new TriggerDecision(state, TriggerAction.None);
        }

        bool satisfied = evaluation == ConditionEvaluation.Satisfied;

        return state switch
        {
            // Seed silently: a trigger created (or edited) while the condition is already true must NOT fire on
            // first sight — that is a state we are learning, not a crossing we witnessed.
            TriggerArmState.Unseeded => satisfied
                ? new TriggerDecision(TriggerArmState.Fired, TriggerAction.None)
                : new TriggerDecision(TriggerArmState.Armed, TriggerAction.None),

            // The arming edge: armed, and now satisfied -> fire, exactly once.
            TriggerArmState.Armed => satisfied
                ? new TriggerDecision(TriggerArmState.Fired, TriggerAction.Fire)
                : new TriggerDecision(TriggerArmState.Armed, TriggerAction.None),

            // Debounce while it stays true; the opposite edge re-arms and resolves the open incident, so the next
            // crossing is reported as the new incident it is.
            TriggerArmState.Fired => satisfied
                ? new TriggerDecision(TriggerArmState.Fired, TriggerAction.None)
                : new TriggerDecision(TriggerArmState.Armed, TriggerAction.Resolve),

            _ => new TriggerDecision(state, TriggerAction.None),
        };
    }
}
