namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The side effect a debounce transition calls for (gh#385). The evaluator turns this into an alert send / resolve;
/// the state machine itself stays pure.
/// </summary>
public enum TriggerAction
{
    /// <summary>Nothing to do — a hold, a silent seed, or a debounced repeat. The zero value.</summary>
    None = 0,

    /// <summary>Fire: send the mechanical alert and journal the firing. Only on the arming edge (Armed → Fired).</summary>
    Fire = 1,

    /// <summary>Re-arm: resolve the open incident so a later crossing is a new one. On the opposite edge (Fired → Armed).</summary>
    Resolve = 2,
}
