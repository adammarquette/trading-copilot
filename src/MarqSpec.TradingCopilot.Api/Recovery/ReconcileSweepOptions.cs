namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The runtime reconcile-strand sweep's cadence and age bound (gh#722, ADR-0013). Bound from the
/// <c>ReconcileSweep</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The sweep is a <b>detection</b> pass: it finds an order left <c>Taking</c> or a conditional left <c>Firing</c>
/// past the age bound and raises an operator alert so the human reconciles it (via the existing
/// <c>POST /orders/{id}/reconcile</c> / <c>POST /conditionals/{id}/reconcile</c>). It transmits nothing and changes
/// no order state, so it is <b>on by default</b> like the restart rehydrator it complements — a strand is a
/// maybe-live order with no journal behind it, and leaving it unsurfaced is the risk this closes.
/// </para>
/// <para>
/// The values are validated on start (<c>Program.cs</c>, the <c>SuggestionOptions</c> idiom) so a nonsensical
/// cadence fails the host once rather than every pass.
/// </para>
/// </remarks>
public sealed class ReconcileSweepOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "ReconcileSweep";

    /// <summary>
    /// Whether the sweep runs. On by default — it is a read-only safety detection that never acts, so disabling it
    /// is a deliberate, configured choice, never a silent omission (the auto-flatten / rehydration posture).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How often the sweep scans for strands. Shorter than <see cref="StrandAgeThresholdSeconds"/> so a strand is
    /// caught promptly once it ages, cheap enough to run always (the suggestion-expire sweep's 30 s cadence).
    /// </summary>
    public int SweepIntervalSeconds { get; init; } = 30;

    /// <summary>The sweep cadence as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(SweepIntervalSeconds);

    /// <summary>
    /// How long an intent must persist <c>Taking</c> / <c>Firing</c> before it is alertable, in seconds. Both are
    /// transient at runtime — a take request or a firing pass resolves them within one operation — so a strand still
    /// present after this window is stuck, not in-flight. ~2 min by default (the watchdog-grace scale), long enough
    /// that a slow-but-live transmit is never mistaken for a strand.
    /// </summary>
    public int StrandAgeThresholdSeconds { get; init; } = 120;

    /// <summary>The strand age bound as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan StrandAgeBound => TimeSpan.FromSeconds(StrandAgeThresholdSeconds);
}
