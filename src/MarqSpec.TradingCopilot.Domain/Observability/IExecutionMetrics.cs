using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Domain.Observability;

/// <summary>Which auto-flatten tier produced a signal (gh#232) — the two must never be merged.</summary>
public enum FlattenTier
{
    /// <summary>The primary scheduler (gh#185).</summary>
    Primary,

    /// <summary>The independent redundant watchdog (gh#187).</summary>
    Watchdog,
}

/// <summary>
/// The execution-SLI sink (gh#232, gh#295) — the seam the enforcing paths record through.
/// </summary>
/// <remarks>
/// <para>
/// A seam in <c>Domain</c> for the same reason <see cref="Events.IEventLog"/> and the notification channel are:
/// the <b>send path lives here</b>, so measuring transmit → acknowledgement is impossible from the Api layer, and
/// Domain must not depend on it. The concrete <c>System.Diagnostics.Metrics</c> implementation stays in the Api
/// composition root, exactly as the Timescale event log stays in Data.
/// </para>
/// <para>
/// <b>Every implementation must be total.</b> Nothing here may throw: a metrics fault must never fail the trading
/// action it was measuring (engineering §9). Callers therefore record without a try/catch, and that is safe by
/// contract rather than by luck.
/// </para>
/// </remarks>
public interface IExecutionMetrics
{
    /// <summary>Counts one gate decision.</summary>
    /// <param name="outcome">The gate's verdict.</param>
    /// <param name="bindingLayer">The layer that bound, or <see langword="null"/> when none did.</param>
    void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer);

    /// <summary>Counts one evaluated flatten deadline — emitted whatever the outcome, including idle.</summary>
    /// <param name="tier">Which tier evaluated it.</param>
    /// <param name="outcome">One of the sink's outcome constants.</param>
    void RecordFlattenDeadline(FlattenTier tier, string outcome);

    /// <summary>Records how long a flatten took to reach confirmed flat.</summary>
    /// <param name="tier">Which tier flattened.</param>
    /// <param name="elapsed">Deadline to flat.</param>
    void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed);

    /// <summary>Records transmit → venue acknowledgement latency.</summary>
    /// <param name="elapsed">The round trip.</param>
    void RecordOrderAck(TimeSpan elapsed);

    /// <summary>Sets whether the kill switch is engaged.</summary>
    /// <param name="engaged">Whether it is engaged.</param>
    void SetKillSwitchEngaged(bool engaged);

    /// <summary>Sets how many working stops are currently orphaned.</summary>
    /// <param name="count">The count.</param>
    void SetOrphanedStops(int count);

    /// <summary>
    /// Sets the protection census (gh#370): how many positions the venue reports open, and how many of those
    /// have <b>no protective stop resting at the venue</b>.
    /// </summary>
    /// <remarks>
    /// <paramref name="unprotected"/> is <b>ADR-0019's P1</b> — a live position with nothing exchange-held behind
    /// it is the state the staged-stop model exists to prevent, and nothing measured it before. It is distinct
    /// from <see cref="SetOrphanedStops"/>, which counts a stop that <i>was</i> venue-held and was orphaned on a
    /// connection drop: a known, handled transition rather than unaccounted exposure.
    /// <paramref name="open"/> supplies the "…with exposure" qualifier that several ADR-0019 conditions need.
    /// </remarks>
    /// <param name="open">Positions with non-zero net exposure.</param>
    /// <param name="unprotected">Of those, how many have no resting stop on the same contract.</param>
    void SetPositionProtection(int open, int unprotected);

    /// <summary>
    /// Sets whether the venue connection is up (gh#370). A gauge rather than an event, so a rule can express
    /// ADR-0019's <i>"connection lost &gt; 2 min"</i> as a duration over the series rather than the app having to
    /// track elapsed time itself.
    /// </summary>
    /// <param name="connected">Whether the venue connection is currently up.</param>
    void SetVenueConnected(bool connected);

    /// <summary>Counts one retention gap observed by a consumer (gh#227).</summary>
    /// <param name="consumerGroup">The consumer group whose cursor fell behind.</param>
    void RecordRetentionGap(string consumerGroup);

    /// <summary>Records append → consume delay for a consumer group.</summary>
    /// <param name="consumerGroup">The consumer group.</param>
    /// <param name="lag">The delay.</param>
    void RecordPipelineLag(string consumerGroup, TimeSpan lag);

    /// <summary>
    /// Records how much of a blind window the bar store could <b>not</b> cover when recovering from a retention
    /// gap (gh#482, gh#306).
    /// </summary>
    /// <remarks>
    /// The consequence, and why this is not a data-quality nicety: hidden stops on that contract may have crossed
    /// their promotion band <b>unobserved</b> and were not recovered, leaving the native safety stop as the only
    /// floor. Emitted only when there is a shortfall — a complete recovery is silent, so a rule reading this can
    /// never fire on a healthy session (ADR-0019 §4).
    /// </remarks>
    /// <param name="contractKey">The venue contract whose window was not fully covered.</param>
    /// <param name="uncovered">How much of the window has no bars in the store.</param>
    void RecordBackfillShortfall(string contractKey, TimeSpan uncovered);

    /// <summary>
    /// Counts one processed <b>flat</b> event's round-trip journaling outcome (gh#731), dimensioned by
    /// <paramref name="outcome"/>. Emitted for every flat that reached the composition stage — a journalled trip, each
    /// way one is <b>refused</b>, and each flat <b>deferred</b> because its closing fill has not yet been ingested
    /// (gh#748) — so an account stuck permanently refusing or deferring is visible to an alert rather than only to a
    /// log line. The daily governor and the R-4 throttle read the rows this writes; if it silently stops producing
    /// them, their headroom silently drifts.
    /// </summary>
    /// <param name="outcome">One of the sink's trade-journal outcome constants (a closed set — never an id).</param>
    void RecordTradeJournalOutcome(string outcome);

    /// <summary>
    /// Counts one stranded pre-transmit intent the runtime reconcile sweep newly detected (gh#722), dimensioned by
    /// <paramref name="kind"/>. Emitted <b>once</b> per strand as it crosses the age bound — a maybe-live order left
    /// <c>Taking</c> or conditional left <c>Firing</c> past its bound, awaiting operator reconcile — so an operator
    /// alert can fire on a strand that a log line alone would let scroll past. The sweep transmits nothing; this is
    /// the detection signal, not an action.
    /// </summary>
    /// <param name="kind">One of the sink's reconcile-strand kind constants (a closed set — never an id).</param>
    void RecordReconcileStrandDetected(string kind);

    /// <summary>
    /// Counts one operator notification the delivery queue <b>refused</b> (gh#1077), dimensioned by
    /// <paramref name="kind"/> — a page it could not accept, or a resolve it could not accept.
    /// </summary>
    /// <remarks>
    /// <b>This is the alerting path reporting its own failure, so it must not be reported only by alerting.</b>
    /// A refused page means Layer 1 — the push the app sends itself — did not go out; a log line for it is
    /// "visible" only to an engineer who goes looking, which is the exact criticism gh#1045 and gh#1051 levelled at
    /// their own <c>LogWarning</c>-only signals. Metering it hands the fact to Layer 2 (the rule engine), whose
    /// entire purpose under ADR-0019 is to cover what Layer 1 cannot self-report — and a queue that is refusing is
    /// precisely a Layer-1 blind spot.
    /// </remarks>
    /// <param name="kind">One of the sink's notification-refusal kind constants (a closed set — never a dedup key).</param>
    void RecordNotificationRefused(string kind);
}

/// <summary>
/// A sink that records nothing — the default where telemetry is not wired (tests, a host that never composed it).
/// </summary>
/// <remarks>
/// Exists so a caller never needs a null check on a metrics dependency. A missing sink must degrade to silence,
/// never to a branch on the trading path.
/// </remarks>
public sealed class NullExecutionMetrics : IExecutionMetrics
{
    /// <summary>The shared instance.</summary>
    public static NullExecutionMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordGateDecision(GateOutcome outcome, RiskLayer? bindingLayer)
    {
    }

    /// <inheritdoc />
    public void SetPositionProtection(int open, int unprotected)
    {
    }

    /// <inheritdoc />
    public void SetVenueConnected(bool connected)
    {
    }

    /// <inheritdoc />
    public void RecordFlattenDeadline(FlattenTier tier, string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordTimeToFlat(FlattenTier tier, TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public void RecordOrderAck(TimeSpan elapsed)
    {
    }

    /// <inheritdoc />
    public void SetKillSwitchEngaged(bool engaged)
    {
    }

    /// <inheritdoc />
    public void SetOrphanedStops(int count)
    {
    }

    /// <inheritdoc />
    public void RecordRetentionGap(string consumerGroup)
    {
    }

    /// <inheritdoc />
    public void RecordPipelineLag(string consumerGroup, TimeSpan lag)
    {
    }

    /// <inheritdoc />
    public void RecordBackfillShortfall(string contractKey, TimeSpan uncovered)
    {
    }

    /// <inheritdoc />
    public void RecordTradeJournalOutcome(string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordReconcileStrandDetected(string kind)
    {
    }

    /// <inheritdoc />
    public void RecordNotificationRefused(string kind)
    {
    }
}
