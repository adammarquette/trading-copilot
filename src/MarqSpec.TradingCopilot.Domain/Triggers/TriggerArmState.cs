namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The debounce state of a trigger (gh#385). A three-state machine — the two-state version has a cold-start bug:
/// a trigger created while its condition is <i>already</i> true would fire on the first scan, reporting a crossing
/// that never happened.
/// </summary>
public enum TriggerArmState
{
    /// <summary>
    /// Never evaluated (freshly created, or just edited / re-enabled). The zero value. The first measurement
    /// <b>seeds silently</b>: pre-existing truth arms-to-fired without firing; pre-existing falsehood arms.
    /// </summary>
    Unseeded = 0,

    /// <summary>Armed and waiting for the arming edge — the next transition into satisfied fires, once.</summary>
    Armed = 1,

    /// <summary>Already fired for the current crossing; debounces while satisfied, re-arms on the opposite edge.</summary>
    Fired = 2,
}
