namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The debounce state of a standing trigger (ADR-0008) — the memory of "were we above or below last scan" that
/// turns a level condition into an edge-triggered, fire-once alert.
/// </summary>
/// <remarks>
/// <b>Three states are required, not two.</b> A two-state Armed / Fired model cannot tell "never observed, and
/// already true" apart from "was false, now true", so a trigger created while its condition is <i>already</i> true
/// (RSI already below 30) would fire on the very first scan — an alert on no observed crossing.
/// <see cref="Unseeded"/> is the seed state that adopts pre-existing truth <b>silently</b>: it moves straight to
/// <see cref="Fired"/> without firing, so the first alert only ever comes from an observed edge.
/// <para>
/// <see cref="Unseeded"/> is deliberately the zero value: a defaulted or reset state re-seeds silently — it fails
/// toward <i>no alert</i>, which is the fail-closed direction for an authoring / cold-start path.
/// </para>
/// </remarks>
public enum TriggerArmState
{
    /// <summary>Never evaluated since creation / re-enable / edit — the next scan seeds silently (adopts current truth, no fire).</summary>
    Unseeded = 0,

    /// <summary>The condition was last seen unsatisfied — the next satisfied reading is a crossing edge and fires.</summary>
    Armed = 1,

    /// <summary>The condition was last seen satisfied and has already fired — held (debounced) until it clears and re-arms.</summary>
    Fired = 2,
}
