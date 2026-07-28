namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The outcome of one debounce step (gh#385): the state to persist next, and the side effect to perform. Returned
/// by <see cref="TriggerDebounce.Decide"/>.
/// </summary>
/// <param name="NextState">The arm state to persist after this evaluation.</param>
/// <param name="Action">The side effect the evaluator must perform (send, resolve, or nothing).</param>
public readonly record struct TriggerDecision(TriggerArmState NextState, TriggerAction Action);
