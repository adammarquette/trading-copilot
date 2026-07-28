namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>The outcome of a debounce step: the next arm state, and whether this step fires or re-arms.</summary>
/// <param name="NextState">The arm state to persist after this reading.</param>
/// <param name="ShouldFire">Whether this reading is the arming edge that fires a mechanical alert.</param>
/// <param name="ReArmed">Whether this reading is the opposite edge that re-arms (resolve the incident, bump the cycle).</param>
public readonly record struct TriggerDecision(TriggerArmState NextState, bool ShouldFire, bool ReArmed);

/// <summary>
/// The pure debounce state machine of a standing trigger (ADR-0008) — turns a level condition into a fire-once,
/// edge-triggered alert. Like <c>ConditionalOrder.ShouldFire</c>, the transition logic is pure; the resulting
/// <see cref="TriggerArmState"/> is what the caller persists.
/// </summary>
/// <remarks>
/// The whole matrix, and why each transition:
/// <list type="bullet">
/// <item><b>Any + Unmeasurable / Indeterminate → hold.</b> A null indicator or a hysteresis dead-band reading never
/// fires and never re-arms — a momentary gap on a <see cref="TriggerArmState.Fired"/> trigger cannot collapse to
/// "not satisfied" and then spuriously re-fire when the value returns still-satisfied.</item>
/// <item><b>Unseeded + Satisfied → Fired (no fire).</b> Adopt pre-existing truth <i>silently</i>: a trigger created
/// while already satisfied must not fire on its first scan.</item>
/// <item><b>Armed + Satisfied → Fired (FIRE).</b> The arming edge — the one crossing that fires.</item>
/// <item><b>Fired + Satisfied → Fired (no fire).</b> Debounce: a continuously-satisfied condition fires once.</item>
/// <item><b>Fired + NotSatisfied → Armed (RE-ARM).</b> The opposite edge — resolve the incident and re-arm.</item>
/// </list>
/// </remarks>
public static class TriggerDebounce
{
    /// <summary>Decides the next state and any edge action from the current state and this reading.</summary>
    /// <param name="state">The persisted arm state before this reading.</param>
    /// <param name="satisfaction">The condition's reading of the resolved indicator value.</param>
    /// <returns>The next state and whether to fire or re-arm.</returns>
    public static TriggerDecision Decide(TriggerArmState state, ConditionSatisfaction satisfaction) =>
        satisfaction switch
        {
            // A null indicator or the hysteresis dead-band: hold everything, fail-closed.
            ConditionSatisfaction.Unmeasurable or ConditionSatisfaction.Indeterminate =>
                new TriggerDecision(state, ShouldFire: false, ReArmed: false),

            ConditionSatisfaction.Satisfied => state switch
            {
                TriggerArmState.Unseeded => new TriggerDecision(TriggerArmState.Fired, false, false), // seed silently
                TriggerArmState.Armed => new TriggerDecision(TriggerArmState.Fired, ShouldFire: true, false), // fire
                TriggerArmState.Fired => new TriggerDecision(TriggerArmState.Fired, false, false), // debounce
                _ => new TriggerDecision(state, false, false),
            },

            ConditionSatisfaction.NotSatisfied => state switch
            {
                TriggerArmState.Unseeded => new TriggerDecision(TriggerArmState.Armed, false, false), // seed
                TriggerArmState.Armed => new TriggerDecision(TriggerArmState.Armed, false, false),
                TriggerArmState.Fired => new TriggerDecision(TriggerArmState.Armed, false, ReArmed: true), // re-arm
                _ => new TriggerDecision(state, false, false),
            },

            _ => new TriggerDecision(state, false, false),
        };
}
